using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace UnityDataMiner
{
    public abstract class MinerJob
    {
        public abstract string Name { get; }

        // answers whether this job can act for this build
        public abstract bool CanRunFor(UnityBuild build);
        // answers whether this job should run, i.e. whether it is out-of-date
        public abstract bool ShouldRunFor(UnityBuild build);

        // returns a set of options, i.e. OR'd, in order of preference.
        public abstract ImmutableArray<MinerDependencyOption> GetDependencies(UnityBuild build);

        // if true, each dependency will be passed in as it is downloaded
        // note: the order guarantee that is normally provided in ExtractFromAssets does not apply in this case
        public virtual bool RunIncrementally => false;

        // chosenPackages is in the same order as the dependency, but with the Any OS replaced with the actually selected OS
        public abstract Task ExtractFromAssets(UnityBuild build, string tmpDir,
            ImmutableArray<UnityPackage> chosenPackages, ImmutableArray<string> packagePaths,
            CancellationToken cancellationToken);
    }

    // TODO: we may be able to replace the heuristic size with actual size, but that doesn't really
    //       enable us to encode preference

    // AllowMissing means that if this package isn't available for the given Unity version,
    // the dependency group this is a part of is still otherwise valid.
    public readonly record struct UnityPackage(UnityPackageKind Kind, EditorOS OS, bool AllowMissing = false)
    {
        public int HeuristicSize => Kind.GetRelativePackageSize() + (OS switch
        {
            EditorOS.Any => 0,
            EditorOS.Windows => 2,
            EditorOS.Linux => 0,
            EditorOS.MacOS => 1,
            _ => throw new System.NotImplementedException(),
        });

        public bool Matches(UnityPackage package)
            => Kind == package.Kind
            && (OS == package.OS || OS is EditorOS.Any || package.OS is EditorOS.Any);
    }

    // requires ALL packages to do its job
    public readonly record struct MinerDependencyOption(ImmutableArray<UnityPackage> NeededPackages);

    public enum UnityPackageKind
    {
        Editor,
        Android,
        WindowsMonoSupport,
        LinuxMonoSupport,
        MacMonoSupport
    }

    public static class UnityPackageKindExtensions
    {
        // We use this during planning as a heuristic to minimize the overall download size.
        public static int GetRelativePackageSize(this UnityPackageKind kind)
            => kind switch
            {
                UnityPackageKind.Editor => 10,
                UnityPackageKind.Android => 1,
                UnityPackageKind.WindowsMonoSupport => 1,
                UnityPackageKind.LinuxMonoSupport => 1,
                UnityPackageKind.MacMonoSupport => 1,
                _ => 0,
            };
    }

    public enum EditorOS
    {
        Any,
        Windows,
        Linux,
        MacOS,
    }
}
