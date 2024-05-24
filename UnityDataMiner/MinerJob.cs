using AssetRipper.Primitives;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace UnityDataMiner
{
    internal abstract class MinerJob
    {
        public abstract string Name { get; }

        // answers whether this job can act for this build
        public abstract bool CanRunFor(UnityBuild build);
        // answers whether this job should run, i.e. whether it is out-of-date
        public abstract bool ShouldRunFor(UnityBuild build);

        // returns a set of options, i.e. OR'd, in order of preference.
        public abstract ImmutableArray<MinerDependencyOption> GetDependencies(UnityBuild build);

        // chosenPackages is in the same order as the dependency, but with the Any OS replaced with the actually selected OS
        public abstract Task ExtractFromAssets(UnityBuild build, string tmpDir,
            ImmutableArray<UnityPackage> chosenPackages, ImmutableArray<string> packagePaths,
            CancellationToken cancellationToken);
    }

    // AllowMissing means that if this package isn't available for the given Unity version,
    // the dependency group this is a part of is still otherwise valid.
    internal readonly record struct UnityPackage(UnityPackageKind Kind, EditorOS OS, bool AllowMissing = false);

    // requires ALL packages to do its job
    internal readonly record struct MinerDependencyOption(ImmutableArray<UnityPackage> NeededPackages);

    internal enum UnityPackageKind
    {
        Editor,
        Android,
        WindowsMonoSupport,
        LinuxMonoSupport,
        MacMonoSupport
    }

    internal static class UnityPackageKindExtensions
    {
        // We use this during planning as a heuristic to minimize the overall download size.
        public static int GetRelativePackageSize(this UnityPackageKind kind)
            => kind switch
            {
                UnityPackageKind.Editor => 6,
                UnityPackageKind.Android => 1,
                UnityPackageKind.WindowsMonoSupport => 1,
                UnityPackageKind.LinuxMonoSupport => 1,
                UnityPackageKind.MacMonoSupport => 1,
                _ => 0,
            };
    }

    internal enum EditorOS
    {
        Any,
        Windows,
        Linux,
        MacOS,
    }
}
