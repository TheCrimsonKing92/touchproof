using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns guard augmentation, resume membership, and changed-file guard attribution together.
internal sealed class PreTesterGuardSelections
{
    private readonly DeveloperDeferredTestSelection _changedFileGuards;
    private readonly IReadOnlyList<FindingEvidenceSelection> _guardSelections;
    private readonly IReadOnlyList<FindingEvidenceSelection> _cliSelections;
    private readonly IReadOnlyList<FindingEvidenceSelection> _coupledSelections;
    private readonly IReadOnlyList<FindingEvidenceSelection> _reverseDependencySelections;
    private readonly bool _hasShippingSourceChanges;
    internal IReadOnlyList<FindingEvidenceSelection> Selections { get; }
    internal IReadOnlyList<string> NotRun => _changedFileGuards.NotRun;

    private PreTesterGuardSelections(DeveloperDeferredTestSelection changedFileGuards,
        IReadOnlyList<FindingEvidenceSelection> selections, IReadOnlyList<FindingEvidenceSelection> cliSelections,
        IReadOnlyList<FindingEvidenceSelection> coupledSelections,
        IReadOnlyList<FindingEvidenceSelection> reverseDependencySelections, bool hasShippingSourceChanges)
    {
        _changedFileGuards = changedFileGuards;
        _guardSelections = selections;
        Selections = selections.Concat(cliSelections).Concat(coupledSelections).Distinct().ToArray();
        _cliSelections = cliSelections;
        _coupledSelections = coupledSelections;
        _reverseDependencySelections = reverseDependencySelections;
        _hasShippingSourceChanges = hasShippingSourceChanges;
    }

