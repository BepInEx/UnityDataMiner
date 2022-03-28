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
        Add(new Option<bool>("--download-corlibs", () => false));
    }

    public new class Handler : ICommandHandler
    {
        private readonly ILogger<Handler> _logger;
        private readonly MinerOptions _minerOptions;

        public string? Version { get; init; }
        public DirectoryInfo Repository { get; init; }
        public bool DownloadCorlibs { get; init; }

        public Handler(ILogger<Handler> logger, IOptions<MinerOptions> minerOptions)
        {
            _logger = logger;
            _minerOptions = minerOptions.Value;
        }

        public async Task<int> InvokeAsync(InvocationContext context)
        {
            var token = context.GetCancellationToken();

            Directory.CreateDirectory(Path.Combine(Repository.FullName, "libraries"));
            Directory.CreateDirectory(Path.Combine(Repository.FullName, "packages"));
            Directory.CreateDirectory(Path.Combine(Repository.FullName, "corlibs"));
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
                    await unityVersion.MineAsync(DownloadCorlibs, cancellationToken);
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
            // TODO fill missing with versions/*.ini (make sure to take only latest final versions)
            return unityVersions;
        }

        private async Task<List<UnityBuild>> FetchStableUnityVersionsAsync(string repositoryPath)
        {
            var document = new HtmlDocument();
            using var httpClient = new HttpClient();
            await using var stream = await httpClient.GetStreamAsync("https://unity3d.com/get-unity/download/archive");
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
            using var httpClient = new HttpClient();
            using var xmlReader = XmlReader.Create(await httpClient.GetStreamAsync("https://unity3d.com/unity/beta/latest.xml"));
            var feedReader = new RssFeedReader(xmlReader);

            var unityVersions = new List<UnityBuild>();

            while (await feedReader.Read())
            {
                if (feedReader.ElementType == SyndicationElementType.Item)
                {
                    var item = await feedReader.ReadItem();

                    if (item.Title.StartsWith("Release "))
                    {
                        unityVersions.Add(new UnityBuild(repositoryPath, item.Id, item.Title["Release ".Length..]));
                    }
                }
            }

            _logger.LogInformation("Found {Count} beta unity versions", unityVersions.Count);

            return unityVersions;
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
                var mono = new HashSet<UnityBuild>();
                var android = new HashSet<UnityBuild>();

                if (changes != null)
                {
                    foreach (var added in changes.Added)
                    {
                        var path = added.Path;

                        if (path.StartsWith("libraries/"))
                        {
                            mono.Add(unityVersions.Single(x => x.ZipFilePath.EndsWith(added.Path)));
                        }
                        else if (path.StartsWith("android/"))
                        {
                            android.Add(unityVersions.Single(x => x.AndroidPath.EndsWith(string.Join("/", added.Path.Split("/").Take(3)))));
                        }
                    }
                }

                _logger.LogInformation("Compared");

                var message = new StringBuilder("Automatically mined");

                if (mono.Any() || android.Any())
                {
                    message.AppendLine();

                    if (mono.Any())
                    {
                        message.AppendLine($"Added unity libraries for {string.Join(", ", mono.OrderBy(x => x.Version).Select(x => x.Version))}");
                    }

                    if (android.Any())
                    {
                        message.AppendLine($"Added android binaries for {string.Join(", ", android.OrderBy(x => x.Version).Select(x => x.Version))}");
                    }
                }

                var author = new Signature("UnityDataMiner", "UnityDataMiner@bepinex.dev", DateTimeOffset.Now);

                _logger.LogInformation("Committing");

                var commit = repository.Commit(message.ToString(), author, author);

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
