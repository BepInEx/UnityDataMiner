using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AssetRipper.Primitives;
using BepInEx.AssemblyPublicizer;
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

        public string InfoCacheDir { get; }

        public string CorlibZipPath { get; }

        public string LibIl2CppSourceZipPath { get; }

        public bool IsRunNeeded => !File.Exists(ZipFilePath) || !File.Exists(NuGetPackagePath) ||
                                   !File.Exists(CorlibZipPath) ||
                                   !File.Exists(LibIl2CppSourceZipPath) ||
                                   !Version.IsMonolithic() && !Directory.Exists(AndroidPath);

        public string BaseDownloadUrl => Version.GetDownloadUrl() + (Id == null ? string.Empty : $"{Id}/");

        public UnityBuildInfo? WindowsInfo { get; private set; }
        public UnityBuildInfo? LinuxInfo { get; private set; }
        public UnityBuildInfo? MacOsInfo { get; private set; }

        public UnityBuild(string repositoryPath, string? id, UnityVersion version)
        {
            Id = id;
            Version = version;

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
                    _ => throw new ArgumentOutOfRangeException(nameof(Version.Type), Version.Type,
                        "Invalid Version.Type for " + Version),
                } + "." + Version.TypeNumber);

            var versionName = Version.Type == UnityVersionType.Final ? ShortVersion : Version.ToString();
            var zipName = $"{versionName}.zip";
            ZipFilePath = Path.Combine(repositoryPath, "libraries", zipName);
            AndroidPath = Path.Combine(repositoryPath, "android", versionName);
            CorlibZipPath = Path.Combine(repositoryPath, "corlibs", zipName);
            LibIl2CppSourceZipPath = Path.Combine(repositoryPath, "libil2cpp-source", zipName);
            NuGetPackagePath = Path.Combine(repositoryPath, "packages", $"{NuGetVersion}.nupkg");
            InfoCacheDir = Path.Combine(repositoryPath, "versions", $"{id}");

            WindowsInfo = ReadInfo("win");

            if (Version >= _firstLinuxVersion)
            {
                LinuxInfo = ReadInfo("linux");
            }

            MacOsInfo = ReadInfo("osx");
        }

        private static readonly HttpClient _httpClient = new();
        private static readonly SemaphoreSlim _downloadLock = new(2, 2);

        private static readonly UnityVersion _firstLinuxVersion = new(2018, 1, 5);

        // First modular version where own native player is included in the default installer
        private static readonly UnityVersion _firstMergedModularVersion = new(5, 4, 0, UnityVersionType.Beta, 8);

        // TODO: Might need to define more DLLs? This should be enough for basic unhollowing.
        private static readonly string[] _importantCorlibs =
        {
            "Microsoft.CSharp",
            "Mono.Posix",
            "Mono.Security",
            "mscorlib",
            "Facades/netstandard",
            "System.Configuration",
            "System.Core",
            "System.Data",
            "System",
            "System.Net.Http",
            "System.Numerics",
            "System.Runtime.Serialization",
            "System.Security",
            "System.Xml",
            "System.Xml.Linq",
        };

        private bool HasLinuxEditor => LinuxInfo is not null;
        private bool HasModularPlayer => Version >= _firstMergedModularVersion;
        private bool IsMonolithic => Version.IsMonolithic();

        public bool NeedsInfoFetch { get; private set; }

        private UnityBuildInfo? ReadInfo(string variation)
        {
            if (!Directory.Exists(InfoCacheDir))
            {
                NeedsInfoFetch = true;
                return null;
            }

            var path = Path.Combine(InfoCacheDir, $"{variation}.ini");
            try
            {
                var variationIni = File.ReadAllText(path);
                var info = UnityBuildInfo.Parse(variationIni);
                if (info.Unity.Version != null && !info.Unity.Version.Equals(Version))
                {
                    throw new Exception();
                }

                return info;
            }
            catch (Exception)
            {
                NeedsInfoFetch = true;
                return null;
            }
        }

        public async Task FetchInfoAsync(CancellationToken cancellationToken)
        {
            if (!NeedsInfoFetch)
            {
                return;
            }

            async Task<UnityBuildInfo?> FetchVariation(string variation)
            {
                var variationUrl = BaseDownloadUrl + $"unity-{Version}-{variation}.ini";
                string variationIni;
                try
                {
                    Log.Information("Fetching {Variation} info for {Version} from {Url}", variation, Version, variationUrl);
                    variationIni = await _httpClient.GetStringAsync(variationUrl, cancellationToken);
                }
                catch (HttpRequestException hre) when (hre.StatusCode == HttpStatusCode.Forbidden)
                {
                    Log.Warning("Could not fetch {Variation} info for {Version} from {Url}. Got 'Access forbidden'",
                        variation, Version, variationUrl);
                    return null;
                }

                var info = UnityBuildInfo.Parse(variationIni);
                if (info.Unity.Version != null && !info.Unity.Version.Equals(Version))
                {
                    throw new Exception(
                        $"Build info version is invalid (expected {Version}, got {info.Unity.Version})");
                }

                Directory.CreateDirectory(InfoCacheDir);
                await File.WriteAllTextAsync(Path.Combine(InfoCacheDir, $"{variation}.ini"), variationIni,
                    cancellationToken);
                return info;
            }

            WindowsInfo = await FetchVariation("win");
            MacOsInfo = await FetchVariation("osx");

            if (Version >= _firstLinuxVersion)
            {
                LinuxInfo = await FetchVariation("linux");
            }

            NeedsInfoFetch = false;
        }

        private string GetUnityEditorDownloadPath()
        {
            var isLegacyDownload = Id == null || Version.Major < 5;
            var editorDownloadPrefix = isLegacyDownload ? "UnitySetup-" : "UnitySetup64-";

            if (LinuxInfo != null)
            {
                return LinuxInfo.Unity.Url;
            }

            if (MacOsInfo != null && HasModularPlayer)
            {
                return MacOsInfo.Unity.Url;
            }

            if (WindowsInfo != null)
            {
                return WindowsInfo.Unity.Url;
            }

            return $"{editorDownloadPrefix}{ShortVersion}.exe";
        }

        public async Task MineAsync(CancellationToken cancellationToken)
        {
            var isLegacyDownload = Id == null || Version.Major < 5;

            var unityEditorDownloadPath = GetUnityEditorDownloadPath();

            var monoDownloadUrl = BaseDownloadUrl + unityEditorDownloadPath;
            string? corlibDownloadUrl = null;

            // For specific versions, the installer has no players at all
            // So for corlib, download both the installer and the support module
            if (!HasModularPlayer)
            {
                corlibDownloadUrl = monoDownloadUrl;
                monoDownloadUrl = BaseDownloadUrl + WindowsInfo?.WindowsMono?.Url ?? throw new InvalidOperationException();
            }

            var androidDownloadUrl =
                (LinuxInfo == null && MacOsInfo == null) ||
                Version.IsMonolithic() // TODO make monolithic handling better
                    ? null
                    : BaseDownloadUrl + (LinuxInfo ?? MacOsInfo)!.Android!.Url;

            var tmpDirectory = Path.Combine(Path.GetTempPath(), "UnityDataMiner", Version.ToString());
            Directory.CreateDirectory(tmpDirectory);

            var managedDirectory = Path.Combine(tmpDirectory, "managed");
            var corlibDirectory = Path.Combine(tmpDirectory, "corlib");
            var libil2cppSourceDirectory = Path.Combine(tmpDirectory, "libil2cpp-source");
            var androidDirectory = Path.Combine(tmpDirectory, "android");

            var monoArchivePath = Path.Combine(tmpDirectory, Path.GetFileName(monoDownloadUrl));
            var corlibArchivePath = HasModularPlayer
                ? monoArchivePath
                : Path.Combine(tmpDirectory, Path.GetFileName(corlibDownloadUrl)!);
            var androidArchivePath = androidDownloadUrl == null
                ? null
                : Path.Combine(tmpDirectory, Path.GetFileName(androidDownloadUrl));
            var libil2cppSourceArchivePath = Path.Combine(tmpDirectory, Path.GetFileName(unityEditorDownloadPath));

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

                            if (corlibDownloadUrl != null)
                            {
                                await DownloadAsync(corlibDownloadUrl, corlibArchivePath, cancellationToken);
                            }

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
                    catch (IOException e) when (e.InnerException is SocketException
                                                {
                                                    SocketErrorCode: SocketError.ConnectionReset
                                                })
                    {
                        Log.Warning("Failed to download {Version}, waiting 5 seconds before retrying...", Version);
                        await Task.Delay(5000, cancellationToken);
                    }
                }

                if (androidDownloadUrl != null && !Directory.Exists(AndroidPath))
                {
                    Log.Information("[{Version}] Extracting android binaries", Version);
                    using var stopwatch = new AutoStopwatch();

                    var archiveDirectory =
                        Path.Combine(tmpDirectory, Path.GetFileNameWithoutExtension(androidArchivePath)!);

                    const string libs = "Variations/il2cpp/Release/Libs";
                    const string symbols = "Variations/il2cpp/Release/Symbols";

                    await ExtractAsync(androidArchivePath!, archiveDirectory,
                        new[] { $"./{libs}/*/libunity.so", $"./{symbols}/*/libunity.sym.so" }, cancellationToken, false);

                    Directory.CreateDirectory(androidDirectory);

                    IEnumerable<string> directories = Directory.GetDirectories(Path.Combine(archiveDirectory, libs));

                    var hasSymbols = Version >= new UnityVersion(5, 4, 0, UnityVersionType.Beta, 19);

                    if (hasSymbols)
                    {
                        directories =
                            directories.Concat(Directory.GetDirectories(Path.Combine(archiveDirectory, symbols)));
                    }

                    foreach (var directory in directories)
                    {
                        var directoryInfo =
                            Directory.CreateDirectory(Path.Combine(androidDirectory, Path.GetFileName(directory)));
                        foreach (var file in Directory.GetFiles(directory))
                        {
                            File.Copy(file, Path.Combine(directoryInfo.FullName, Path.GetFileName(file)), true);
                        }
                    }

                    if (hasSymbols)
                    {
                        foreach (var directory in Directory.GetDirectories(androidDirectory))
                        {
                            await EuUnstrip.UnstripAsync(Path.Combine(directory, "libunity.so"),
                                Path.Combine(directory, "libunity.sym.so"), cancellationToken);
                        }
                    }

                    Directory.CreateDirectory(AndroidPath);

                    foreach (var directory in Directory.GetDirectories(androidDirectory))
                    {
                        ZipFile.CreateFromDirectory(directory,
                            Path.Combine(AndroidPath, Path.GetFileName(directory) + ".zip"));
                    }

                    Log.Information("[{Version}] Extracted android binaries in {Time}", Version, stopwatch.Elapsed);
                }

                if (!File.Exists(LibIl2CppSourceZipPath))
                {
                    Log.Information("[{Version}] Extracting libil2cpp source code", Version);
                    using (var stopwatch = new AutoStopwatch())
                    {
                        // TODO: find out if the path changes in different versions
                        var libil2cppSourcePath = HasLinuxEditor switch
                        {
                            true => "Editor/Data/il2cpp/libil2cpp",
                            false when HasModularPlayer => "./Unity/Unity.app/Contents/il2cpp/libil2cpp",
                            false when Version.LessThan(5, 0, 2) => "Editor/Data/PlaybackEngines/iossupport/il2cpp/libil2cpp/include",
                            false => "Editor/Data/il2cpp/libil2cpp",
                        };
                        await ExtractAsync(libil2cppSourceArchivePath, libil2cppSourceDirectory,
                            new[] { $"{libil2cppSourcePath}/**" }, cancellationToken, false);
                        var zipDir = Path.Combine(libil2cppSourceDirectory, libil2cppSourcePath);
                        if (!Directory.Exists(zipDir) || Directory.GetFiles(zipDir).Length <= 0)
                        {
                            throw new Exception("LibIl2Cpp source code directory is empty");
                        }

                        File.Delete(LibIl2CppSourceZipPath);
                        ZipFile.CreateFromDirectory(zipDir, LibIl2CppSourceZipPath);

                        Log.Information("[{Version}] Extracted libil2cpp source code in {Time}", Version,
                            stopwatch.Elapsed);
                    }
                }

                async Task ExtractManagedDir()
                {
                    bool Exists() => Directory.Exists(managedDirectory) &&
                                     Directory.GetFiles(managedDirectory, "*.dll").Length > 0;

                    if (Exists())
                    {
                        return;
                    }

                    // TODO: Clean up this massive mess
                    var monoPath = (Version.IsMonolithic(), isLegacyDownload) switch
                    {
                        (true, true) when Version.Major == 4 && Version.Minor >= 5 =>
                            "Data/PlaybackEngines/windowsstandalonesupport/Variations/win64_nondevelopment/Data/Managed",
                        (true, true) => "Data/PlaybackEngines/windows64standaloneplayer/Managed",
                        (true, false) =>
                            "Editor/Data/PlaybackEngines/windowsstandalonesupport/Variations/win64_nondevelopment_mono/Data/Managed",
                        (false, true) => throw new Exception(
                            "Release can't be both legacy and modular at the same time"),
                        (false, false) when HasLinuxEditor =>
                            $"Editor/Data/PlaybackEngines/LinuxStandaloneSupport/Variations/linux64{(Version >= new UnityVersion(2021, 2, 0, UnityVersionType.Alpha, 8) ? "_player" : "_withgfx")}_nondevelopment_mono/Data/Managed",
                        (false, false) when !HasLinuxEditor && HasModularPlayer =>
                            $"./Unity/Unity.app/Contents/PlaybackEngines/MacStandaloneSupport/Variations/macosx64_nondevelopment_mono/Data/Managed",
                        (false, false) => "*/Variations/win64_nondevelopment_mono/Data/Managed",
                    };

                    await ExtractAsync(monoArchivePath, managedDirectory, new[] { $"{monoPath}/*.dll" },
                        cancellationToken);

                    if (!Exists())
                    {
                        throw new Exception("Managed directory is empty");
                    }
                }

                if (!File.Exists(ZipFilePath))
                {
                    Log.Information("[{Version}] Extracting mono libraries", Version);
                    using (var stopwatch = new AutoStopwatch())
                    {
                        await ExtractManagedDir();
                        ZipFile.CreateFromDirectory(managedDirectory, ZipFilePath);
                        Log.Information("[{Version}] Extracted mono libraries in {Time}", Version, stopwatch.Elapsed);
                    }
                }

                if (!File.Exists(CorlibZipPath))
                {
                    using (var stopwatch = new AutoStopwatch())
                    {
                        // TODO: Maybe grab both 2.0 and 4.5 DLLs for < 2018 monos
                        var corlibPath = isLegacyDownload switch
                        {
                            true => "Data/Mono/lib/mono/2.0",
                            false when HasLinuxEditor || !HasModularPlayer =>
                                "Editor/Data/MonoBleedingEdge/lib/mono/4.5",
                            false => "./Unity/Unity.app/Contents/MonoBleedingEdge/lib/mono/4.5",
                        };

                        await ExtractAsync(corlibArchivePath, corlibDirectory,
                            _importantCorlibs.Select(s => $"{corlibPath}/{s}.dll").ToArray(), cancellationToken);

                        if (!Directory.Exists(corlibDirectory) ||
                            Directory.GetFiles(corlibDirectory, "*.dll").Length <= 0)
                        {
                            throw new Exception("Corlibs directory is empty");
                        }

                        File.Delete(CorlibZipPath);
                        ZipFile.CreateFromDirectory(corlibDirectory, CorlibZipPath);

                        Log.Information("[{Version}] Extracted corlibs in {Time}", Version, stopwatch.Elapsed);
                    }
                }

                if (!File.Exists(NuGetPackagePath))
                {
                    Log.Information("[{Version}] Creating NuGet package for mono libraries", Version);
                    using (var stopwatch = new AutoStopwatch())
                    {
                        await ExtractManagedDir();
                        CreateNuGetPackage(managedDirectory);
                        Log.Information("[{Version}] Created NuGet package for mono libraries in {Time}", Version,
                            stopwatch.Elapsed);
                    }
                }
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

        private async Task ExtractAsync(string archivePath, string destinationDirectory, string[] filter,
            CancellationToken cancellationToken, bool flat = true)
        {
            var archiveDirectory = Path.Combine(Path.GetDirectoryName(archivePath)!,
                Path.GetFileNameWithoutExtension(archivePath));
            var extension = Path.GetExtension(archivePath);

            switch (extension)
            {
                case ".pkg":
                {
                    await SevenZip.ExtractAsync(archivePath, archiveDirectory, ["Payload~", "*/Payload"], true, cancellationToken);

                    if (File.Exists(Path.Combine(archiveDirectory, "Payload")))
                    {
                        await SevenZip.ExtractAsync(Path.Combine(archiveDirectory, "Payload"), archiveDirectory, ["Payload~"], true, cancellationToken);
                        File.Delete(Path.Combine(archiveDirectory, "Payload"));
                    }

                    if (!File.Exists(Path.Combine(archiveDirectory, "Payload~")))
                    {
                        throw new Exception("Couldn't find Payload~ in the .pkg");
                    }

                    await SevenZip.ExtractAsync(Path.Combine(archiveDirectory, "Payload~"), destinationDirectory, filter, flat, cancellationToken);
                    File.Delete(Path.Combine(archiveDirectory, "Payload~"));

                    break;
                }

                case ".exe":
                {
                    await SevenZip.ExtractAsync(archivePath, destinationDirectory, filter, flat, cancellationToken);

                    break;
                }

                case ".xz":
                {
                    string payloadName = Path.GetFileNameWithoutExtension(archivePath);
                    await SevenZip.ExtractAsync(archivePath, archiveDirectory, new[] { payloadName }, true,
                        cancellationToken);
                    await SevenZip.ExtractAsync(Path.Combine(archiveDirectory, payloadName), destinationDirectory,
                        filter, flat, cancellationToken);

                    break;
                }

                default:
                    throw new ArgumentOutOfRangeException(nameof(extension), extension, "Unrecognized archive type");
            }
        }

        private void CreateNuGetPackage(string pkgDir)
        {
            foreach (var file in Directory.EnumerateFiles(pkgDir, "*.dll"))
                AssemblyPublicizer.Publicize(file, file, new AssemblyPublicizerOptions { Strip = true });

            var deps = new[] { "net35", "net45", "netstandard2.0" };

            var meta = new ManifestMetadata
            {
                Id = "UnityEngine.Modules",
                Authors = new[] { "Unity" },
                Version = NuGetVersion,
                Description = "UnityEngine modules",
                DevelopmentDependency = true,
                DependencyGroups = deps.Select(d =>
                    new PackageDependencyGroup(NuGetFramework.Parse(d), Array.Empty<PackageDependency>()))
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
