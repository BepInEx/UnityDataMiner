using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AssetRipper.VersionUtilities;
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
    public class UnityBuild
    {
        public string? Id { get; }

        public UnityVersion Version { get; }

        public string ShortVersion { get; }

        public NuGetVersion NuGetVersion { get; }

        public string ZipFilePath { get; }

        public string NuGetPackagePath { get; }

        public bool IsRunNeeded => !File.Exists(ZipFilePath) || !File.Exists(NuGetPackagePath);

        public string BaseDownloadUrl => Version.GetDownloadUrl() + (Id == null ? string.Empty : $"{Id}/");

        public UnityBuildInfo? Info { get; private set; }

        public UnityBuild(string repositoryPath, string? id, string version)
        {
            Id = id;
            Version = UnityVersion.Parse(version);

            if (Version.Major >= 5 && Id == null)
            {
                throw new Exception("Hash cannot be null after 5.x");
            }

            ShortVersion = Version.ToStringWithoutType();
            NuGetVersion = Version.Type == UnityVersionType.Final
                ? new NuGetVersion(Version.Major, Version.Minor, Version.Build)
                : new NuGetVersion(Version.Major, Version.Minor, Version.Build, Version.Type.ToString().ToLowerInvariant() + "." + Version.TypeNumber);

            ZipFilePath = Path.Combine(repositoryPath, "libraries", $"{(Version.Type == UnityVersionType.Final ? ShortVersion : Version)}.zip");
            NuGetPackagePath = Path.Combine(repositoryPath, "packages", $"{NuGetVersion}.nupkg");
        }

        private static readonly HttpClient _httpClient = new();
        private static readonly SemaphoreSlim _downloadLock = new(1, 1);

        private static readonly UnityVersion _firstLinuxVersion = new(2018, 1, 5);

        public async Task FetchInfoAsync()
        {
            var ini = await _httpClient.GetStringAsync(BaseDownloadUrl + $"unity-{Version}-{(Version >= _firstLinuxVersion ? "linux" : "osx")}.ini");
            var info = UnityBuildInfo.Parse(ini);

            if (info.Unity.Version != null && !info.Unity.Version.Equals(Version))
            {
                throw new Exception($"Build info version is invalid (expected {Version}, got {info.Unity.Version})");
            }

            Info = info;
        }

        public async Task MineAsync()
        {
            if (Version.Major >= 5)
            {
                await FetchInfoAsync();
            }

            var downloadUrl = BaseDownloadUrl + (Info == null
                ? $"UnitySetup-{ShortVersion}.exe"
                : (Info.WindowsMono ?? Info.Unity).Url);

            var tmpDirectory = Path.Combine(Path.GetTempPath(), "UnityDataMiner", Version.ToString());
            Directory.CreateDirectory(tmpDirectory);

            var managedDirectory = Path.Combine(tmpDirectory, "managed");
            Directory.CreateDirectory(managedDirectory);

            var archivePath = Path.Combine(tmpDirectory, Path.GetFileName(downloadUrl));
            var extension = Path.GetExtension(archivePath);

            var archiveDirectory = Path.Combine(tmpDirectory, Path.GetFileNameWithoutExtension(downloadUrl));

            try
            {
                while (true)
                {
                    try
                    {
                        if (File.Exists(archivePath))
                        {
                            Log.Information("[{Version}] Skipping download", Version);
                        }
                        else
                        {
                            Log.Information("[{Version}] Downloading", Version);

                            await _downloadLock.WaitAsync();
                            try
                            {
                                await using (var stream = await _httpClient.GetStreamAsync(downloadUrl))
                                await using (var fileStream = File.OpenWrite(archivePath + ".part"))
                                {
                                    await stream.CopyToAsync(fileStream);
                                }

                                File.Move(archivePath + ".part", archivePath);
                            }
                            finally
                            {
                                _downloadLock.Release();
                            }
                        }

                        break;
                    }
                    catch (IOException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset })
                    {
                        Log.Warning("Failed to download {Version}, waiting 5 seconds before retrying...", Version);
                        await Task.Delay(5000);
                    }
                }

                Log.Information("[{Version}] Extracting", Version);

                var isLegacyDownload = Id == null || Version.Major < 5;
                var isMonolithic = isLegacyDownload || Version.Major == 5 && Version.Minor < 3;

                var monoPath = (isMonolithic, isLegacyDownload) switch
                {
                    (true, true) when Version.Major == 4 && Version.Minor >= 5 => "Data/PlaybackEngines/windowsstandalonesupport/Variations/win64_nondevelopment/Data/Managed",
                    (true, true) => "Data/PlaybackEngines/windows64standaloneplayer/Managed",
                    (true, false) => "Unity/Unity.app/Contents/PlaybackEngines/WindowsStandaloneSupport/Variations/win64_nondevelopment_mono/Data/Managed",
                    (false, true) => throw new Exception("Release can't be both legacy and modular at the same time"),
                    (false, false) => $"Variations/win64{(Version >= new UnityVersion(2021, 2) ? "_player" : "")}_nondevelopment_mono/Data/Managed",
                };

                switch (extension)
                {
                    case ".pkg":
                    {
                        const string payloadName = "Payload~";
                        await SevenZip.ExtractAsync(archivePath, archiveDirectory, payloadName);
                        await SevenZip.ExtractAsync(Path.Combine(archiveDirectory, payloadName), managedDirectory, $"./{monoPath}/*.dll");

                        break;
                    }

                    case ".exe":
                    {
                        await SevenZip.ExtractAsync(archivePath, managedDirectory, $"{monoPath}/*.dll");

                        break;
                    }

                    default: throw new ArgumentOutOfRangeException(nameof(extension), extension, "Unrecognized archive type");
                }

                if (Directory.GetFiles(managedDirectory, "*.dll").Length <= 0)
                {
                    throw new Exception("Managed directory is empty");
                }

                ZipFile.CreateFromDirectory(managedDirectory, ZipFilePath);

                Log.Information("[{Version}] Done, creating NuGet package", Version);

                CreateNuGetPackage(managedDirectory);
            }
            finally
            {
                Directory.Delete(tmpDirectory, true);
            }
        }

        private void CreateNuGetPackage(string pkgDir)
        {
            Log.Information("[{Version}] Stripping assemblies", Version);
            foreach (var file in Directory.EnumerateFiles(pkgDir, "*.dll"))
                AssemblyStripper.StripAssembly(file);

            Log.Information("[{Version}] Packing", Version);

            var deps = new[] { "net35", "net45", "netstandard2.0" };

            var meta = new ManifestMetadata
            {
                Id = "UnityEngine.Modules",
                Authors = new[] { "Unity" },
                Version = NuGetVersion,
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

        public async Task UploadNuGetPackage(string sourceUrl, string apikey)
        {
            Log.Information("[{Version}] Pushing NuGet package", Version);
            var repo = Repository.Factory.GetCoreV3(sourceUrl);
            var updateResource = await repo.GetResourceAsync<PackageUpdateResource>();
            await updateResource.Push(new[] { NuGetPackagePath },
                null,
                2 * 60,
                false,
                s => apikey,
                s => null,
                false,
                true,
                null,
                NullLogger.Instance);
            Log.Information("[{Version}] Done pushing NuGet package", Version);
        }
    }
}
