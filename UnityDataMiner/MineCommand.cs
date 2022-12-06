using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using AssetRipper.VersionUtilities;
using HtmlAgilityPack;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SyndicationFeed;
using Microsoft.SyndicationFeed.Rss;

namespace UnityDataMiner;

public class MineCommand : RootCommand
{
    public MineCommand()
    {
        Add(new Argument<string?>("version")
        {
            Arity = ArgumentArity.ZeroOrOne,
        });
        Add(new Option<DirectoryInfo>("--repository", () => new DirectoryInfo(Directory.GetCurrentDirectory())));
    }

    public new class Handler : ICommandHandler
    {
        private readonly ILogger<Handler> _logger;
        private readonly MinerOptions _minerOptions;
        private readonly IHttpClientFactory _clientFactory;

        public string? Version { get; init; }
        public DirectoryInfo Repository { get; init; }

        public Handler(ILogger<Handler> logger, IOptions<MinerOptions> minerOptions, IHttpClientFactory clientFactory)
        {
            _logger = logger;
            _minerOptions = minerOptions.Value;
            _clientFactory = clientFactory;
        }

        public async Task<int> InvokeAsync(InvocationContext context)
        {
            await SevenZip.EnsureInstalled();
            await EuUnstrip.EnsureInstalled();
            
            var token = context.GetCancellationToken();

            Directory.CreateDirectory(Path.Combine(Repository.FullName, "libraries"));
            Directory.CreateDirectory(Path.Combine(Repository.FullName, "packages"));
            Directory.CreateDirectory(Path.Combine(Repository.FullName, "corlibs"));
            Directory.CreateDirectory(Path.Combine(Repository.FullName, "libil2cpp-source"));
            Directory.CreateDirectory(Path.Combine(Repository.FullName, "android"));
            Directory.CreateDirectory(Path.Combine(Repository.FullName, "versions"));

            var unityVersions = await FetchUnityVersionsAsync(Repository.FullName);

            await Parallel.ForEachAsync(unityVersions.Where(x => x.Version.Major >= 5 && x.NeedsInfoFetch), token, async (unityVersion, cancellationToken) =>
            {
                try
                {
                    await unityVersion.FetchInfoAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Failed to fetch info for {Version}", unityVersion.Version);
                }
            });

            var toRun = string.IsNullOrEmpty(Version)
                ? unityVersions.Where(unityVersion => unityVersion.IsRunNeeded).ToArray()
                : new[] { unityVersions.Single(x => x.ShortVersion == Version) };

            _logger.LogInformation("Mining {Count} unity versions", toRun.Length);

            await Parallel.ForEachAsync(toRun, token, async (unityVersion, cancellationToken) =>
            {
                try
                {
                    await unityVersion.MineAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Failed to download {Version}", unityVersion.Version);
                }
            });

            if (!string.IsNullOrEmpty(_minerOptions.NuGetSource) && !string.IsNullOrEmpty(_minerOptions.NuGetSourceKey))
                await Task.WhenAll(toRun.Select(unityVersion => Task.Run(async () =>
                {
                    try
                    {
                        await unityVersion.UploadNuGetPackageAsync(_minerOptions.NuGetSource, _minerOptions.NuGetSourceKey);
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "Failed to download {Version}", unityVersion.Version);
                    }
                }, token)));
            else
                _logger.LogInformation("Skipping pushing NuGet packages (no package config specified)");

            UpdateGitRepository(Repository.FullName, unityVersions);

            return 0;
        }

        private async Task<List<UnityBuild>> FetchUnityVersionsAsync(string repositoryPath)
        {
            var unityVersions = new List<UnityBuild>();
            unityVersions.AddRange(await FetchStableUnityVersionsAsync(repositoryPath));
            unityVersions.AddRange(await FetchBetaUnityVersionsAsync(repositoryPath));
            await FillMissingUnityVersionsAsync(repositoryPath, unityVersions);
            return unityVersions;
        }

