using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class RecurringUnrelatedTestDisposition(
    Func<DateTimeOffset, IReadOnlyList<RecurringTestFailureOccurrence>> readHistory,
    Func<IReadOnlyList<RecurringTestPassedGate>> readPasses,
    RecurringTestFixItemFiler filer,
    Func<GoalId, string, int> countRegates,
    Func<GoalId, string, DateTimeOffset, bool> appendRegate,
    Func<DateTimeOffset> utcNow)
{
    internal const string RegateKey = "recurring-unrelated-regate";
    internal const string HoldKey = "recurring-unrelated-test-hold";

    internal ConductorAdvanceResult? TryDispose(Goal goal, string prefix, ConductorAutonomyPolicy policy,
        AcceptanceVerificationSummary acceptance, ApparatusRedGateReading reading,
        Action<Goal, string> restoreVerified,
        Action<Goal, IReadOnlyList<string>, string?, string?, IReadOnlyList<AcceptanceCheckAttribution>?, string?> recordFailure,
        Action<Goal, GoalLifecycleState, string> recordEscalation)
    {
        var now = utcNow();
        var history = readHistory(now);
        // An unreadable fix store cannot create positive clearing evidence; recurrence still routes safely.
        DateTimeOffset? ClosedAt(string key)
        {
            try { return filer.ClosedAt(key); }
            catch (Exception) { return null; }
        }
        var verdict = RecurringUnrelatedTestRule.Evaluate(goal.Id.Value, reading, history, [], ClosedAt, now);
        if (verdict is not null)
            verdict = RecurringUnrelatedTestRule.Evaluate(goal.Id.Value, reading, history, readPasses(), ClosedAt, now);
        if (verdict is null) return null;
        var itemIds = verdict.RecurringTests.Select(match => TryFile(goal.Id.Value, match.Test,
            match.Occurrences, acceptance.BranchHeadSha, now, attemptId: acceptance.AttemptId)).Distinct().ToArray();
        var candidate = acceptance.BranchHeadSha;
        var count = string.IsNullOrWhiteSpace(candidate) ? RecurringUnrelatedTestRule.RegateCap : countRegates(goal.Id, candidate);
        var regate = count < RecurringUnrelatedTestRule.RegateCap;
        var boundEvidence = string.IsNullOrWhiteSpace(candidate) ? "candidate-unknown" : "cap-exhausted";
        if (regate)
        {
            regate = appendRegate(goal.Id, candidate!, now);
            boundEvidence = regate ? "bound-receipt-recorded" : "bound-write-not-observed";
        }
        var tests = string.Join(", ", verdict.RecurringTests.Select(match => match.Test.TestIdentity));
        var otherGoals = verdict.RecurringTests.SelectMany(match => match.Occurrences).Select(row => row.GoalId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var items = string.Join(", ", itemIds.Select(id => id ?? "fix item not filed"));
        var reason = $"recurring-unrelated tests={tests}; candidate={candidate ?? "unknown"}; other goals={otherGoals}; " +
            $"no worker was reopened and no retry was counted. " +
            (regate ? $"Re-gate {count + 1}/{RecurringUnrelatedTestRule.RegateCap} on next conduct tick. " :
                $"Held: bound={boundEvidence}, count={count}/{RecurringUnrelatedTestRule.RegateCap}. Owner: operator; next action: land or close backlog item. ") +
            $"Fix item: {items}.";
        restoreVerified(goal, reason);
        if (!regate)
        {
            recordFailure(goal, reading.FailedCheckNames, candidate, acceptance.MainHeadSha,
                acceptance.CheckAttributions, acceptance.BaselineAttestation);
            recordEscalation(goal, GoalLifecycleState.Verified, reason);
        }
        return new(goal.Id.Value, prefix, policy.Name,
            new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, reason, $"{HoldKey}:{candidate}")
            {
                Owner = regate ? ConductorHoldOwner.None : ConductorHoldOwner.Operator,
                Decision = new("acceptance", regate ? "regate" : "hold", 0, regate ? RegateKey : HoldKey, reason,
                    [new("candidate", candidate ?? "unknown"), new("fix_items", items), new("tests", tests),
                     new("other_goals", otherGoals.ToString()), new("regate_count", count.ToString()), new("bound_evidence", boundEvidence),
                     new("corroboration", string.Join("; ", verdict.RecurringTests.Select(match => match.Test.TestIdentity + ":" +
                         string.Join(",", match.Occurrences.Select(row => $"{row.GoalId}@{row.At:O}/{row.AttemptId ?? row.CandidateSha}")))))])
            });
    }

    private string? TryFile(string goalId, ApparatusRedFailingTest test,
        IReadOnlyList<RecurringTestFailureOccurrence> occurrences, string? candidate, DateTimeOffset now,
        int minimumGoals = RecurringUnrelatedTestRule.MinimumOtherGoals + 1, string? attemptId = null)
    {
        try { return filer.Ensure(goalId, test, occurrences, candidate, now, minimumGoals, attemptId); }
        catch (RecurringTestFixItemAttentionException ex) { return ex.Message; }
        catch (Exception ex) { return $"fix item not filed ({ex.GetType().Name}: {ex.Message})"; }
    }

    internal void FileInherited(Goal goal, AcceptanceVerificationSummary acceptance, IReadOnlyList<string> identities,
        GoalId earlierGoal, string? candidate)
    {
        var now = utcNow();
        var history = readHistory(now);
        foreach (var identity in identities)
        {
            if (CrossGoalTestFailureHistory.TestKey(identity) is not { } key) continue;
            var check = acceptance.RequiredUnmetCriteria.FirstOrDefault(check => check.FailingTestIdentities?.Contains(identity) == true);
            var test = new ApparatusRedFailingTest(check?.Name ?? "inherited", identity, null, [], false, false,
                FailureMessage: check?.OutputTail);
            DateTimeOffset? closedAt;
            try { closedAt = filer.ClosedAt(key); }
            catch (Exception) { closedAt = null; }
            var occurrences = history.Where(row => row.TestKey == key && (closedAt is null || row.At > closedAt.Value)).ToList();
            // Solo receipts carry no time: after a closure only dated gate occurrences corroborate.
            if (closedAt is null && !occurrences.Any(row => row.GoalId == earlierGoal.Value))
                occurrences.Add(new(key, earlierGoal.Value, now, "solo-inherited-receipt", null, check?.Name, null, null));
            TryFile(goal.Id.Value, test, occurrences, candidate, now, minimumGoals: 2, attemptId: acceptance.AttemptId);
        }
    }

    internal static RecurringUnrelatedTestDisposition? Create(OrchestratorWorkspace? workspace, Func<DateTimeOffset> utcNow)
    {
        if (workspace is null) return null;
        var censusPath = Path.Combine(workspace.OrchestratorDirectory, "acceptance-gate-attempts", AcceptanceFailingTestIndex.FileName);
        var history = new CrossGoalTestFailureHistory(censusPath, workspace.ExecutionDirectory, () => []);
        var ledger = new RecurringUnrelatedRegateLedger(new(censusPath));
        const string attentionPrefix = "recurring-unrelated-test:";
        var filer = new RecurringTestFixItemFiler(new BacklogRecurringTestFixItemStore(workspace.BacklogStorePath),
            (id, body) => CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory).RaiseAsync(CollaborationItemType.Decision, null, $"Repair recurring test: {id}", body,
                attentionPrefix + id).GetAwaiter().GetResult(),
            id => CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory).TryResolveAsync(attentionPrefix + id, "Recurring test fix item closed.").GetAwaiter().GetResult());
        return new(history.ReadOccurrences, () => ReadPassedGates(workspace.ExecutionDirectory), filer, ledger.Count, ledger.Append, utcNow);
    }

    internal static IReadOnlyList<RecurringTestPassedGate> ReadPassedGates(string root)
    {
        try
        {
            return GoalOperationJournal.ReadAll(root).Values.SelectMany(journal => journal.Entries)
                .Where(entry => entry.Operation is "acceptance" or "conductor:acceptance" &&
                    entry.Status == GoalOperationStatus.Completed && entry.AcceptanceOutcome is "passed" or "gate-passed" && entry.HasCandidatePair)
                .Select(entry => new RecurringTestPassedGate(entry.GoalId.Value, entry.BranchHeadSha!, entry.MainHeadSha!, entry.At)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
