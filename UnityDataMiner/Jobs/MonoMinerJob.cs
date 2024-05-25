using Serilog;
using System;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace UnityDataMiner.Jobs
{
    internal sealed class MonoMinerJob : MinerJob
    {
        public override string Name => "Mono runtime";

        public override bool CanRunFor(UnityBuild build) => true;

        public override bool ShouldRunFor(UnityBuild build)
            => !Directory.Exists(build.MonoPath);

        public override ImmutableArray<MinerDependencyOption> GetDependencies(UnityBuild build)
            => [
                // first, prefer getting just a component for each
                new MinerDependencyOption([
                    new(UnityPackageKind.WindowsMonoSupport, EditorOS.Any),
                    new(UnityPackageKind.LinuxMonoSupport, EditorOS.Any, AllowMissing: true),
                    new(UnityPackageKind.MacMonoSupport, EditorOS.Any),
                ]),
                
                // then, the editor package for the host + the support packages for the others
                ..(ReadOnlySpan<MinerDependencyOption>)(
                    build.IsMonolithic
                    ? [new([new(UnityPackageKind.Editor, EditorOS.Any)])] // in monolithic builds, we need the editor always
                    : build.HasModularPlayer 
                    ? [ // in non-monolithic builds (that are modular with the OS support package in the main editor), we also want to allow editor + support packages
                        new([
                            new(UnityPackageKind.Editor, EditorOS.Windows),
                            new(UnityPackageKind.LinuxMonoSupport, EditorOS.Any, AllowMissing: true),
                            new(UnityPackageKind.MacMonoSupport, EditorOS.Any),
                        ]),
                        new([
                            new(UnityPackageKind.WindowsMonoSupport, EditorOS.Any),
                            new(UnityPackageKind.Editor, EditorOS.Linux),
                            new(UnityPackageKind.MacMonoSupport, EditorOS.Any),
                        ]),
                        new([
                            new(UnityPackageKind.WindowsMonoSupport, EditorOS.Any),
                            new(UnityPackageKind.LinuxMonoSupport, EditorOS.Any, AllowMissing: true),
                            new(UnityPackageKind.Editor, EditorOS.MacOS),
                        ]),
                    ]
                    : [] // if this build is in the short window where the editor is modular, but doesn't include the platform's runtime in the editor bundle,
                         // we need all of the support bundles as well, which is the previous dep
                ),
            ];

        public override async Task ExtractFromAssets(UnityBuild build, string tmpDir,
            ImmutableArray<UnityPackage> chosenPackages, ImmutableArray<string> packagePaths,
            CancellationToken cancellationToken)
        {
            var monoBaseDir = Path.Combine(tmpDir, "mono");
            Directory.CreateDirectory(monoBaseDir);

            Log.Information("[{Version}] Packaging Mono binaries", build.Version);
            using var stopwatch = new AutoStopwatch();

            for (var i = 0; i < chosenPackages.Length; i++)
            {
                var (packageKind, packageOs, _) = chosenPackages[i];
                var packagePath = packagePaths[i];

                if (build.IsMonolithic)
                {
                    // TODO:
                    throw new NotImplementedException();
                }
                else
                {
                    var monoTargetOs = packageKind switch
                    {
                        UnityPackageKind.Editor => packageOs,
                        UnityPackageKind.WindowsMonoSupport => EditorOS.Windows,
                        UnityPackageKind.LinuxMonoSupport => EditorOS.Linux,
                        UnityPackageKind.MacMonoSupport => EditorOS.MacOS,
                        _ => throw new NotImplementedException(),
                    };

                    var targetOsName = monoTargetOs switch
                    {
                        EditorOS.Any => "any",
                        EditorOS.Windows => "win",
                        EditorOS.Linux => "linux",
                        EditorOS.MacOS => "mac",
                        _ => throw new NotImplementedException(),
                    };

                    Log.Information("[{Version}] Processing {TargetOS}", build.Version, monoTargetOs);

                    var thisPkgDir = Path.Combine(monoBaseDir, monoTargetOs.ToString());

                    switch (build.Version.Major)
                    {
                        case >= 2021:
                        {
                            var (extractPath, variationsBase, inPlayerDir) = (monoTargetOs, packageKind) switch
                            {
                                // TODO: editor package paths

                                (EditorOS.Windows, _) => ("./Variations/*_player_nondevelopment_mono/Mono*/**", "Variations", ""),
                                (EditorOS.Linux, _) => ("./Variations/*_player_nondevelopment_mono/Data/Mono*/**", "Variations", "Data/"),
                                (EditorOS.MacOS, _) => ("./Variations/*_player_nondevelopment_mono/UnityPlayer.app/Contents/Frameworks/lib*", "Variations", ""),

                                _ => throw new NotImplementedException()
                            };

                            var variationsDir = !string.IsNullOrEmpty(variationsBase) ? Path.Combine(thisPkgDir, variationsBase) : thisPkgDir;

                            if (monoTargetOs is not EditorOS.MacOS)
                            {
                                // Windows and Linux layouts are relatively convenient
                                await build.ExtractAsync(packagePath, thisPkgDir, [extractPath], cancellationToken, flat: false);

                                foreach (var playerDir in Directory.EnumerateDirectories(variationsDir))
                                {
                                    var arch = Path.GetFileName(playerDir).Replace("_player_nondevelopment_mono", "");
                                    if (!arch.Contains(targetOsName))
                                    {
                                        arch = targetOsName + "_" + arch;
                                    }

                                    var monoRoot = !string.IsNullOrEmpty(inPlayerDir) ? Path.Combine(playerDir, inPlayerDir) : playerDir;
                                    foreach (var mono in Directory.EnumerateDirectories(monoRoot))
                                    {
                                        var monoName = Path.GetFileName(mono);

                                        // If there's a dir that's not named "etc", rename that to "runtime"
                                        foreach (var subdir in Directory.EnumerateDirectories(mono))
                                        {
                                            if (Path.GetFileName(subdir) != "etc")
                                            {
                                                Directory.Move(subdir, Path.Combine(mono, "runtime"));
                                                break;
                                            }
                                        }

                                        // then we can create the zip file
                                        ZipFile.CreateFromDirectory(mono, Path.Combine(monoBaseDir, $"{arch}_{monoName}.zip"));
                                    }
                                }
                            }
                            else
                            {
                                // MacOS has a more annoying layout. The config is entirely separate from the runtime. 
                                await build.ExtractAsync(packagePath, thisPkgDir, [extractPath, "./Mono*/**"], cancellationToken, flat: false);

                                // our outer loop is for the config
                                foreach (var monoConfigDir in Directory.EnumerateDirectories(thisPkgDir, "Mono*"))
                                {
                                    var monoName = Path.GetFileName(monoConfigDir);
                                    var runtimeDir = Path.Combine(monoConfigDir, "runtime");

                                    foreach (var playerDir in Directory.EnumerateDirectories(variationsDir))
                                    {
                                        var arch = Path.GetFileName(playerDir).Replace("_player_nondevelopment_mono", "");
                                        if (!arch.Contains(targetOsName))
                                        {
                                            arch = targetOsName + "_" + arch;
                                        }

                                        if (Directory.Exists(runtimeDir))
                                        {
                                            Directory.Delete(runtimeDir, true);
                                        }

                                        Directory.Move(Path.Combine(playerDir, "UnityPlayer.app", "Contents", "Frameworks"), runtimeDir);
                                        ZipFile.CreateFromDirectory(monoConfigDir, Path.Combine(monoBaseDir, $"{arch}_{monoName}.zip"));
                                    }
                                }
                            }

                            break;
                        }

                        default:
                            throw new NotImplementedException();
                    }
                }
            }

            Directory.CreateDirectory(build.MonoPath);
            foreach (var zip in Directory.EnumerateFiles(monoBaseDir, "*.zip"))
            {
                File.Move(zip, Path.Combine(build.MonoPath, Path.GetFileName(zip)));
            }

            Log.Information("[{Version}] Mono binaries packaged in {Time}", build.Version, stopwatch.Elapsed);
        }
    }
}
