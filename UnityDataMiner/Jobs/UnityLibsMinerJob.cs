using AssetRipper.Primitives;
using BepInEx.AssemblyPublicizer;
using NuGet.Frameworks;
using NuGet.Packaging;
using Serilog;
using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace UnityDataMiner.Jobs
{
    internal sealed class UnityLibsMinerJob : MinerJob
    {
        public override string Name => "Unity libs + NuGet packages";

        public override bool CanRunFor(UnityBuild build) => true;

        public override bool ShouldRunFor(UnityBuild build)
            => !File.Exists(build.UnityLibsZipFilePath) || !File.Exists(build.NuGetPackagePath);

        public override ImmutableArray<MinerDependencyOption> GetDependencies(UnityBuild build)
            => [
                new([new(UnityPackageKind.Editor, EditorOS.Any)])
            ];

        public override async Task ExtractFromAssets(UnityBuild build, string tmpDir,
            ImmutableArray<UnityPackage> chosenPackages, ImmutableArray<string> packagePaths,
            CancellationToken cancellationToken)
        {
            Debug.Assert(chosenPackages is [{ Kind: UnityPackageKind.Editor }]);
            Debug.Assert(packagePaths.Length is 1);

            var packageOs = chosenPackages[0].OS;
            var packagePath = packagePaths[0];

            var managedDirectory = Path.Combine(tmpDir, "managed");
            Directory.CreateDirectory(managedDirectory);

            Log.Information("[{Version}] Extracting Unity libraries", build.Version);
            using var stopwatch = new AutoStopwatch();

            // select the correct path in the archive
            string managedPath;
            if (build.IsMonolithic)
            {
                Debug.Assert(packageOs is EditorOS.Windows);
                if (build.IsLegacyDownload)
                {
                    managedPath = build.Version is { Major: 4, Minor: >= 5 }
                        ? "Data/PlaybackEngines/windowsstandalonesupport/Variations/win64_nondevelopment/Data/Managed"
                        : "Data/PlaybackEngines/windows64standaloneplayer/Managed";
                }
                else
                {
                    managedPath = "Editor/Data/PlaybackEngines/windowsstandalonesupport/Variations/win64_nondevelopment_mono/Data/Managed";
                }
            }
            else
            {
                managedPath = packageOs switch
                {
                    EditorOS.Windows
                        => "./Variations/win64_nondevelopment_mono/Data/Managed",
                    EditorOS.MacOS
                        => "./Unity/Unity.app/Contents/PlaybackEngines/MacStandaloneSupport/Variations/macosx64_nondevelopment_mono/Data/Managed",
                    EditorOS.Linux when build.Version >= new UnityVersion(2021, 2)
                        => "Editor/Data/PlaybackEngines/LinuxStandaloneSupport/Variations/linux64_player_nondevelopment_mono/Data/Managed",
                    EditorOS.Linux
                        => "Editor/Data/PlaybackEngines/LinuxStandaloneSupport/Variations/linux64_withgfx_nondevelopment_mono/Data/Managed",

                    _ => throw new NotSupportedException(),
                };
            }

            // extract the binaries from the archive
            await build.ExtractAsync(packagePath, managedDirectory, [managedPath + "/*.dll"], cancellationToken, flat: true);

            Log.Information("[{Version}] Packaging Unity libraries into zip", build.Version);
            var tmpZip = Path.Combine(tmpDir, "libs.zip");
            ZipFile.CreateFromDirectory(managedDirectory, tmpZip);

            if (File.Exists(build.UnityLibsZipFilePath))
            {
                File.Delete(build.UnityLibsZipFilePath);
            }
            File.Move(tmpZip, build.UnityLibsZipFilePath);


            Log.Information("[{Version}] Creating NuGet package for Unity libraries", build.Version);

            // first, publicize and strip the assemblies
            foreach (var file in Directory.EnumerateFiles(managedDirectory, "*.dll"))
            {
                AssemblyPublicizer.Publicize(file, file, new() { Strip = true });
            }

            // now create the package
            var frameworkTargets = new[] { "net35", "net45", "netstandard2.0" };

            var meta = new ManifestMetadata
            {
                Id = "UnityEngine.Modules",
                Authors = ["Unity"],
                Version = build.NuGetVersion,
                Description = "UnityEngine modules",
                DevelopmentDependency = true,
                DependencyGroups = frameworkTargets.Select(d =>
                    new PackageDependencyGroup(NuGetFramework.Parse(d), []))
            };

            var builder = new PackageBuilder(true);
            builder.PopulateFiles(managedDirectory, frameworkTargets.Select(d => new ManifestFile
            {
                Source = "*.dll",
                Target = $"lib/{d}"
            }));
            builder.Populate(meta);

            var tmpPkg = Path.Combine(tmpDir, "libs.nupkg");
            using (var fs = File.Create(tmpPkg))
            {
                builder.Save(fs);
            }

            if (File.Exists(build.NuGetPackagePath))
            {
                File.Delete(build.NuGetPackagePath);
            }
            File.Move(tmpPkg, build.NuGetPackagePath);

            Log.Information("[{Version}] Extracted and packaged Unity libraries in {Time}", build.Version, stopwatch.Elapsed);
        }
    }
}
