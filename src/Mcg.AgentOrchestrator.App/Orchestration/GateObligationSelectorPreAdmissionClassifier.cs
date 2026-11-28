using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis.CSharp;
using SoloAcceptanceAdmission = Mcg.AgentOrchestrator.App.Orchestration.ConductorBatchLoop.SoloAcceptanceAdmission;
using SoloAcceptanceAdmissionKind = Mcg.AgentOrchestrator.App.Orchestration.ConductorBatchLoop.SoloAcceptanceAdmissionKind;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Admission evidence is derived anew from the current brief and candidate.
internal static class GateObligationSelectorPreAdmissionClassifier
{
    internal enum Kind { Resolved, UniquelyMatched, Ambiguous, Unmatched }

    internal sealed record Classification(string Selector, Kind Kind,
        IReadOnlyList<string> CandidateFullNames, string? MatchedSimpleName = null);

    internal sealed record Result(IReadOnlyList<string> HoldLines)
    {
        internal bool Hold => HoldLines.Count > 0;
    }

    internal static bool IsClassOnly(string selector) => SyntaxFacts.IsValidIdentifier(selector) &&
        !selector.StartsWith('@');

    private static string SimpleName(string fullName) => fullName.Split('.', '+')[^1];

    internal static IReadOnlyList<Classification> Classify(IReadOnlyList<string> selectors,
        IReadOnlyList<AcceptanceTestClassDescriptor> inventory)
    {
        var classes = inventory.Select(item => item.FullName).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        return selectors.Where(IsClassOnly).Select(selector =>
        {
            var exact = classes.Where(name => SimpleName(name) == selector).ToArray();
            if (exact.Length > 0) return new Classification(selector, Kind.Resolved, exact);
            var matches = classes.Where(name => SimpleName(name).Contains(selector, StringComparison.Ordinal)).ToArray();
            return matches.Length switch
            {
                1 => new Classification(selector, Kind.UniquelyMatched, matches, SimpleName(matches[0])),
                0 => new Classification(selector, Kind.Unmatched, classes),
                _ => new Classification(selector, Kind.Ambiguous, matches)
            };
        }).ToArray();
    }

    internal sealed record CandidateInventory(IReadOnlyList<AcceptanceTestClassDescriptor>? Classes,
        string? UnavailableCause = null);

    // Unavailable scans are shared only within the current solo admission pass.
    internal sealed class SoloInventoryPass(DeclaredTestInventoryRevisionStore store, Action<string> emit)
    {
        private readonly Dictionary<(string Revision, string? ProjectHome), CandidateInventory> _unavailable = new();

        internal Action<string> Emit { get; } = emit;

        internal CandidateInventory Read(string revision, string? projectHome,
            Func<(string? Revision, string? UnavailableCause)> readRevision,
            Func<IReadOnlyList<AcceptanceTestClassDescriptor>?> scan)
        {
            // Even on a cache hit, the current goal's worktree must bind to the candidate.
            if (WorktreeBindingFailure(revision, readRevision()) is { } cause) return new(null, cause);
            var key = (revision, projectHome);
            if (_unavailable.TryGetValue(key, out var unavailable)) return unavailable;

            var classes = store.Read(revision, projectHome, () =>
            {
                CandidateInventory result;
                try
                {
                    result = ScanCandidateInventory(revision, readRevision, scan);
                }
                catch (Exception ex)
                {
                    result = new(null, DescribeException(ex));
                }
                if (result.Classes is { Count: > 0 }) return result.Classes;
                _unavailable[key] = result;
                return null;
            });
            return _unavailable.TryGetValue(key, out unavailable) ? unavailable : new(classes);
        }
    }

