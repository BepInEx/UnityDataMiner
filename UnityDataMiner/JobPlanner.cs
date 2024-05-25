using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.CommandLine;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace UnityDataMiner
{
    internal class JobPlanner
    {
        public static JobPlan? Plan(ImmutableArray<MinerJob> jobs, UnityBuild build, CancellationToken cancellationToken)
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

            Debug.Assert(needJobs.Count == jobDeps.Count);

            if (needJobs.Count == 0)
            {
                Log.Information("[{Version}] No applicable jobs; no work needs to be done.", build.Version);
                return null;
            }

            Log.Information("[{Version}] {Jobs} jobs to run", build.Version, needJobs.Count);

            var planTree = new PlanTree(build, needJobs, jobDeps);

            return planTree.ComputeFullJobPlan(cancellationToken);
        }

        private sealed class PlanTree(UnityBuild build, List<MinerJob> jobs, List<ImmutableArray<MinerDependencyOption>> jobDeps)
        {
            private readonly UnityBuild build = build;

            private readonly List<MinerJob> jobs = jobs;
            private readonly List<ImmutableArray<MinerDependencyOption>> jobDeps = jobDeps;

            private readonly PriorityQueue<InProgressPlan, int> queue = new();

            private void EnqueueCandidateMoves(ImmutableHashSet<PlannedPackage> curPlan, int jobIndex)
            {
                foreach (var depSet in jobDeps[jobIndex])
                {
                    EnqueueForDepSet(curPlan, depSet, 0, jobIndex);
                }
            }

            // TODO: it would be nice to figure out some early-outs that can enable us to skip large branches of the tree
            private void EnqueueForDepSet(ImmutableHashSet<PlannedPackage> plan, MinerDependencyOption depSet, int depIndex, int jobIndex)
            {
                // enclose in look to try to tail-call opt this explicitly
                do
                {
                    if (depIndex == depSet.NeededPackages.Length)
                    {
                        // no more deps to enqueue, enqueue this plan
                        var newPlan = new InProgressPlan(plan, jobIndex + 1);
                        queue.Enqueue(newPlan, newPlan.PlanWeight); // TODO: compute weight as we go?
                        return;
                    }

                    var dep = depSet.NeededPackages[depIndex];

                    // check whether this dep is already satisfied
                    foreach (var planned in plan)
                    {
                        if (dep.Matches(planned.Package))
                        {
                            goto NextDep;
                        }
                    }

                    // try to find suitable packages
                    if (!build.IsMonolithic)
                    {
                        ReadOnlySpan<(EditorOS selectedOs, UnityBuildInfo? buildInfo)> candidates = dep.OS switch
                        {
                            EditorOS.Windows => [(EditorOS.Windows, build.WindowsInfo)],
                            EditorOS.Linux => [(EditorOS.Linux, build.LinuxInfo)],
                            EditorOS.MacOS => [(EditorOS.MacOS, build.MacOsInfo)],

                            EditorOS.Any => [
                                (EditorOS.Windows, build.WindowsInfo),
                                (EditorOS.Linux, build.LinuxInfo),
                                (EditorOS.MacOS, build.MacOsInfo),
                            ],

                            _ => throw new NotSupportedException(),
                        };

                        if (candidates is [(var selectedOs, { } info)])
                        {
                            // we have a single viable candidate, use it and tail-recurse
                            if (GetPlannedPackageForBuild(info, dep.Kind, selectedOs) is not { } package)
                            {
                                // the single candidate couldn't be fully resolved; try the fallback
                            }
                            else
                            {
                                plan = plan.Add(package);
                                goto NextDep;
                            }
                        }
                        else
                        {
                            // we have multiple candidates, add them all recursively. If none we viable, try the fallback.
                            var anyViable = false;
                            foreach (var t in candidates)
                            {
                                (selectedOs, info) = t;

                                if (GetPlannedPackageForBuild(info, dep.Kind, selectedOs) is not { } package)
                                {
                                    continue;
                                }

                                anyViable = true;
                                EnqueueForDepSet(plan.Add(package), depSet, depIndex + 1, jobIndex);
                            }

                            if (anyViable)
                            {
                                // we've already added stuff for this tree, we're done at this level
                                return;
                            }
                        }
                    }

                    // always fall out to this path

                    // if we're looking at a monolithic build, always try for an Editor component, Windows mode.
                    var resultPackage = new UnityPackage(UnityPackageKind.Editor, EditorOS.Windows);
                    if (!dep.Matches(resultPackage)) goto NoMatch; // dep doesn't meet criteria, can't continue

                    var editorDownloadPrefix = build.IsLegacyDownload ? "UnitySetup-" : "UnitySetup64-";
                    plan = plan.Add(new PlannedPackage(resultPackage, editorDownloadPrefix + build.ShortVersion + ".exe"));
                    goto NextDep;

                NoMatch:
                    // if we found no match, but that's allowed, go to the next dep anyway
                    if (dep.AllowMissing) goto NextDep;
                    return;

                NextDep:
                    depIndex++;
                }
                while (true);
            }

            public JobPlan? ComputeFullJobPlan(CancellationToken cancellationToken)
            {
                EnqueueCandidateMoves(ImmutableHashSet<PlannedPackage>.Empty, 0);

                while (queue.TryDequeue(out var plan, out _))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (plan.NextJob == jobDeps.Count)
                    {
                        // we've found an optimal layout

                        // first, make sure that this is actually a valid layout (and figure out which dep set we're using for each)
                        var matchingDepOptions = new List<MinerDependencyOption>();
                        foreach (var deps in jobDeps)
                        {
                            (MinerDependencyOption depOpt, int skipped)? option = null;
                            foreach (var depSet in deps)
                            {
                                var skipped = 0;
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

                                    if (!isSatisfied)
                                    {
                                        if (dep.AllowMissing)
                                        {
                                            skipped++;
                                            continue;
                                        }
                                        else
                                        {
                                            goto NextSet;
                                        }
                                    }
                                }

                                // make sure we pick the dep set with the least skipped deps
                                if (option is null || skipped < option.Value.skipped)
                                {
                                    option = (depSet, skipped);
                                }

                            NextSet:;
                            }

                            if (option is not (var set, _))
                            {
                                // this job doesn't have its deps satisfied; skip
                                goto NextIter;
                            }
                            else
                            {
                                // this job has deps satisfied
                                matchingDepOptions.Add(set);
                            }
                        }

                        // this is a valid configuration, we're done! build the final plan and exit
                        return BuildCompletePlan(jobs, matchingDepOptions, plan.Packages);
                    }
                    else
                    {
                        // this is an incomplete job plan, add candidates for the current job in the plan
                        EnqueueCandidateMoves(plan.Packages, plan.NextJob);
                    }

                NextIter:;
                }

                Log.Warning("[{Version}] Could not compute viable plan", build.Version);
                return null;
            }
        }

        private static PlannedPackage? GetPlannedPackageForBuild(UnityBuildInfo? info, UnityPackageKind kind, EditorOS selectedOs)
        {
            if (info is null) return null;

            // Don't get support packages from the same OS as the target of the support package
            if (kind is UnityPackageKind.WindowsMonoSupport && selectedOs is EditorOS.Windows) return null;
            if (kind is UnityPackageKind.LinuxMonoSupport && selectedOs is EditorOS.Linux) return null;
            if (kind is UnityPackageKind.MacMonoSupport && selectedOs is EditorOS.MacOS) return null;

            var module = kind switch
            {
                UnityPackageKind.Editor => info.Unity,
                UnityPackageKind.Android => info.Android,
                UnityPackageKind.WindowsMonoSupport => info.WindowsMono,
                UnityPackageKind.LinuxMonoSupport => info.LinuxMono,
                UnityPackageKind.MacMonoSupport => info.MacMono,

                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };

            if (module is null)
            {
                return null;
            }

            return new PlannedPackage(new(kind, selectedOs), module.Url);
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
            var jobsBuilder = ImmutableArray.CreateBuilder<(ImmutableArray<int> need, MinerJob job)>(jobs.Count);

            for (var i = 0; i < jobs.Count; i++)
            {
                var job = jobs[i];
                var depSet = selectedDepSet[i];

                // build up this list in order
                var needBuilder = ImmutableArray.CreateBuilder<int>(depSet.NeededPackages.Length);
                foreach (var dep in depSet.NeededPackages)
                {
                    for (var j = 0; j < allPackages.Length; j++)
                    {
                        var pkg = allPackages[j];
                        if (dep.Matches(pkg.Package))
                        {
                            needBuilder.Add(j);
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
            ImmutableArray<(ImmutableArray<int> NeedsPackages, MinerJob Job)> Jobs
        );

    }
}
