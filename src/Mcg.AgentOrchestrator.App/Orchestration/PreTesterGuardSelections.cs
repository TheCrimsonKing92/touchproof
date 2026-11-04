using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns guard augmentation, resume membership, and changed-file guard attribution together.
internal sealed class PreTesterGuardSelections
{
    private readonly DeveloperDeferredTestSelection _changedFileGuards;
    private readonly IReadOnlyList<FindingEvidenceSelection> _cliSelections;
    internal IReadOnlyList<FindingEvidenceSelection> Selections { get; }
    internal IReadOnlyList<string> NotRun => _changedFileGuards.NotRun;

    private PreTesterGuardSelections(DeveloperDeferredTestSelection changedFileGuards,
        IReadOnlyList<FindingEvidenceSelection> selections, IReadOnlyList<FindingEvidenceSelection> cliSelections)
    {
        _changedFileGuards = changedFileGuards;
        Selections = selections;
        _cliSelections = cliSelections;
    }

    internal static PreTesterGuardSelections Create(PreTesterChangedFileGuardClasses cache,
        string worktreePath, IReadOnlyList<string> changedPaths, string mainRevision,
        Action<string>? progress = null, AcceptanceGateEngineSettings? settings = null,
        IReadOnlyList<string>? priorSelections = null)
    {
        var (changedFileGuards, provenance) = cache.SelectWithProvenance(worktreePath, changedPaths, mainRevision);
        foreach (var selection in changedFileGuards.Selections)
        {
            var guard = provenance.Single(item => item.Class == selection.TestClass);
            (progress ?? Console.WriteLine)(
                $"PRE_TESTER_GUARD_SELECTED class={guard.Class} kind={guard.Kind} path={guard.Path}");
        }
        var cli = priorSelections is null
            ? PreTesterCliSurfaceSelections.Select(worktreePath, changedPaths, settings)
            : (Selections: (IReadOnlyList<FindingEvidenceSelection>)[],
                Provenance: (IReadOnlyList<PreTesterChangedFileGuardClasses.Provenance>)[]);
        foreach (var reason in cli.Provenance)
            (progress ?? Console.WriteLine)(
                $"PRE_TESTER_GUARD_SELECTED class={reason.Class} kind={reason.Kind} path={reason.Path}");
        return new(changedFileGuards, PreTesterAlwaysRunGuardTestClasses.Select(worktreePath)
            .Concat(PreTesterTestSourceGuardClasses.Select(worktreePath, changedPaths))
            .Concat(changedFileGuards.Selections).Concat(cli.Selections).ToArray(), cli.Selections);
    }

    internal static string ReadMainRevision(string worktreePath, OrchestratorWorkspace? workspace)
    {
        // Delegate-only driver fixtures have no workspace or integration ref to resolve.
        if (workspace is null) return "fixture-main-revision";
        return GoalWorktrees.TryResolveCandidateMergeBase(worktreePath, workspace.IntegrationBranch, out var revision)
            ? revision
            : throw new InvalidDataException("Pre-Tester changed-file guards could not resolve the candidate merge base.");
    }

    internal DeveloperDeferredTestSelection NormalizeWithNotRun(AcceptanceGateEngineSettings settings)
    {
        return DeveloperDeferredTestSelectionNormalization.Normalize(new(Selections, NotRun), settings);
    }

    internal DeveloperDeferredTestSelection Augment(DeveloperDeferredTestSelection declaration,
        AcceptanceGateEngineSettings settings, PreTesterEvidenceEntry? prior,
        Func<FindingEvidenceSelection, string> format)
    {
        var selected = declaration.Selections.ToList();
        var notRun = declaration.NotRun.ToList();
        var normalizedGuards = NormalizeWithNotRun(settings);
        notRun.AddRange(normalizedGuards.NotRun.Where(name => prior?.Outcome != "started" ||
            prior.NotRun.Contains(name, StringComparer.Ordinal)));
        selected.AddRange(normalizedGuards.Selections);
        if (prior?.Outcome == "started")
        {
            // CLI discovery belongs to attempt creation. Recorded obligations survive later inventory edits.
            selected.AddRange(PreTesterCliSurfaceSelections.RecordedSelections(prior.Selections));
            notRun = prior.NotRun.ToList();
        }
        return new(RetainStartedSelections(selected,
            prior?.Outcome == "started" ? prior.Selections : null, format), notRun);
    }

    internal FindingEvidenceSelection[] RetainStartedSelections(
        IEnumerable<FindingEvidenceSelection> selected, IReadOnlyList<string>? priorSelections,
        Func<FindingEvidenceSelection, string> format)
    {
        var cli = priorSelections is null ? _cliSelections : PreTesterCliSurfaceSelections.RecordedSelections(priorSelections).ToArray();
        // Legacy declarations name the umbrella project. Its executor routes these same classes
        // to CLI; canonicalize that overlap before normal duplicate elimination.
        selected = selected.Select(selection => selection.TestProject == "Infrastructure.Tests" &&
            priorSelections?.Contains(format(selection), StringComparer.OrdinalIgnoreCase) != true
            ? cli.FirstOrDefault(item => item.TestClass == selection.TestClass) ?? selection : selection);
        // Pre-change attempts keep their original request identity and per-class TRX obligation.
        return selected.DistinctBy(format).Where(selection => priorSelections is null ||
            !(PreTesterAlwaysRunGuardTestClasses.ContainsClass(selection.TestClass) ||
              PreTesterTestSourceGuardClasses.ContainsClass(selection.TestClass) ||
              PreTesterChangedFileGuardClasses.ContainsClass(_changedFileGuards.Selections, selection.TestClass)) ||
            priorSelections.Contains(format(selection), StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    internal bool EveryFailureIsInsideChanges(IReadOnlyList<string> failures,
        Func<IReadOnlyList<string>, bool> otherFailuresAreInsideChanges)
    {
        var otherFailures = failures.Where(test => !PreTesterChangedFileGuardClasses.ContainsClass(
            _changedFileGuards.Selections, AcceptanceTestSourceResolver.ExtractClassName(test))).ToArray();
        return otherFailures.Length == 0 || otherFailuresAreInsideChanges(otherFailures);
    }
}
