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
    internal sealed class LibIl2CppSourceMinerJob : MinerJob
    {
        public override string Name => "IL2CPP sources";

        public override bool CanRunFor(UnityBuild build)
            => build.HasLibIl2Cpp;

        public override bool ShouldRunFor(UnityBuild build)
            => !File.Exists(build.LibIl2CppSourceZipPath);

        public override ImmutableArray<MinerDependencyOption> GetDependencies(UnityBuild build)
            => [new([new(UnityPackageKind.Editor, EditorOS.Any)])];

        public override async Task ExtractFromAssets(UnityBuild build, string tmpDir,
            ImmutableArray<UnityPackage> chosenPackages, ImmutableArray<string> packagePaths,
            CancellationToken cancellationToken)
        {
            Debug.Assert(packagePaths.Length is 1);
            Debug.Assert(chosenPackages is [{ Kind: UnityPackageKind.Editor }]);

            var packagePath = packagePaths[0];
            var packageOs = chosenPackages[0].OS;

            var libil2cppSourceDirectory = Path.Combine(tmpDir, "libil2cpp-source");

            Log.Information("[{Version}] Extracting libil2cpp source code", build.Version);
            using var stopwatch = new AutoStopwatch();

            // TODO: find out if the path changes in different versions
            var libil2cppSourcePath = packageOs switch
            {
                EditorOS.MacOS => "./Unity/Unity.app/Contents/il2cpp/libil2cpp",
                _ => "Editor/Data/il2cpp/libil2cpp",
            };

            await build.ExtractAsync(packagePath, libil2cppSourceDirectory,
                [$"{libil2cppSourcePath}/**"], cancellationToken, false);

            var zipDir = Path.Combine(libil2cppSourceDirectory, libil2cppSourcePath);
            if (!Directory.Exists(zipDir) || Directory.GetFiles(zipDir).Length <= 0)
            {
                throw new Exception("LibIl2Cpp source code directory is empty");
            }

            File.Delete(build.LibIl2CppSourceZipPath);
            ZipFile.CreateFromDirectory(zipDir, build.LibIl2CppSourceZipPath);

            Log.Information("[{Version}] Extracted libil2cpp source code in {Time}", build.Version,
                stopwatch.Elapsed);
        }
    }
}