    internal static SoloAcceptanceAdmission CheckSolo(AgentOrchestratorKernel kernel, ConductorDriver driver, Goal goal,
        ConductorAutonomyPolicy policy, OrchestratorWorkspace? workspace, SoloInventoryPass? inventoryPass,
        SoloAcceptanceAdmission admission)
    {
        if (inventoryPass is null || admission.Kind != SoloAcceptanceAdmissionKind.Admissible) return admission;

        var admissionRevision = admission.Candidate?.BranchHeadSha;
        var revision = admissionRevision;
        try
        {
            var hasClassSelector = GateTestObligationEffectiveSelectors.Current(goal).Any(obligation =>
                GateTestObligationRepositoryKey.IsHome(obligation.RepositoryKey) &&
                GateTestObligationWaivers.FindWaiver(goal, obligation) is null &&
                GateTestObligationEffectiveSelectors.Effective(goal, obligation, kernel.HumanInputRequests)
                    .Selectors.Any(IsClassOnly));
            if (!hasClassSelector) return admission;

            var projection = driver.ProjectCachedGateReadyCandidate(goal, policy);
            if (projection is GateReadyCandidateProjectionResult.Ready ready)
            {
                revision = ready.Projection.CandidateRevision;
                if (admissionRevision is not null && !string.Equals(admissionRevision, revision, StringComparison.Ordinal))
                {
                    EmitSoloInventoryWarning(goal, revision, $"solo admission branch HEAD changed from {admissionRevision}",
                        inventoryPass.Emit);
                    return admission;
                }
                if (workspace is not null)
                {
                    var root = new Lazy<string>(() => workspace.ResolveExecutionDirectory(goal.Id));
                    var projectHome = workspace.ProjectHomeDirectoryOrNull;
                    return CheckSoloWithStore(kernel, goal, workspace.OrchestratorDirectory, revision, projectHome,
                        inventoryPass, () => ReadWorktreeRevision(root.Value),
                        () => ReadDeclaredInventory(root.Value, projectHome), admission);
                }
            }

            var cause = projection is GateReadyCandidateProjectionResult.Excluded excluded
                ? $"ready candidate projection unavailable ({excluded.Reason})"
                : "goal workspace is unavailable";
            EmitSoloInventoryWarning(goal, revision, cause, inventoryPass.Emit);
        }
        catch (Exception ex)
        {
            EmitSoloInventoryWarning(goal, revision, DescribeException(ex), inventoryPass.Emit);
        }
        return admission;
    }

    internal static SoloAcceptanceAdmission CheckSoloWithStore(AgentOrchestratorKernel kernel, Goal goal,
        string? orchestratorDirectory, string revision, string? projectHome, SoloInventoryPass? inventoryPass,
        Func<(string? Revision, string? UnavailableCause)> readRevision,
        Func<IReadOnlyList<AcceptanceTestClassDescriptor>?> scan, SoloAcceptanceAdmission admission)
    {
        if (inventoryPass is null || admission.Kind != SoloAcceptanceAdmissionKind.Admissible) return admission;

        var result = ApplyWithCandidateInventory(kernel, goal, orchestratorDirectory, revision, () =>
        {
            try
            {
                return inventoryPass.Read(revision, projectHome, readRevision, scan);
            }
            catch (Exception ex)
            {
                return new(null, DescribeException(ex));
            }
        }, inventoryPass.Emit, soloAdmission: true);
        return result.Hold
            ? admission with { Kind = SoloAcceptanceAdmissionKind.UnresolvedObligationSelector, Reason = result.HoldLines[0] }
            : admission;
    }

    private static string DescribeException(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    private static void EmitSoloInventoryWarning(Goal goal, string? revision, string cause, Action<string> emit) =>
        emit($"GATE_OBLIGATION_SELECTOR_PRE_ADMISSION_WARNING goal={goal.Id.Value[..8]} " +
            $"candidate={revision ?? "none"} result=admitted cause={cause.ReplaceLineEndings(" ")}");

    // The scanner reads a worktree. Both observations must bind it to this admission candidate.
    internal static CandidateInventory ReadCandidateInventory(
        string candidateRevision, Func<(string? Revision, string? UnavailableCause)> readRevision,
        Func<IReadOnlyList<AcceptanceTestClassDescriptor>> scan)
    {
        if (!ConductorGitRevisionReader.IsValid(candidateRevision)) return new(null, "invalid candidate revision");
        if (WorktreeBindingFailure(candidateRevision, readRevision()) is { } cause) return new(null, cause);
        return ScanCandidateInventory(candidateRevision, readRevision, scan);
    }

    private static string? WorktreeBindingFailure(string candidateRevision, (string? Revision, string? UnavailableCause) observed) =>
        observed.UnavailableCause ?? (string.Equals(observed.Revision, candidateRevision, StringComparison.Ordinal)
            ? null : $"candidate revision mismatch (worktree HEAD {observed.Revision})");

    private static CandidateInventory ScanCandidateInventory(string candidateRevision,
        Func<(string? Revision, string? UnavailableCause)> readRevision,
        Func<IReadOnlyList<AcceptanceTestClassDescriptor>?> scan)
    {
        var inventory = scan();
        var after = readRevision();
        if (after.UnavailableCause is { } afterCause) return new(null, afterCause);
        return string.Equals(after.Revision, candidateRevision, StringComparison.Ordinal)
            ? new(inventory)
            : new(null, $"worktree revision changed during inventory scan (worktree HEAD {after.Revision})");
    }

    internal static IReadOnlyList<AcceptanceTestClassDescriptor> ReadDeclaredInventory(string root,
        string? projectHome = null)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(AcceptanceManifestLocator.Resolve(root, projectHome)));
        var checks = manifest.RootElement.GetProperty("checks")
            .Deserialize<GoalAcceptanceVerifier.AcceptanceManifestCheck[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        var projects = checks.Where(check => check.Project?.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) == true)
            .Select(check => check.Project!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (projects.Length == 0) throw new InvalidDataException("No declared test projects in acceptance manifest.");
        // Lane roots are only a partition of the test inventory. Whole-project checks can
        // declare additional classes, and a missing project must not imply their absence.
        return projects.SelectMany(project => AcceptanceTestClassSourceScanner.ScanProject(root, project))
            .DistinctBy(item => item.FullName, StringComparer.Ordinal)
            .OrderBy(item => item.FullName, StringComparer.Ordinal).ToArray();
    }

