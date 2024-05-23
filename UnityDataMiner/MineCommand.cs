using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AssetRipper.Primitives;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace UnityDataMiner;

public partial class MineCommand : RootCommand
{
    [GeneratedRegex(@"unityhub:\/\/(?<version>[\d.\w]+)\/(?<id>[0-9a-f]+)")]
    private static partial Regex UnityHubLinkRegex();

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
            Directory.CreateDirectory(Path.Combine(Repository.FullName, "mono"));
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
            var unityVersions = new Dictionary<UnityVersion, UnityBuild>();
            await FetchUnityVersionsAsync(repositoryPath, unityVersions, "releases/editor/archive");
            await FetchUnityVersionsAsync(repositoryPath, unityVersions, "releases/editor/releases.xml");
            await FetchUnityVersionsAsync(repositoryPath, unityVersions, "releases/editor/beta/latest.xml");
            await FetchUnityVersionsAsync(repositoryPath, unityVersions, "releases/editor/lts-releases.xml");
            await FillMissingUnityVersionsAsync(repositoryPath, unityVersions);
            return unityVersions.Values.ToList();
        }

        private async Task FetchUnityVersionsAsync(string repositoryPath, Dictionary<UnityVersion, UnityBuild> unityVersions, string path)
        {
            var httpClient = _clientFactory.CreateClient("unity");
            var text = await httpClient.GetStringAsync(path);

            var count = 0;

            foreach (Match match in UnityHubLinkRegex().Matches(text))
            {
                var unityVersion = UnityVersion.Parse(match.Groups["version"].Value);

                if (!unityVersions.ContainsKey(unityVersion))
                {
                    unityVersions.Add(unityVersion, new UnityBuild(repositoryPath, match.Groups["id"].Value, unityVersion));
                    count++;
                }
            }

            _logger.LogInformation("Found {Count} new unity versions in {Path}", count, path);
        }

        private async Task FillMissingUnityVersionsAsync(string repositoryPath, Dictionary<UnityVersion, UnityBuild> unityVersions)
        {
            var versionsCachePath = Path.Combine(repositoryPath, "versions");
            if (!Directory.Exists(versionsCachePath)) return;

            var count = 0;

            await Parallel.ForEachAsync(Directory.GetDirectories(versionsCachePath), async (versionCachePath, token) =>
            {
                var unityBuildInfo = UnityBuildInfo.Parse(await File.ReadAllTextAsync(Path.Combine(versionCachePath, "win.ini"), token));
                if (unityBuildInfo.Unity.Title == "Unity 5") return;

                var unityVersion = UnityVersion.Parse(unityBuildInfo.Unity.Title.Replace("Unity ", ""));

                if (!unityVersions.ContainsKey(unityVersion))
                {
                    lock (unityVersions)
                    {
                        unityVersions.Add(unityVersion, new UnityBuild(repositoryPath, Path.GetFileName(versionCachePath), unityVersion));
                        count++;
                    }
                }
            });

            _logger.LogInformation("Filled {Count} unity versions", count);
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
