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

        public override bool RunIncrementally => true;

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
                    if (Directory.Exists(thisPkgDir))
                    {
                        Directory.Delete(thisPkgDir, true);
                    }
                    Directory.CreateDirectory(thisPkgDir);

                    var playerdirSuffix = (build.Version.Major, monoTargetOs) switch
                    {
                        ( >= 2021, _) => "_player_nondevelopment_mono",
                        (_, EditorOS.Linux) => "_withgfx_nondevelopment_mono",
                        _ => "_nondevelopment_mono"
                    };

                    var prefix = "**/";
                    var extractPath = monoTargetOs switch
                    {
                        // TODO: editor package paths

                        EditorOS.Windows => $"Variations/*{playerdirSuffix}/**",
                        EditorOS.Linux => $"Variations/*{playerdirSuffix}/**",
                        EditorOS.MacOS => $"Variations/*{playerdirSuffix}/UnityPlayer.app/Contents/**",

                        _ => throw new NotImplementedException()
                    };

                    if (packageKind is UnityPackageKind.Editor)
                    {
                        var editorPrefixPath = monoTargetOs switch
                        {
                            EditorOS.Windows => throw new NotImplementedException(),
                            EditorOS.Linux => "Editor/Data/PlaybackEngines/LinuxStandaloneSupport/",
                            EditorOS.MacOS => "Unity/Unity.app/Contents/PlaybackEngines/MacStandaloneSupport/",

                            _ => throw new NotSupportedException(),
                        };
                        extractPath = editorPrefixPath + extractPath;
                    }

                    if (monoTargetOs is not EditorOS.MacOS || packageKind is not UnityPackageKind.Editor)
                    {
                        // Windows and Linux layouts are relatively convenient
                        await build.ExtractAsync(packagePath, thisPkgDir, [prefix + extractPath], cancellationToken, flat: false);
                        var variationsDir = GetVariationsDir(build, thisPkgDir);
                        if (variationsDir is null) continue; // can't do anything without a known variations dir

                        foreach (var playerDir in Directory.EnumerateDirectories(variationsDir))
                        {
                            var arch = Path.GetFileName(playerDir).Replace(playerdirSuffix, "");
                            if (!arch.Contains(targetOsName))
                            {
                                arch = targetOsName + "_" + arch;
                            }

                            // search subdirs so we can find multiple Mono builds
                            foreach (var mono in Directory.EnumerateDirectories(playerDir, "Mono*", SearchOption.AllDirectories))
                            {
                                var monoName = Path.GetFileName(mono);
                                var targetRuntimeDir = Path.Combine(mono, "runtime");

                                // If there's a dir that's not named "etc", rename that to "runtime"
                                var foundRuntimeDir = false;
                                foreach (var subdir in Directory.EnumerateDirectories(mono))
                                {
                                    if (Path.GetFileName(subdir) != "etc")
                                    {
                                        var hasMonoBin = false;
                                        foreach (var file in Directory.EnumerateFiles(subdir, "*mono*"))
                                        {
                                            if (Path.GetFileName(file).Contains("mono"))
                                            {
                                                hasMonoBin = true;
                                                break;
                                            }
                                        }

                                        if (!hasMonoBin)
                                        {
                                            // this dir doesn't actually have any Mono binaries, skip it
                                            continue;
                                        }

                                        Directory.Move(subdir, targetRuntimeDir);
                                        foundRuntimeDir = true;
                                        break;
                                    }
                                }

                                if (!foundRuntimeDir && monoTargetOs is EditorOS.MacOS)
                                {
                                    // on MacOS, the non-mac distribution is even more screwey than usual. The binaries are in /UnityPlayer.app/Contents/Frameworks/<MonoName>/MonoEmbedRuntime/osx/
                                    var binariesDir = Path.Combine(playerDir, "UnityPlayer.app/Contents/Frameworks", monoName, "MonoEmbedRuntime/osx/");
                                    if (!Directory.Exists(binariesDir))
                                    {
                                        Log.Warning("[{Version}] Could not find MacOS binaries ({TestDir})", build.Version, binariesDir);
                                        continue; // no use trying to do anything useful here
                                    }
                                    Directory.Move(binariesDir, targetRuntimeDir);
                                }

                                // then we can create the zip file
                                ZipFile.CreateFromDirectory(mono, Path.Combine(monoBaseDir, $"{arch}_{monoName}.zip"));
                            }
                        }
                    }
                    else
                    {
                        // MacOS has a more annoying layout. The config is entirely separate from the runtime. 
                        await build.ExtractAsync(packagePath, thisPkgDir, [prefix + extractPath, "./Mono*/**"], cancellationToken, flat: false);
                        var variationsDir = GetVariationsDir(build, thisPkgDir);
                        if (variationsDir is null) continue; // can't do anything without a known variations dir

                        // our outer loop is for the config
                        foreach (var monoConfigDir in Directory.EnumerateDirectories(thisPkgDir, "Mono*"))
                        {
                            var monoName = Path.GetFileName(monoConfigDir);
                            var runtimeDir = Path.Combine(monoConfigDir, "runtime");

                            foreach (var playerDir in Directory.EnumerateDirectories(variationsDir))
                            {
                                var arch = Path.GetFileName(playerDir).Replace(playerdirSuffix, "");
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
                }
            }

            Directory.CreateDirectory(build.MonoPath);
            foreach (var zip in Directory.EnumerateFiles(monoBaseDir, "*.zip"))
            {
                File.Move(zip, Path.Combine(build.MonoPath, Path.GetFileName(zip)));
            }

            Log.Information("[{Version}] Mono binaries packaged in {Time}", build.Version, stopwatch.Elapsed);

            static string? GetVariationsDir(UnityBuild build, string thisPkgDir)
            {
                string? variationsDir = null;
                foreach (var candidate in Directory.EnumerateDirectories(thisPkgDir, "Variations", SearchOption.AllDirectories))
                {
                    if (Path.GetFileName(candidate) is "Variations")
                    {
                        variationsDir = candidate;
                        break;
                    }
                }

                if (variationsDir is null)
                {
                    Log.Error("[{Version}] Could not find variations dir in selectively extracted package", build.Version);
                }
                else
                {
                    Log.Debug("[{Version}] Found variations dir {Dir}", build.Version, variationsDir);
                }

                return variationsDir;
            }
        }
    }
}
