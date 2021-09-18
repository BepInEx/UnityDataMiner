using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Serilog;

namespace UnityDataMiner
{
    public class UnityVersion
    {
        public string? Hash { get; }

        public string RawVersion { get; }

        public Version Version { get; }

        public string ShortVersion => Version.ToString(3);

        public string ZipFilePath { get; }
        
        public string NuGetPackagePath { get; }
        
        public bool IsRunNeeded => !File.Exists(ZipFilePath);

        public UnityVersion(string repositoryPath, string? hash, string rawVersion)
        {
            Hash = hash;
            RawVersion = rawVersion;

            Version = Version.Parse(RawVersion.Replace("f", "."));
            ZipFilePath = Path.Combine(repositoryPath, "libraries", $"{ShortVersion}.zip");
            NuGetPackagePath = Path.Combine(repositoryPath, "packages", $"{ShortVersion}.nupkg");
        }

        private static readonly SemaphoreSlim _downloadLock = new(1, 1);

        public async Task MakeLibraryZipAsync()
        {
            var isLegacyDownload = Hash == null || Version.Major < 5;
            var isMonolithic = isLegacyDownload || Version.Major == 5 && Version.Minor < 3;

            var downloadUrl = (isMonolithic, isLegacyDownload) switch
            {
                (true, true) => $"https://download.unity3d.com/download_unity/UnitySetup-{RawVersion}.exe",
                (true, false) => $"https://beta.unity3d.com/download/{Hash}/MacEditorInstaller/Unity-{RawVersion}.pkg", 
                _ => $"https://beta.unity3d.com/download/{Hash}/MacEditorTargetInstaller/UnitySetup-Windows{(Version.Major >= 2018 ? "-Mono" : "")}-Support-for-Editor-{RawVersion}.pkg"
            };
            
            await _downloadLock.WaitAsync();
            using var httpClient = new HttpClient();

            var tmpDirectory = Path.Combine(Path.GetTempPath(), "UnityDataMiner", RawVersion);
            var pkgPath = Path.Combine(tmpDirectory, $"{RawVersion}.pkg");

            try
            {
                Directory.CreateDirectory(tmpDirectory);
                Log.Information("[{Version}] Downloading", RawVersion);

                await using var stream = await httpClient.GetStreamAsync(downloadUrl);
                await using var fileStream = File.OpenWrite(pkgPath);
                await stream.CopyToAsync(fileStream);
            }
            catch (IOException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset })
            {
                Log.Warning("Failed to download {Version}, waiting 5 seconds before retrying...", RawVersion);
                await Task.Delay(5000);
                _downloadLock.Release();
                await MakeLibraryZipAsync();
                return;
            }

            _downloadLock.Release();

            Log.Information("[{Version}] Extracting", RawVersion);

            var monoPath = (isMonolithic, isLegacyDownload) switch
            {
                (true, true) when Version.Major == 4 && Version.Minor >= 5 => "Data/PlaybackEngines/windowsstandalonesupport/Variations/win64_nondevelopment/Data/Managed",
                (true, true) => "Data/PlaybackEngines/windows64standaloneplayer/Managed",
                (true, false) => "Unity/Unity.app/Contents/PlaybackEngines/WindowsStandaloneSupport/Variations/win64_nondevelopment_mono/Data/Managed",
                _ => "Variations/win64_nondevelopment_mono/Data/Managed"
            };
            
            // I'm way too lazy to write c# wrappers for both 7zip (XAR) and cpio
            await Process.Start(new ProcessStartInfo("7z")
            {
                ArgumentList =
                {
                    "x", // extract
                    "-y", // assume Yes on all queries
                    pkgPath, // source
                    $"-o{tmpDirectory}", // output
                    isLegacyDownload ? $"{monoPath}/*.dll" : "Payload~" // file filter
                },
                RedirectStandardOutput = true
            })!.WaitForExitAsync();

            if (!isLegacyDownload)
            {
                await Process.Start(new ProcessStartInfo("cpio")
                {
                    ArgumentList =
                    {
                        "--quiet",
                        "--extract",
                        "--unconditional",
                        "--make-directories",
                        "-I", Path.Combine(tmpDirectory, "Payload~"),
                        "-D", tmpDirectory,
                        $"./{monoPath}/*.dll"
                    }
                })!.WaitForExitAsync();
            }

            Directory.GetParent(ZipFilePath)!.Create();
            ZipFile.CreateFromDirectory(Path.Combine(tmpDirectory, monoPath), ZipFilePath);

            Log.Information("[{Version}] Done, creating NuGet package", RawVersion);

            CreateNuGetPackage(Path.Combine(tmpDirectory, monoPath));
            
            Directory.Delete(tmpDirectory, true);
        }

        public async Task UploadNuGetPackage(string sourceUrl, string apikey)
        {
            Log.Information("[{Version}] Pushing NuGet package", RawVersion);
            var repo = Repository.Factory.GetCoreV3(sourceUrl);
            var updateResource = await repo.GetResourceAsync<PackageUpdateResource>();
            await updateResource.Push(new [] { NuGetPackagePath },
                null,
                2 * 60,
                false,
                s => apikey,
                s => null,
                false,
                true,
                null,
                NullLogger.Instance);
            Log.Information("[{Version}] Done pushing NuGet package", RawVersion);
        }

        private void CreateNuGetPackage(string pkgDir)
        {
            Log.Information("[{Version}] Stripping assemblies", RawVersion);
            foreach (var file in Directory.EnumerateFiles(pkgDir, "*.dll"))
                AssemblyStripper.StripAssembly(file);
            
            Log.Information("[{Version}] Packing", RawVersion);

            var deps = new[] { "net35", "net45", "netstandard2.0" };
            
            var meta = new ManifestMetadata
            {
                Id = "UnityEngine.Modules",
                Authors = new [] { "Unity" },
                Version = new NuGetVersion(Version),
                Description = "UnityEngine modules",
                DevelopmentDependency = true,
                DependencyGroups = deps.Select(d => new PackageDependencyGroup(NuGetFramework.Parse(d), Array.Empty<PackageDependency>()))
            };

            var builder = new PackageBuilder();
            builder.PopulateFiles(pkgDir, deps.Select(d => new ManifestFile
            {
                Source = "*.dll",
                Target = $"lib/{d}"
            }));
            builder.Populate(meta);
            using var fs = File.Create(NuGetPackagePath);
            builder.Save(fs);
        }
    }
}
