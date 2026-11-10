using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// This input deliberately contains no gate labels or reference failures.
internal static class ExperimentReplaySelectionReader
{
    internal static ExperimentReplaySelection Read(string checkout, string baseCommit, string candidateCommit, string arm, bool includeWorkingChanges = false)
    {
        if (arm is not ("A" or "B")) throw new ArgumentException("Unknown replay arm.");
        var diff = GitCli.RunForRepository(checkout, "diff", "--name-only", "-z", "--no-renames", baseCommit, candidateCommit, "--");
        if (!diff.ProcessStarted || !diff.Succeeded || diff.DrainTimedOut) throw new InvalidDataException("Pinned source diff unavailable.");
        var paths = diff.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (includeWorkingChanges)
        {
            var mutation = GitCli.RunForRepository(checkout, "diff", "--name-only", "-z", "--no-renames", candidateCommit, "--");
            if (!mutation.ProcessStarted || !mutation.Succeeded || mutation.DrainTimedOut) throw new InvalidDataException("Mutation source diff unavailable.");
            paths.UnionWith(mutation.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries));
        }
        return Select(checkout, paths.Order(StringComparer.Ordinal).ToArray(), arm);
    }

    internal static ExperimentReplaySelection Select(string checkout, IReadOnlyList<string> changedFiles, string arm)
    {
        var routing = new ImpactReplayChangedSourceRouting(checkout);
        var inventory = routing.TestProjects.Select(p => new { Project = p,
            Classes = AcceptanceTestClassSourceScanner.Scan(checkout, p.Sources.Where(File.Exists).ToArray()) }).Where(p => p.Classes.Count > 0).ToArray();
        var projects = inventory.Select(p => Path.GetRelativePath(checkout, p.Project.Path).Replace('\\', '/')).ToArray();
        var descriptors = inventory.SelectMany(p => p.Classes)
            .DistinctBy(c => c.FullName).ToArray();
        if (descriptors.Length == 0) throw new InvalidDataException("No runnable test inventory.");
        var changedSources = changedFiles.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToArray();
        var changedTests = changedSources.Where(routing.IsTestSource).ToArray();
        var changed = ImpactReplayChangedTestClassSelection.Read(checkout, changedTests, [], routing);
        var diagnostics = new List<string>();
        var selected = new HashSet<string>(changed.QualifiedClasses, StringComparer.Ordinal);
        var fallback = changed.AbandonReason is not null || changed.OtherFiles.Count > 0;
        if (changed.AbandonReason is { } reason) diagnostics.Add(reason);
        if (arm == "B")
        {
            var production = changedSources.Except(changedTests, StringComparer.OrdinalIgnoreCase).ToArray();
            var impact = production.Length == 0 && changedTests.Length > 0 ? ReverseDependencyTestSelection.Resolved([])
                : ReverseDependencyTestImpactReader.Read(checkout, production, bypassCache: true, maximumChangedSourceFiles: 20);
            var sources = new ImpactReplayTestSourceIndex(LaneTestSourceRootResolver.SourceFiles(checkout, LaneTestSourceRootResolver.Resolve(checkout)));
            var guards = SourceReadingGuardTestClasses.Read(sources, descriptors);
            impact = ImpactReplaySelectionAugmentation.Apply(impact, changedFiles, sources, descriptors, guards);
            fallback |= impact.Outcome != ReverseDependencySelectionOutcome.Resolved;
            if (impact.Reason is { } diagnostic) diagnostics.Add(diagnostic);
            foreach (var selector in impact.TestClassNames)
            {
                var matches = descriptors.Where(c => selector.Contains('.') || selector.Contains('+')
                    ? c.FullName == selector : c.FullName.Split('.', '+').Last() == selector).ToArray();
                if (matches.Length != 1) { fallback = true; diagnostics.Add($"Ambiguous/unavailable selector: {selector}"); }
                foreach (var match in matches) selected.Add(match.FullName);
            }
            var settings = AcceptanceGateEngineSettings.Load(checkout);
            var lanes = AcceptanceLaneMembership.ResolveOwnedCollections(settings.InfrastructureTestLanes, descriptors);
            foreach (var lane in lanes.Where(l => IsFloor(l.Name)))
                foreach (var item in descriptors.Where(c => AcceptanceLaneMembership.LanesIncluding(lanes, c.FullName).Any(l => l.Name == lane.Name)))
                    selected.Add(item.FullName);
        }
        fallback |= selected.Count == 0 || selected.Any(s => !descriptors.Any(c => c.FullName == s));
        if (fallback)
        {
            selected.UnionWith(descriptors.Select(c => c.FullName));
            diagnostics.Add("Conservative full-inventory fallback; empty, missing or ambiguous selection cannot pass silently.");
        }
        return new(arm, selected.Order(StringComparer.Ordinal).ToArray(), projects,
            fallback ? "fallback" : "resolved", diagnostics);
    }

    private static bool IsFloor(string name) => name.StartsWith("Process spawning", StringComparison.Ordinal) ||
        name.StartsWith("Isolated process spawning", StringComparison.Ordinal) || name is "Chaos gate" or "Remainder merge train and ratchet";
}
