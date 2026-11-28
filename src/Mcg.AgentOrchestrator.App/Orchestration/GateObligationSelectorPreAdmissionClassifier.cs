using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis.CSharp;

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

    // The scanner reads a worktree. Both observations must bind it to this admission candidate.
    internal static CandidateInventory ReadCandidateInventory(
        string candidateRevision, Func<(string? Revision, string? UnavailableCause)> readRevision,
        Func<IReadOnlyList<AcceptanceTestClassDescriptor>> scan)
    {
        if (!ConductorGitRevisionReader.IsValid(candidateRevision)) return new(null, "invalid candidate revision");
        var before = readRevision();
        if (before.UnavailableCause is { } beforeCause) return new(null, beforeCause);
        if (!string.Equals(before.Revision, candidateRevision, StringComparison.Ordinal))
            return new(null, $"candidate revision mismatch (worktree HEAD {before.Revision})");

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
        string candidateRevision, Func<CandidateInventory> readInventory, Action<string> emit)
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
                void Hold(string detail) => holds.Add(LandingExecutor.FormatObligationHoldLine(
                    obligation, "selector-not-found", [selector], detail));
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
