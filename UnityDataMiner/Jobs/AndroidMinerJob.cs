using AssetRipper.Primitives;
using Serilog;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace UnityDataMiner.Jobs
{
    internal sealed class AndroidMinerJob : MinerJob
    {
        public override string Name => "Android binaries";

        public override bool CanRunFor(UnityBuild build)
            => !build.Version.IsMonolithic()
            && (build.LinuxInfo is not null || build.MacOsInfo is not null);

        public override bool ShouldRunFor(UnityBuild build)
            => !Directory.Exists(build.AndroidPath);

        public override ImmutableArray<MinerDependencyOption> GetDependencies(UnityBuild build)
            => [
                new([new(UnityPackageKind.Android, EditorOS.Linux)]), // Prefer the Linux package
                new([new(UnityPackageKind.Android, EditorOS.MacOS)]),
            ];

        public override async Task ExtractFromAssets(UnityBuild build, string tmpDir,
            ImmutableArray<UnityPackage> fulfilledDependency, ImmutableArray<string> packagePaths,
            CancellationToken cancellationToken)
        {
            Debug.Assert(packagePaths.Length is 1);
            Debug.Assert(fulfilledDependency is [{ Kind: UnityPackageKind.Android }]);

            var packagePath = packagePaths[0];
            var packageOs = fulfilledDependency[0].OS;

            Log.Information("[{Version}] Extracting android binaries", build.Version);
            using var stopwatch = new AutoStopwatch();
            var archiveDirectory =
                Path.Combine(tmpDir, Path.GetFileNameWithoutExtension(packagePath)!);

            const string libs = "Variations/il2cpp/Release/Libs";
            const string symbols = "Variations/il2cpp/Release/Symbols";
            await build.ExtractAsync(packagePath, archiveDirectory,
                [$"./{libs}/*/libunity.so", $"./{symbols}/*/libunity.sym.so"],
                cancellationToken, flat: false);

            var androidDirectory = Path.Combine(tmpDir, "android");

            Directory.CreateDirectory(androidDirectory);

            IEnumerable<string> directories = Directory.GetDirectories(Path.Combine(archiveDirectory, libs));

            var hasSymbols = build.Version > new UnityVersion(5, 3, 5, UnityVersionType.Final, 1);

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

            Directory.CreateDirectory(build.AndroidPath);

            foreach (var directory in Directory.GetDirectories(androidDirectory))
            {
                ZipFile.CreateFromDirectory(directory,
                    Path.Combine(build.AndroidPath, Path.GetFileName(directory) + ".zip"));
            }

            Log.Information("[{Version}] Extracted android binaries in {Time}", build.Version, stopwatch.Elapsed);
        }
    }
}
