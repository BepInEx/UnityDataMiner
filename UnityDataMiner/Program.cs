using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Hosting;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using HtmlAgilityPack;
using LibGit2Sharp;
using Microsoft.Extensions.Hosting;
using Serilog;
using Tommy.Extensions.Configuration;

namespace UnityDataMiner
{
    internal static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            Log.Logger = new LoggerConfiguration()
                .WriteTo.Console()
                .CreateBootstrapLogger();

            try
            {
                return await BuildCommandLine()
                    .UseHost(host =>
                    {
                        host.UseConsoleLifetime(options => options.SuppressStatusMessages = true);
                        host.ConfigureAppConfiguration(configuration => configuration.AddTomlFile("config.toml"));
                        host.UseSerilog((context, services, loggerConfiguration) => loggerConfiguration
                            .ReadFrom.Configuration(context.Configuration)
                            .Enrich.FromLogContext()
                            .WriteTo.Console());
                    })
                    .UseDefaults()
                    .UseExceptionHandler((ex, _) => Log.Fatal(ex, "Exception, cannot continue!"), -1)
                    .Build()
                    .InvokeAsync(args);
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        private static CommandLineBuilder BuildCommandLine()
        {
            var root = new RootCommand
            {
                new Argument<string?>("version")
                {
                    Arity = ArgumentArity.ZeroOrOne
                },
                new Option<DirectoryInfo>("--repository", () => new DirectoryInfo(Directory.GetCurrentDirectory()))
            };
            root.Handler = CommandHandler.Create<string?, DirectoryInfo>(RunAsync);
            return new CommandLineBuilder(root);
        }

        public static async Task RunAsync(string? version, DirectoryInfo repository)
        {
            var unityVersions = await FetchUnityVersionsAsync(repository.FullName);

            Log.Information("Found {Count} unity versions", unityVersions.Count);

            var toRun = string.IsNullOrEmpty(version)
                ? unityVersions.Where(unityVersion => unityVersion.IsRunNeeded).ToArray()
                : new[] { unityVersions.Single(x => x.RawVersion == version) };

            Log.Information("Downloading {Count} unity versions", toRun.Length);

            await Task.WhenAll(toRun.Select(unityVersion => Task.Run(async () =>
            {
                try
                {
                    await unityVersion.MakeLibraryZipAsync();
                }
                catch (Exception e)
                {
                    Log.Error(e, "Failed to download {Version}", unityVersion.RawVersion);
                }
            })));

            UpdateGitRepository(repository.FullName, unityVersions);
        }

        private static async Task<List<UnityVersion>> FetchUnityVersionsAsync(string repositoryPath)
        {
            var document = new HtmlDocument();
            using var httpClient = new HttpClient();
            await using var stream = await httpClient.GetStreamAsync("https://unity3d.com/get-unity/download/archive");
            document.Load(stream);

            var unityVersions = new List<UnityVersion>();

            foreach (var link in document.DocumentNode.Descendants("a"))
            {
                var href = link.GetAttributeValue("href", null);
                if (href == null)
                    continue;

                var uri = new Uri(href);
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

                    unityVersions.Add(new UnityVersion(repositoryPath, split[2], split[4][prefix.Length..^exe.Length]));
                }
                else if (innerText == "Unity Editor")
                {
                    const string prefix = "UnitySetup-";

                    if (split.Length == 3 && split[2].StartsWith(prefix))
                    {
                        unityVersions.Add(new UnityVersion(repositoryPath, null, split[2][prefix.Length..^exe.Length]));
                    }
                }
            }

            return unityVersions;
        }

        private static void UpdateGitRepository(string repositoryPath, List<UnityVersion> unityVersions)
        {
            var repository = new Repository(Repository.Init(repositoryPath));

            Commands.Stage(repository, Path.Combine(repositoryPath, "*"));

            var currentCommit = repository.Head.Tip;
            var changes = repository.Diff.Compare<TreeChanges>(currentCommit?.Tree, DiffTargets.WorkingDirectory);

            if (currentCommit == null || changes.Any())
            {
                var addedVersions = new List<UnityVersion>();

                if (changes != null)
                {
                    foreach (var added in changes.Added)
                    {
                        var path = added.Path.Split("/");

                        if (path.Length == 2 && path[0] == "libraries" && path[1].EndsWith(".zip"))
                        {
                            addedVersions.Add(unityVersions.Single(x => x.Version.ToString(3) == path[1][..^".zip".Length]));
                        }
                    }
                }

                var message = new StringBuilder("Automatically mined");

                if (addedVersions.Any())
                {
                    message.AppendLine();
                    message.AppendLine($"Added unity libraries for {string.Join(", ", addedVersions.OrderBy(x => x.Version).Select(x => x.RawVersion))}");
                }

                var author = new Signature("UnityDataMiner", "UnityDataMiner@bepinex.dev", DateTimeOffset.Now);
                var commit = repository.Commit(message.ToString(), author, author);

                Log.Information("Committed {Sha}", commit.Sha);

                // Shell out to git for SSH support
                Process.Start(new ProcessStartInfo("git", "push")
                {
                    WorkingDirectory = repositoryPath
                })!.WaitForExit();
                Log.Information("Pushed!");
            }
            else
            {
                Log.Information("No git changes found");
            }
        }
    }
}
