using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace UnityDataMiner
{
    public class UnityVersion
    {
        public string? Hash { get; }

        public string RawVersion { get; }

        public Version Version { get; }

        public string ZipFilePath { get; }

        public bool IsRunNeeded => !File.Exists(ZipFilePath);

        public UnityVersion(string repositoryPath, string? hash, string rawVersion)
        {
            Hash = hash;
            RawVersion = rawVersion;

            Version = Version.Parse(RawVersion.Replace("f", "."));
            ZipFilePath = Path.Combine(repositoryPath, "libraries", Version.ToString(3) + ".zip");
        }

        private static readonly SemaphoreSlim _downloadLock = new SemaphoreSlim(1, 1);

        public async Task MakeLibraryZipAsync()
        {
            var isLegacyDownload = Hash == null || Version.Major < 5;
            var isMonolithic = isLegacyDownload || Version.Major == 5 && Version.Minor < 3;

            var downloadUrl = isMonolithic
                ? isLegacyDownload
                    ? $"https://download.unity3d.com/download_unity/UnitySetup-{RawVersion}.exe"
                    : $"https://beta.unity3d.com/download/{Hash}/MacEditorInstaller/Unity-{RawVersion}.pkg"
                : $"https://beta.unity3d.com/download/{Hash}/MacEditorTargetInstaller/UnitySetup-Windows{(Version.Major >= 2018 ? "-Mono" : "")}-Support-for-Editor-{RawVersion}.pkg";

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
            catch (IOException e) when (e.InnerException is SocketException socketException && socketException.SocketErrorCode == SocketError.ConnectionReset)
            {
                Log.Warning("Failed to download {Version}, waiting 5 seconds before retrying...", RawVersion);
                await Task.Delay(5000);
                _downloadLock.Release();

                await MakeLibraryZipAsync();
                return;
            }

            _downloadLock.Release();

            Log.Information("[{Version}] Extracting", RawVersion);

            var monoPath = isMonolithic
                ? isLegacyDownload
                    ? (Version.Major == 4 && Version.Minor >= 5) ? "Data/PlaybackEngines/windowsstandalonesupport/Variations/win64_nondevelopment/Data/Managed" : "Data/PlaybackEngines/windows64standaloneplayer/Managed"
                    : "Unity/Unity.app/Contents/PlaybackEngines/WindowsStandaloneSupport/Variations/win64_nondevelopment_mono/Data/Managed"
                : "Variations/win64_nondevelopment_mono/Data/Managed";

            // I'm way too lazy to write c# wrappers for both 7zip (XAR) and cpio (whatever the fuck Payload~ is)
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

            ZipFile.CreateFromDirectory(Path.Combine(tmpDirectory, monoPath), ZipFilePath);

            Directory.Delete(tmpDirectory, true);

            Log.Information("[{Version}] Done", RawVersion);
        }
    }
}
