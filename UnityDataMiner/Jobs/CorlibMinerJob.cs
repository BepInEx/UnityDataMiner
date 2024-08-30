using AsmResolver.PE.Tls;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UnityDataMiner.Jobs
{
    internal sealed class CorlibMinerJob : MinerJob
    {
        public override string Name => "corlibs";

        public override bool CanRunFor(UnityBuild build) => true;

        public override bool ShouldRunFor(UnityBuild build) => !File.Exists(build.CorlibZipPath);

        public override ImmutableArray<MinerDependencyOption> GetDependencies(UnityBuild build)
            => [new([new(UnityPackageKind.Editor, EditorOS.Any)])];

        public override async Task ExtractFromAssets(UnityBuild build, string tmpDir, ImmutableArray<UnityPackage> chosenPackages, ImmutableArray<string> packagePaths, CancellationToken cancellationToken)
        {
            Debug.Assert(packagePaths.Length is 1);
            Debug.Assert(chosenPackages is [{ Kind: UnityPackageKind.Editor }]);

            var packagePath = packagePaths[0];
            var packageOs = chosenPackages[0].OS;

            var corlibDirectory = Path.Combine(tmpDir, "corlib");

            Log.Information("[{Version}] Extracting corlibs", build.Version);
            using var stopwatch = new AutoStopwatch();

            // TODO: Maybe grab both 2.0 and 4.5 DLLs for < 2018 monos
            var corlibPath = (build.IsLegacyDownload, packageOs) switch
            {
                (true, _) => "Data/Mono/lib/mono/2.0",
                (_, EditorOS.MacOS) => "./Unity/Unity.app/Contents/MonoBleedingEdge/lib/mono/4.5",
                _ => "Editor/Data/MonoBleedingEdge/lib/mono/4.5",
            };

            await build.ExtractAsync(packagePath, corlibDirectory, [$"{corlibPath}/*.dll"], cancellationToken);

            if (!Directory.Exists(corlibDirectory) ||
                Directory.GetFiles(corlibDirectory, "*.dll").Length <= 0)
            {
                throw new Exception("Corlibs directory is empty");
            }

            File.Delete(build.CorlibZipPath);
            ZipFile.CreateFromDirectory(corlibDirectory, build.CorlibZipPath);

            Log.Information("[{Version}] Extracted corlibs in {Time}", build.Version, stopwatch.Elapsed);
        }

    }
}