    internal static Result Apply(AgentOrchestratorKernel kernel, Goal goal, string? orchestratorDirectory,
        string candidateRevision, Func<IReadOnlyList<AcceptanceTestClassDescriptor>?> readInventory, Action<string> emit) =>
        ApplyWithCandidateInventory(kernel, goal, orchestratorDirectory, candidateRevision, () => new(readInventory()), emit);

    internal static Result ApplyWithCandidateInventory(AgentOrchestratorKernel kernel, Goal goal, string? orchestratorDirectory,
        string candidateRevision, Func<CandidateInventory> readInventory, Action<string> emit, bool soloAdmission = false)
    {
        var pending = orchestratorDirectory is null
            ? GateTestObligationEffectiveSelectors.Current(goal)
                .Where(obligation => GateTestObligationWaivers.FindWaiver(goal, obligation) is null).ToArray()
            : GateTestObligationEvaluator.Evaluate(goal, orchestratorDirectory, candidateRevision,
                answeredRequests: kernel.HumanInputRequests)
            .Where(result => GateTestObligationRepositoryKey.IsHome(result.Obligation.RepositoryKey) &&
                result.Outcome is not (GateTestObligationEvaluator.Outcome.Satisfied or
                    GateTestObligationEvaluator.Outcome.Waived or GateTestObligationEvaluator.Outcome.TraitExcluded or
                    GateTestObligationEvaluator.Outcome.AllSkipped))
            .Select(result => result.Obligation).ToArray();
        var inventory = new Lazy<CandidateInventory>(() => ConductorGitRevisionReader.IsValid(candidateRevision)
            ? readInventory() : new(null, "invalid candidate revision"));
        var holds = new List<string>();
        foreach (var obligation in pending.Where(item => GateTestObligationRepositoryKey.IsHome(item.RepositoryKey)))
        {
            var effective = GateTestObligationEffectiveSelectors.Effective(goal, obligation, kernel.HumanInputRequests);
            foreach (var selector in effective.Selectors.Where(IsClassOnly).Distinct(StringComparer.Ordinal))
            {
                void Hold(string detail)
                {
                    if (soloAdmission)
                        detail = $"next: obligation-rebind {goal.Id.Value[..8]} {selector} <DeclaredClass> --text-file <path>; {detail}";
                    var reason = LandingExecutor.FormatObligationHoldLine(obligation, "selector-not-found", [selector], detail);
                    holds.Add(soloAdmission ? ConductorDiagnosticText.BoundSingleLine(reason) : reason);
                }
                try
                {
                    var rebinds = AnsweredSelectorRebinding.For(goal, obligation, kernel.HumanInputRequests);
                    var existing = rebinds.SingleOrDefault(item => item.OldClass == selector);
                    var considered = existing?.NewClass ?? selector;
                    var candidateInventory = inventory.Value;
                    if (candidateInventory.Classes is not { Count: > 0 } classes)
                    {
                        var cause = candidateInventory.UnavailableCause ?? (candidateInventory.Classes is null
                            ? "inventory reader returned no inventory" : "declared inventory is empty");
                        if (soloAdmission)
                        {
                            EmitSoloInventoryWarning(goal, candidateRevision, cause, emit);
                            return new([]);
                        }
                        Hold($"inventory unavailable at candidate {candidateRevision}: {cause}; candidate classes considered: none");
                        continue;
                    }
                    var classification = Classify([considered], classes).Single();
                    if (classification.Kind == Kind.Resolved) continue;
                    var candidates = string.Join(", ", classification.CandidateFullNames);
                    if (classification.Kind != Kind.UniquelyMatched)
                    {
                        var candidateClasses = candidates.Length == 0 ? "none" : candidates;
                        Hold($"candidate {candidateRevision}; effective selector {considered}; " +
                            $"candidate classes considered: {candidateClasses}");
                        if (!soloAdmission)
                            GateObligationSelectorDeveloperRouting.Route(
                                kernel, goal.Id, obligation.Id, selector, candidateRevision, classification, candidateClasses, emit);
                        continue;
                    }

                    var replacement = classification.MatchedSimpleName!;
                    // The evaluator follows one recorded pair, not a chain of successive suggestions.
                    if (existing is not null)
                    {
                        Hold($"recorded rebind {selector} becomes {considered} unresolved at candidate {candidateRevision}; " +
                            $"candidate classes considered: {candidates}");
                        continue;
                    }
                    ObligationRebindIntentHandler.ValidatePair(selector, replacement);
                    if (!HasRecordedPair(kernel, goal, obligation, selector, replacement))
                    {
                        var request = kernel.RecordAnsweredOperatorHumanInput(goal.Id,
                            $"Pre-admission obligation rebind: selector {selector} matched only declared class {candidates} " +
                                $"(new class {replacement}) at candidate {candidateRevision}",
                            $"{selector} becomes {replacement}");
                        emit($"GATE_TEST_OBLIGATION_PRE_ADMISSION_REBOUND id={obligation.Id[..Math.Min(8, obligation.Id.Length)]} " +
                            $"old={selector} new={replacement} candidate={candidateRevision} request={request.Id.Value[..8]}");
                    }
                    if (!AnsweredSelectorRebinding.For(goal, obligation, kernel.HumanInputRequests)
                        .Any(item => item.OldClass == selector && item.NewClass == replacement))
                        Hold($"rebind {selector} becomes {replacement} not effective at candidate {candidateRevision}; " +
                            $"candidate classes considered: {candidates}");
                }
                catch (Exception ex)
                {
                    Hold($"pre-admission classification unavailable at candidate {candidateRevision}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        return new(holds);
    }

    internal static IReadOnlySet<string> HeldGoalIds(AgentOrchestratorKernel kernel,
        IReadOnlyList<Goal> eligibleGoals, IReadOnlyList<ConductorSpeculativeAcceptanceCandidate> candidates,
        OrchestratorWorkspace? workspace, Action<string> emit, DeclaredTestInventoryRevisionStore? inventoryStore = null)
    {
        var held = new HashSet<string>(StringComparer.Ordinal);
        var goals = eligibleGoals.ToDictionary(goal => goal.Id);
        var store = inventoryStore ?? new DeclaredTestInventoryRevisionStore();
        foreach (var candidate in candidates)
        {
            if (!goals.TryGetValue(candidate.GoalId, out var goal) ||
                candidate.ProjectionResult is not GateReadyCandidateProjectionResult.Ready ready) continue;
            var revision = ready.Projection.CandidateRevision;
            try
            {
                var result = ApplyWithCandidateInventory(kernel, goal, workspace?.OrchestratorDirectory, revision, () =>
                {
                    if (workspace is null) return new(null, "goal workspace is unavailable");
                    var projectHome = workspace.ProjectHomeDirectoryOrNull;
                    string? unavailableCause = null;
                    var classes = store.Read(revision, projectHome, () =>
                    {
                        var root = workspace.ResolveExecutionDirectory(goal.Id);
                        var inventory = ReadCandidateInventory(revision, () => ReadWorktreeRevision(root),
                            () => ReadDeclaredInventory(root, projectHome));
                        unavailableCause = inventory.UnavailableCause;
                        return inventory.Classes;
                    });
                    return new(classes, unavailableCause);
                }, emit);
                if (!result.Hold) continue;
                held.Add(goal.Id.Value);
                foreach (var line in result.HoldLines) emit(line);
            }
            catch (Exception ex)
            {
                held.Add(goal.Id.Value);
                foreach (var obligation in GateTestObligationEffectiveSelectors.Current(goal))
                    emit(LandingExecutor.FormatObligationHoldLine(obligation, "pending", obligation.Selectors,
                        $"pre-admission classification unavailable at candidate {revision}: {ex.GetType().Name}: {ex.Message}"));
            }
        }
        return held;
    }

    internal static (string? Revision, string? UnavailableCause) ReadWorktreeRevision(string root)
    {
        var status = GitCli.Run(root, "status", "--porcelain", "--untracked-files=normal");
        if (!status.Succeeded || status.DrainTimedOut) return (null, "git status failed");
        if (!string.IsNullOrWhiteSpace(status.Output)) return (null, "worktree is not clean at the candidate");
        var head = GitCli.Run(root, "rev-parse", "--verify", "HEAD");
        return head.Succeeded && !head.DrainTimedOut ? (head.Output.Trim(), null) : (null, "git rev-parse failed");
    }

    private static bool HasRecordedPair(AgentOrchestratorKernel kernel, Goal goal, GateTestObligation obligation,
        string oldClass, string newClass) => kernel.HumanInputRequests
        .Where(request => request.GoalId == goal.Id && !request.WasDismissed)
        .Select(request => request.AuthoritativeAnswer)
        .Any(answer => answer is { Origin: HumanInputAnswerOrigin.Operator, IsAutoDefaulted: false } &&
            answer.BriefVersion == obligation.BriefVersion &&
            AnsweredSelectorRebinding.Parse(answer.Text).Contains((oldClass, newClass)));
}
