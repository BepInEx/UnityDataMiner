using System;
using System.Collections.Generic;
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

        public string AndroidPath { get; }

        public string NuGetPackagePath { get; }

        public string InfoCachePath { get; }

        public bool IsRunNeeded => !File.Exists(ZipFilePath) || !File.Exists(NuGetPackagePath) || !Version.IsMonolithic() && !File.Exists(AndroidPath);

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
                : new NuGetVersion(Version.Major, Version.Minor, Version.Build, Version.Type switch
                {
                    UnityVersionType.Alpha => "alpha",
                    UnityVersionType.Beta => "beta",
                    UnityVersionType.China => "china",
                    UnityVersionType.Final => "final",
                    UnityVersionType.Patch => "patch",
                    UnityVersionType.Experimental => "experimental",
                    _ => throw new ArgumentOutOfRangeException(nameof(Version.Type), Version.Type, "Invalid Version.Type for " + Version),
                } + "." + Version.TypeNumber);

            var zipName = $"{(Version.Type == UnityVersionType.Final ? ShortVersion : Version)}.zip";
            ZipFilePath = Path.Combine(repositoryPath, "libraries", zipName);
            AndroidPath = Path.Combine(repositoryPath, "android", zipName);
            NuGetPackagePath = Path.Combine(repositoryPath, "packages", $"{NuGetVersion}.nupkg");
            InfoCachePath = Path.Combine(repositoryPath, "versions", $"{id}.ini");
        }

        private static readonly HttpClient _httpClient = new();
        private static readonly SemaphoreSlim _downloadLock = new(2, 2);

        private static readonly UnityVersion _firstLinuxVersion = new(2018, 1, 5);

        public async Task FetchInfoAsync(CancellationToken cancellationToken)
        {
            var ini = await _httpClient.GetStringAsync(BaseDownloadUrl + $"unity-{Version}-{(Version >= _firstLinuxVersion ? "linux" : "osx")}.ini", cancellationToken);
            var info = UnityBuildInfo.Parse(ini);

            if (info.Unity.Version != null && !info.Unity.Version.Equals(Version))
            {
                throw new Exception($"Build info version is invalid (expected {Version}, got {info.Unity.Version})");
            }

            Info = info;

            await File.WriteAllTextAsync(InfoCachePath, ini, cancellationToken);
        }

        public async Task MineAsync(CancellationToken cancellationToken)
        {
            var monoDownloadUrl = BaseDownloadUrl + (Info == null
                ? $"UnitySetup-{ShortVersion}.exe"
                : (Info.WindowsMono ?? Info.Unity).Url);

            var androidDownloadUrl = Info == null || Version.IsMonolithic() // TODO make monolithic handling better
                ? null
                : BaseDownloadUrl + Info.Android!.Url;

            var tmpDirectory = Path.Combine(Path.GetTempPath(), "UnityDataMiner", Version.ToString());
            Directory.CreateDirectory(tmpDirectory);

            var managedDirectory = Path.Combine(tmpDirectory, "managed");
            var androidDirectory = Path.Combine(tmpDirectory, "android");

            var monoArchivePath = Path.Combine(tmpDirectory, Path.GetFileName(monoDownloadUrl));
            var androidArchivePath = androidDownloadUrl == null ? null : Path.Combine(tmpDirectory, Path.GetFileName(androidDownloadUrl));

            try
            {
                while (true)
                {
                    try
                    {
                        await _downloadLock.WaitAsync(cancellationToken);
                        try
                        {
                            await DownloadAsync(monoDownloadUrl, monoArchivePath, cancellationToken);

                            if (androidDownloadUrl != null)
                            {
                                await DownloadAsync(androidDownloadUrl, androidArchivePath!, cancellationToken);
                            }
                        }
                        finally
                        {
                            if (!cancellationToken.IsCancellationRequested)
                            {
                                _downloadLock.Release();
                            }
                        }

                        break;
                    }
                    catch (IOException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset })
                    {
                        Log.Warning("Failed to download {Version}, waiting 5 seconds before retrying...", Version);
                        await Task.Delay(5000, cancellationToken);
                    }
                }

                if (androidDownloadUrl != null)
                {
                    Log.Information("[{Version}] Extracting android binaries", Version);
                    using var stopwatch = new AutoStopwatch();

                    var archiveDirectory = Path.Combine(tmpDirectory, Path.GetFileNameWithoutExtension(androidArchivePath)!);

                    const string libs = "Variations/il2cpp/Release/Libs";
                    const string symbols = "Variations/il2cpp/Release/Symbols";

                    await ExtractAsync(androidArchivePath!, archiveDirectory, new[] { $"./{libs}/*/libunity.so", $"./{symbols}/*/libunity.sym.so" }, cancellationToken, false);

                    Directory.CreateDirectory(androidDirectory);

                    IEnumerable<string> directories = Directory.GetDirectories(Path.Combine(archiveDirectory, libs));
                    if (Version > new UnityVersion(5, 3, 5, UnityVersionType.Final, 1))
                    {
                        directories = directories.Concat(Directory.GetDirectories(Path.Combine(archiveDirectory, symbols)));
                    }

                    foreach (var directory in directories)
                    {
                        var directoryInfo = Directory.CreateDirectory(Path.Combine(androidDirectory, Path.GetFileName(directory)));
                        foreach (var file in Directory.GetFiles(directory))
                        {
                            File.Copy(file, Path.Combine(directoryInfo.FullName, Path.GetFileName(file)), true);
                        }
                    }

                    File.Delete(AndroidPath);
                    ZipFile.CreateFromDirectory(androidDirectory, AndroidPath);

                    Log.Information("[{Version}] Extracted android binaries in {Time}", Version, stopwatch.Elapsed);
                }

                Log.Information("[{Version}] Extracting mono libraries", Version);
                using (var stopwatch = new AutoStopwatch())
                {
                    var isLegacyDownload = Id == null || Version.Major < 5;

                    var monoPath = (Version.IsMonolithic(), isLegacyDownload) switch
                    {
                        (true, true) when Version.Major == 4 && Version.Minor >= 5 => "Data/PlaybackEngines/windowsstandalonesupport/Variations/win64_nondevelopment/Data/Managed",
                        (true, true) => "Data/PlaybackEngines/windows64standaloneplayer/Managed",
                        (true, false) => "./Unity/Unity.app/Contents/PlaybackEngines/WindowsStandaloneSupport/Variations/win64_nondevelopment_mono/Data/Managed",
                        (false, true) => throw new Exception("Release can't be both legacy and modular at the same time"),
                        (false, false) => $"./Variations/win64{(Version >= new UnityVersion(2021, 2) ? "_player" : "")}_nondevelopment_mono/Data/Managed",
                    };

                    await ExtractAsync(monoArchivePath, managedDirectory, new[] { $"{monoPath}/*.dll" }, cancellationToken);

                    if (!Directory.Exists(managedDirectory) || Directory.GetFiles(managedDirectory, "*.dll").Length <= 0)
                    {
                        throw new Exception("Managed directory is empty");
                    }

                    File.Delete(ZipFilePath);
                    ZipFile.CreateFromDirectory(managedDirectory, ZipFilePath);

                    Log.Information("[{Version}] Extracted mono libraries in {Time}", Version, stopwatch.Elapsed);
                }

                Log.Information("[{Version}] Creating NuGet package for mono libraries", Version);

                CreateNuGetPackage(managedDirectory);
            }
            finally
            {
                Directory.Delete(tmpDirectory, true);
            }
        }

        private async Task DownloadAsync(string downloadUrl, string archivePath, CancellationToken cancellationToken)
        {
            if (File.Exists(archivePath))
            {
                Log.Information("[{Version}] Skipping download because {File} exists", Version, archivePath);
            }
            else
            {
                Log.Information("[{Version}] Downloading {Url}", Version, downloadUrl);
                using var stopwatch = new AutoStopwatch();

                await using (var stream = await _httpClient.GetStreamAsync(downloadUrl, cancellationToken))
                await using (var fileStream = File.OpenWrite(archivePath + ".part"))
                {
                    await stream.CopyToAsync(fileStream, cancellationToken);
                }

                File.Move(archivePath + ".part", archivePath);

                Log.Information("[{Version}] Downloaded {Url} in {Time}", Version, downloadUrl, stopwatch.Elapsed);
            }
        }

        private async Task ExtractAsync(string archivePath, string destinationDirectory, string[] filter, CancellationToken cancellationToken, bool flat = true)
        {
            var archiveDirectory = Path.Combine(Path.GetDirectoryName(archivePath)!, Path.GetFileNameWithoutExtension(archivePath));
            var extension = Path.GetExtension(archivePath);

            switch (extension)
            {
                case ".pkg":
                {
                    const string payloadName = "Payload~";
                    await SevenZip.ExtractAsync(archivePath, archiveDirectory, new[] { payloadName }, true, cancellationToken);
                    await SevenZip.ExtractAsync(Path.Combine(archiveDirectory, payloadName), destinationDirectory, filter, flat, cancellationToken);

                    break;
                }

                case ".exe":
                {
                    await SevenZip.ExtractAsync(archivePath, destinationDirectory, filter, flat, cancellationToken);

                    break;
                }

                default: throw new ArgumentOutOfRangeException(nameof(extension), extension, "Unrecognized archive type");
            }
        }

        private void CreateNuGetPackage(string pkgDir)
        {
            foreach (var file in Directory.EnumerateFiles(pkgDir, "*.dll"))
                AssemblyStripper.StripAssembly(file);

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

            var builder = new PackageBuilder(true);
            builder.PopulateFiles(pkgDir, deps.Select(d => new ManifestFile
            {
                Source = "*.dll",
                Target = $"lib/{d}"
            }));
            builder.Populate(meta);
            using var fs = File.Create(NuGetPackagePath);
            builder.Save(fs);
        }

        public async Task UploadNuGetPackageAsync(string sourceUrl, string apikey)
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
            Log.Information("[{Version}] Pushed NuGet package", Version);
        }
    }
}