    internal static PreTesterGuardSelections Create(PreTesterChangedFileGuardClasses cache,
        string worktreePath, IReadOnlyList<string> changedPaths, string mainRevision,
        Action<string>? progress = null, AcceptanceGateEngineSettings? settings = null,
        IReadOnlyList<string>? priorSelections = null,
        Func<string, string, IReadOnlyList<string>?>? structuralPathReader = null,
        Func<string, IReadOnlyList<string>, RepositoryRecheckTestSelection>? reverseDependencySelector = null,
        Func<IReadOnlyList<string>, ReverseDependencyEvidenceCostRule.Decision>? reverseDependencyCostDecision = null)
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
        var structuralPaths = changedPaths.Any(PreTesterLiveTreeStructuralSelections.IsTreePath)
            ? (structuralPathReader ?? PreTesterCoupledClassSelections.ReadStructuralPaths)(worktreePath, mainRevision)
            : [];
        var coupled = PreTesterCoupledClassSelections.Select(worktreePath, changedPaths, settings, progress, structuralPaths);
        foreach (var reason in coupled.Provenance)
            (progress ?? Console.WriteLine)(
                $"PRE_TESTER_GUARD_SELECTED class={reason.Class} kind={reason.Kind} path={reason.Path}");
        var reverse = PreTesterReverseDependencySelections.Select(worktreePath, changedPaths, progress,
            reverseDependencySelector, reverseDependencyCostDecision);
        foreach (var reason in reverse.Provenance)
            (progress ?? Console.WriteLine)(
                $"PRE_TESTER_GUARD_SELECTED class={reason.Class} kind={reason.Kind} path={reason.Path}");
        return new(changedFileGuards, PreTesterAlwaysRunGuardTestClasses.Select(worktreePath)
            .Concat(PreTesterTestSourceGuardClasses.Select(worktreePath, changedPaths))
            .Concat(changedFileGuards.Selections).ToArray(), cli.Selections,
            coupled.Selections.Concat(reverse.Selections).Distinct().ToArray(), reverse.Selections,
            PreTesterReverseDependencySelections.ShippingSourcePaths(changedPaths).Length != 0);
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
        var started = prior?.Outcome == "started";
        var normalizedGuards = DeveloperDeferredTestSelectionNormalization.Normalize(new(_guardSelections, NotRun), settings);
        notRun.AddRange(normalizedGuards.NotRun.Where(name => prior?.Outcome != "started" ||
            prior.NotRun.Contains(name, StringComparer.Ordinal)));
        selected.AddRange(normalizedGuards.Selections);
        var normalizedCli = DeveloperDeferredTestSelectionNormalization.Normalize(new(_cliSelections, []), settings);
        // CLI discovery belongs to creation; restore its recorded block before coupled selections.
        selected.AddRange(started ? PreTesterCliSurfaceSelections.RecordedSelections(prior!.Selections) : normalizedCli.Selections);
        // Retain project identity for rejected CLI obligations, so resume need not rediscover their origin.
        notRun.AddRange(started ? PreTesterCliSurfaceSelections.RecordedSelections(prior!.NotRun).Select(format) :
            normalizedCli.NotRun.Select(name => format(new FindingEvidenceSelection(
                GoalAcceptanceVerifier.ProjectLabel(PreTesterCliSurfaceSelections.Project), name))));
        var normalizedCoupled = DeveloperDeferredTestSelectionNormalization.Normalize(new(_coupledSelections, []), settings);
        selected.AddRange(normalizedCoupled.Selections);
        notRun.AddRange(normalizedCoupled.NotRun.Where(name => prior?.Outcome != "started" ||
            prior.NotRun.Contains(name, StringComparer.Ordinal)));
        // Recordings have no provenance field. Keep their Infrastructure block authoritative
        // when dependency recomputation returns fewer classes or becomes unavailable.
        if (started && _hasShippingSourceChanges)
            selected.AddRange(PreTesterReverseDependencySelections.RecordedSelections(prior!.Selections));
        return new(RetainStartedSelections(selected,
            prior?.Outcome == "started" ? prior.Selections : null, format), notRun);
    }

    internal static DeveloperDeferredTestSelection ReconcileStarted(PreTesterEvidenceEntry prior,
        IReadOnlyList<FindingEvidenceSelection> recomputed, IReadOnlyList<string> notRun,
        Func<FindingEvidenceSelection, string> format, Action<string> progress)
    {
        var names = recomputed.Select(format).ToArray();
        var missing = prior.Selections.Except(names, StringComparer.OrdinalIgnoreCase).ToArray();
        var added = names.Except(prior.Selections, StringComparer.OrdinalIgnoreCase).ToArray();
        var notRunMissing = prior.NotRun.Except(notRun, StringComparer.Ordinal).ToArray();
        var notRunAdded = notRun.Except(prior.NotRun, StringComparer.Ordinal).ToArray();
        var malformed = prior.Selections.Where(name => !name.Contains(':')).ToArray();
        if (missing.Length + added.Length + notRunMissing.Length + notRunAdded.Length + malformed.Length != 0)
        {
            static string List(string[] items) => items.Length == 0 ? "<none>" : string.Join(",", items);
            var detail = $"missing={List(missing)} added={List(added)} " +
                $"not_run_missing={List(notRunMissing)} not_run_added={List(notRunAdded)}";
            if (malformed.Length > 0) detail += $" malformed={List(malformed)}";
            progress($"PRE_TESTER_STARTED_SELECTION_DRIFT receipt={prior.ReceiptId} {detail}");
        }

        // Equality ignores order, casing and duplicates; request identity retains the exact recording.
        var recorded = prior.Selections.Where(name => name.Contains(':')).Select(name =>
        {
            var separator = name.IndexOf(':');
            return new FindingEvidenceSelection(name[..separator], name[(separator + 1)..]);
        }).ToArray();
        return new(recorded, prior.NotRun);
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
              PreTesterChangedFileGuardClasses.ContainsClass(_changedFileGuards.Selections, selection.TestClass) ||
              _coupledSelections.Any(item => format(item).Equals(format(selection), StringComparison.OrdinalIgnoreCase))) ||
            priorSelections.Contains(format(selection), StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    internal bool EveryFailureIsInsideChanges(IReadOnlyList<string> failures,
        Func<IReadOnlyList<string>, bool> otherFailuresAreInsideChanges)
    {
        var otherFailures = failures.Where(test => !PreTesterChangedFileGuardClasses.ContainsClass(
            _changedFileGuards.Selections, AcceptanceTestSourceResolver.ExtractClassName(test)) &&
            !PreTesterChangedFileGuardClasses.ContainsClass(
                _reverseDependencySelections, AcceptanceTestSourceResolver.ExtractClassName(test))).ToArray();
        return otherFailures.Length == 0 || otherFailuresAreInsideChanges(otherFailures);
    }
}

// Owns recovery of the latest candidate-bound record without rewriting historical messages.
internal static class PreTesterStoredEntryRecovery
{
    internal static (PreTesterEvidenceEntry? Entry, bool Healed) ReadAndHeal(
        Goal goal, TaskId testerId, string? candidateSha, Action<string> record)
    {
        if (string.IsNullOrWhiteSpace(candidateSha)) return (null, false);
        foreach (var evt in goal.Timeline.Reverse())
        {
            if (evt.TaskId != testerId ||
                evt.Kind is not (ProgressKind.FindingEvidenceRequestRecorded or ProgressKind.FindingEvidenceRunRecorded)) continue;
            if (PreTesterEvidenceIndexLines.Parse(evt.Message) is not { } entry ||
                !string.Equals(entry.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase)) continue;
            // The index parser splits on semicolons, so inspect the original record for the full marker.
            if (entry.Outcome != "started" ||
                !Regex.IsMatch(evt.Message, @"\[omitted [0-9]+ characters; goal id: ", RegexOptions.CultureInvariant))
                return (entry, false);

            PreTesterStartedEvidence.TryAbandon(entry, "TruncatedStoredMessage", record);
            Console.WriteLine($"PRE_TESTER_STARTED_ENTRY_TRUNCATED receipt={entry.ReceiptId} " +
                $"candidate_sha={candidateSha} abandoned_reason=truncated-stored-message");
            return (null, true);
        }
        return (null, false);
    }
}
