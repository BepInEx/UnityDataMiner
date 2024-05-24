using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UnityDataMiner
{
    internal class MinerJobScheduler
    {
        public static JobPlan? Plan(ImmutableArray<MinerJob> jobs, UnityBuild build)
        {
            Log.Information("[{Version}] Planning mine...", build.Version);

            var needJobs = new List<MinerJob>();
            var jobDeps = new List<ImmutableArray<MinerDependencyOption>>();

            // first, go through the provided jobs, and filter to the ones that can and should run
            foreach (var job in jobs)
            {
                if (!job.CanRunFor(build))
                {
                    Log.Debug("[{Version}] Skipping {Job} because it's not applicable", build.Version, job.Name);
                    continue;
                }

                if (!job.ShouldRunFor(build))
                {
                    Log.Debug("[{Version}] Skipping {Job} because it does not need to run", build.Version, job.Name);
                    continue;
                }

                var depOptions = job.GetDependencies(build);
                needJobs.Add(job);
                jobDeps.Add(depOptions);
            }

            if (jobDeps.Count == 0)
            {
                Log.Information("[{Version}] No applicable jobs; no work needs to be done.", build.Version);
                return null;
            }

            var queue = new PriorityQueue<InProgressPlan, int>();

            // TODO: this initial fill has the possibility of getting a sub-optimal plan if a job needs a specific OS
            //       package, is not the first job, and other jobs take Any. What can we do to solve this?
            // THe answer, I think, is to treat the initial pass differently, and an Any there means we add all
            // OSs to the output. (We should probably do that in general actually, but that means some annoying combinatorics
            // in EnqueueCandidateMoves which I kinda want to avoid.)

            // TODO: Perform early-outs to avoid enqueueing plans with worse weight than the current best for that target level

            // fill initial queue will all candidates for the first job
            EnqueueCandidateMoves(build, queue, ImmutableHashSet<PlannedPackage>.Empty, jobDeps[0], 1);

            while (queue.TryDequeue(out var plan, out _))
            {
                if (plan.NextJob == jobDeps.Count)
                {
                    // we've found an optimal layout

                    // first, make sure that this is actually a valid layout (and figure out which dep set we're using for each)
                    var matchingDepOptions = new List<MinerDependencyOption>();
                    foreach (var deps in jobDeps)
                    {
                        var anySetSatisfied = false;
                        foreach (var depSet in deps)
                        {
                            foreach (var dep in depSet.NeededPackages)
                            {
                                var isSatisfied = false;
                                foreach (var planned in plan.Packages)
                                {
                                    if (dep.Matches(planned.Package))
                                    {
                                        isSatisfied = true;
                                        break;
                                    }
                                }

                                if (!isSatisfied) goto NextSet;
                            }

                            anySetSatisfied = true;
                            matchingDepOptions.Add(depSet);
                            break;

                        NextSet:;
                        }

                        if (!anySetSatisfied)
                        {
                            // this job doesn't have its deps satisfied; skip
                            goto NextIter;
                        }
                    }

                    // this is a valid configuration, we're done! build the final plan and exit
                    return BuildCompletePlan(needJobs, matchingDepOptions, plan.Packages);
                }
                else
                {
                    // this is an incomplete job plan, add candidates for the current job in the plan
                    EnqueueCandidateMoves(build, queue, plan.Packages, jobDeps[plan.NextJob], plan.NextJob + 1);
                }

            NextIter:;
            }

            // if we fall out here, we couldn't find a plan which qualifies for everything
            return null;
        }

        private static void EnqueueCandidateMoves(UnityBuild build, PriorityQueue<InProgressPlan, int> queue,
            ImmutableHashSet<PlannedPackage> plan, ImmutableArray<MinerDependencyOption> deps, int nextJob)
        {
            foreach (var depSet in deps)
            {
                var thisPlan = plan;

                foreach (var dep in depSet.NeededPackages)
                {
                    // check whether it's already satisfied first
                    foreach (var planned in thisPlan)
                    {
                        if (dep.Matches(planned.Package))
                        {
                            goto AlreadySatisfied;
                        }
                    }

                    // dep isn't already satisfied, try to find a plan for it
                    var newPlanned = TryGetPlanForPackage(build, dep);

                    if (newPlanned is null)
                    {
                        // could not resolve the package; skip the set (unless we're allowed to miss this)
                        if (dep.AllowMissing)
                        {
                            goto AlreadySatisfied;
                        }
                        else
                        {
                            goto SkipSet;
                        }
                    }

                    thisPlan = thisPlan.Add(newPlanned.Value);

                AlreadySatisfied:;
                }

                // we've worked out a new plan, add it
                var newPlan = new InProgressPlan(thisPlan, nextJob);
                queue.Enqueue(newPlan, newPlan.PlanWeight);

            SkipSet:;
            }
        }

        private static PlannedPackage? TryGetPlanForPackage(UnityBuild build, UnityPackage package)
        {
            if (!build.IsMonolithic)
            {
                var (selectedOs, info) = package.OS switch
                {
                    EditorOS.Windows => (EditorOS.Windows, build.WindowsInfo),
                    EditorOS.Linux => (EditorOS.Linux, build.LinuxInfo),
                    EditorOS.MacOS => (EditorOS.MacOS, build.MacOsInfo),

                    // prefer linux, when available
                    EditorOS.Any when build.LinuxInfo is not null => (EditorOS.Linux, build.LinuxInfo),
                    // then mac, if we have a modular player
                    EditorOS.Any when build.HasModularPlayer => (EditorOS.MacOS, build.MacOsInfo),
                    // then windows
                    EditorOS.Any => (EditorOS.Windows, build.WindowsInfo),

                    _ => throw new ArgumentOutOfRangeException(nameof(package.OS)),
                };

                // we have the platform info for the the OS, now try to select the right module
                if (info is not null)
                {
                    var module = package.Kind switch
                    {
                        UnityPackageKind.Editor => info.Unity,
                        UnityPackageKind.Android => info.Android,
                        UnityPackageKind.WindowsMonoSupport => info.WindowsMono,
                        UnityPackageKind.LinuxMonoSupport => info.LinuxMono,
                        UnityPackageKind.MacMonoSupport => info.MacMono,

                        _ => throw new ArgumentOutOfRangeException(nameof(package.Kind)),
                    };

                    if (module is null)
                    {
                        return null;
                    }

                    return new PlannedPackage(new(package.Kind, selectedOs), module.Url);
                }

                // intentionally fall out to the prior
            }

            // if we're looking at a monolithic build, always try for an Editor component, Windows mode.
            var resultPackage = new UnityPackage(UnityPackageKind.Editor, EditorOS.Windows);
            if (!package.Matches(resultPackage)) return null;

            var editorDownloadPrefix = build.IsLegacyDownload ? "UnitySetup-" : "UnitySetup64-";
            return new PlannedPackage(resultPackage, editorDownloadPrefix + build.ShortVersion + ".exe");
        }

        public readonly record struct PlannedPackage(UnityPackage Package, string Url);

        private sealed record InProgressPlan(
            ImmutableHashSet<PlannedPackage> Packages,
            int NextJob
        )
        {
            public int PlanWeight => Packages.Sum(p => p.Package.HeuristicSize);
        }

        private static JobPlan BuildCompletePlan(List<MinerJob> jobs, List<MinerDependencyOption> selectedDepSet, ImmutableHashSet<PlannedPackage> planned)
        {
            Debug.Assert(jobs.Count == selectedDepSet.Count);

            var allPackages = planned.ToImmutableArray();
            var jobsBuilder = ImmutableArray.CreateBuilder<(ImmutableArray<PlannedPackage> need, MinerJob job)>(jobs.Count);

            for (var i = 0; i < jobs.Count; i++)
            {
                var job = jobs[i];
                var depSet = selectedDepSet[i];

                // build up this list in order
                var needBuilder = ImmutableArray.CreateBuilder<PlannedPackage>(depSet.NeededPackages.Length);
                foreach (var dep in depSet.NeededPackages)
                {
                    foreach (var pkg in allPackages)
                    {
                        if (dep.Matches(pkg.Package))
                        {
                            needBuilder.Add(pkg);
                            break;
                        }
                    }
                }

                jobsBuilder.Add((needBuilder.DrainToImmutable(), job));
            }

            return new(allPackages, jobsBuilder.DrainToImmutable());
        }

        public sealed record JobPlan(
            ImmutableArray<PlannedPackage> Packages,
            ImmutableArray<(ImmutableArray<PlannedPackage> NeedsPackages, MinerJob Job)> Jobs
        );

    }
}