        private async Task<List<UnityBuild>> FetchStableUnityVersionsAsync(string repositoryPath)
        {
            var document = new HtmlDocument();
            var httpClient = _clientFactory.CreateClient("unity");
            await using var stream = await httpClient.GetStreamAsync("get-unity/download/archive");
            document.Load(stream);

            var unityVersions = new List<UnityBuild>();

            foreach (var link in document.DocumentNode.Descendants("a"))
            {
                var href = link.GetAttributeValue("href", null);
                if (href == null)
                    continue;

                if (!Uri.TryCreate(href, UriKind.Absolute, out var uri))
                    continue;
                var split = uri.AbsolutePath.Split("/");

                if (uri.Host != "download.unity3d.com" || split[1] != "download_unity")
                {
                    continue;
                }

                var innerText = link.InnerText.Trim();

                const string exe = ".exe";

                if (innerText == "Unity Editor 64-bit")
                {
                    const string prefix = "UnitySetup64-";

                    if (split[3] != "Windows64EditorInstaller" || !split[4].StartsWith(prefix))
                    {
                        throw new Exception("Invalid download link for " + href);
                    }

                    unityVersions.Add(new UnityBuild(repositoryPath, split[2], split[4][prefix.Length..^exe.Length]));
                }
                else if (innerText == "Unity Editor")
                {
                    const string prefix = "UnitySetup-";

                    if (split.Length == 3 && split[2].StartsWith(prefix))
                    {
                        unityVersions.Add(new UnityBuild(repositoryPath, null, split[2][prefix.Length..^exe.Length]));
                    }
                }
            }

            _logger.LogInformation("Found {Count} stable unity versions", unityVersions.Count);

            return unityVersions;
        }

        private async Task<List<UnityBuild>> FetchBetaUnityVersionsAsync(string repositoryPath)
        {
            var httpClient = _clientFactory.CreateClient("unity");
            using var xmlReader = XmlReader.Create(await httpClient.GetStreamAsync("unity/beta/latest.xml"));
            var feedReader = new RssFeedReader(xmlReader);

            var unityVersions = new List<UnityBuild>();

            while (await feedReader.Read())
            {
                if (feedReader.ElementType == SyndicationElementType.Item)
                {
                    var item = await feedReader.ReadItem();

                    // TODO: Do we still need Release prefix? Seems like it's not present anymore
                    if (item.Title.StartsWith("Release "))
                    {
                        unityVersions.Add(new UnityBuild(repositoryPath, item.Id, item.Title["Release ".Length..]));
                    }
                    else if (UnityVersionUtils.TryParse(item.Title, out _))
                    {
                        unityVersions.Add(new UnityBuild(repositoryPath, item.Id, item.Title));
                    }
                }
            }

            _logger.LogInformation("Found {Count} beta unity versions", unityVersions.Count);

            return unityVersions;
        }

        private async Task FillMissingUnityVersionsAsync(string repositoryPath, List<UnityBuild> unityVersions)
        {
            var versionsCachePath = Path.Combine(repositoryPath, "versions");
            if (!Directory.Exists(versionsCachePath)) return;

            var filledUnityVersions = new List<UnityBuild>();

            await Parallel.ForEachAsync(Directory.GetDirectories(versionsCachePath), async (versionCachePath, token) =>
            {
                var unityBuildInfo = UnityBuildInfo.Parse(await File.ReadAllTextAsync(Path.Combine(versionCachePath, "win.ini"), token));
                if (unityBuildInfo.Unity.Title == "Unity 5") return;

                var rawUnityVersion = unityBuildInfo.Unity.Title.Replace("Unity ", "");
                var unityVersion = UnityVersion.Parse(rawUnityVersion);

                if (unityVersions.All(x => x.Version != unityVersion))
                {
                    filledUnityVersions.Add(new UnityBuild(repositoryPath, Path.GetFileName(versionCachePath), rawUnityVersion));
                }
            });

            unityVersions.AddRange(filledUnityVersions);
            _logger.LogInformation("Filled {Count} unity versions", filledUnityVersions.Count);
        }

        private void UpdateGitRepository(string repositoryPath, List<UnityBuild> unityVersions)
        {
            _logger.LogInformation("Initialising the git repository");

            var repository = new Repository(LibGit2Sharp.Repository.Init(repositoryPath));

            _logger.LogInformation("Staging");

            Commands.Stage(repository, Path.Combine(repositoryPath, "*"));

            _logger.LogInformation("Comparing");

            var currentCommit = repository.Head.Tip;
            var changes = repository.Diff.Compare<TreeChanges>(currentCommit?.Tree, DiffTargets.WorkingDirectory);

            if (currentCommit == null || changes.Any())
            {
                var author = new Signature("UnityDataMiner", "UnityDataMiner@bepinex.dev", DateTimeOffset.Now);

                _logger.LogInformation("Committing");

                var commit = repository.Commit("Automatically mined", author, author);

                _logger.LogInformation("Committed {Sha}", commit.Sha);

                // Shell out to git for SSH support
                var pushProcess = Process.Start(new ProcessStartInfo("git", "push")
                {
                    WorkingDirectory = repositoryPath,
                    RedirectStandardError = true,
                }) ?? throw new Exception("Failed to start git push");
                pushProcess.WaitForExit();

                if (pushProcess.ExitCode == 0)
                {
                    _logger.LogInformation("Pushed!");
                }
                else
                {
                    _logger.LogError("Failed to push\n{Error}", pushProcess.StandardError.ReadToEnd());
                }
            }
            else
            {
                _logger.LogInformation("No git changes found");
            }
        }
    }
}
