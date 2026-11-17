using Mcg.AgentOrchestrator.Core;

// Parallel-safe: fixed timestamps, in-memory goals and explicit retry intents.
public sealed class RoundValueClassifierTests
{
    [Fact]
    public void Classify_Fixture_ReturnsExactOutcomeValueAndCauseSet()
    {
        var actual = RoundValueClassifier.Classify(RoundValueFixture.Create().Goals)
            .Select(r => (r.Round.TaskId, r.Round.RoundIndex, r.Outcome, r.ValueClass, r.WasteCause))
            .OrderBy(r => r.TaskId, StringComparer.Ordinal).ThenBy(r => r.RoundIndex).ToArray();
        (string TaskId, int RoundIndex, RoundGoalOutcome Outcome, RoundValueClass ValueClass, string? WasteCause)[] expected =
        [
            ("a-plan", 1, RoundGoalOutcome.Landed, RoundValueClass.Productive, null),
            ("a-dev", 1, RoundGoalOutcome.Landed, RoundValueClass.Productive, null),
            ("a-dev", 2, RoundGoalOutcome.Landed, RoundValueClass.Productive, null),
            ("a-test", 1, RoundGoalOutcome.Landed, RoundValueClass.Productive, null),
            ("a-test", 2, RoundGoalOutcome.Landed, RoundValueClass.Wasted, "flake-or-apparatus"),
            ("a-rev", 1, RoundGoalOutcome.Landed, RoundValueClass.ExpectedOverhead, null),
            ("a-rev", 2, RoundGoalOutcome.Landed, RoundValueClass.ExpectedOverhead, null),
            ("a-rev", 3, RoundGoalOutcome.Landed, RoundValueClass.Wasted, "unchanged-commit-review"),
            ("b-dev", 1, RoundGoalOutcome.Lost, RoundValueClass.Wasted, "abandoned-goal"),
            ("b-dev", 2, RoundGoalOutcome.Lost, RoundValueClass.Wasted, "abandoned-goal"),
            ("c-dev", 1, RoundGoalOutcome.Pending, RoundValueClass.Wasted, "orphaned-dispatch"),
            ("c-dev", 2, RoundGoalOutcome.Pending, RoundValueClass.Wasted, "unclassified"),
            ("d-plan", 1, RoundGoalOutcome.Landed, RoundValueClass.Productive, null),
            ("d-dev", 1, RoundGoalOutcome.Landed, RoundValueClass.Productive, null)
        ];
        Assert.Equal(expected.OrderBy(r => r.TaskId, StringComparer.Ordinal).ThenBy(r => r.RoundIndex), actual);
    }

    [Fact]
    public void Classify_EveryFamily_AppliesExpectedValueAndCause()
    {
        (ReworkCauseFamily Family, string Message, RoundValueClass Value, string? Cause, OperatorActorKind? Actor)[] cases =
        [
            (ReworkCauseFamily.FirstPass, "free text", RoundValueClass.Productive, null, null),
            (ReworkCauseFamily.ReviewFinding, "auto-review-retry round 1: finding", RoundValueClass.Productive, null, null),
            (ReworkCauseFamily.CandidateRed, "ACTIONABLE_CANDIDATE_RED", RoundValueClass.Productive, null, null),
            (ReworkCauseFamily.GateRed, "Acceptance criteria unmet; retrying task with feedback", RoundValueClass.Productive, null, null),
            (ReworkCauseFamily.Clarification, "answered", RoundValueClass.Productive, null, null),
            (ReworkCauseFamily.EvidenceRerun, "finding evidence-on-demand: receipt", RoundValueClass.ExpectedOverhead, null, null),
            (ReworkCauseFamily.DownstreamRerun, "Invalidated upstream", RoundValueClass.ExpectedOverhead, null, null),
            (ReworkCauseFamily.Routing, "missing-planner-artifact: dependency reroute", RoundValueClass.ExpectedOverhead, null, null),
            (ReworkCauseFamily.FlakeOrApparatus, "pre-review build repair: error", RoundValueClass.Wasted, "flake-or-apparatus", null),
            (ReworkCauseFamily.Environment, "Dispatch hit provider connectivity failure", RoundValueClass.Wasted, "provider-or-environment", null),
            (ReworkCauseFamily.ReviewContractRepair, "review-finding contract-repair: missing", RoundValueClass.Wasted, "review-contract-repair", null),
            (ReworkCauseFamily.OperatorRetry, "free text", RoundValueClass.Wasted, "manual-route", OperatorActorKind.Human),
            (ReworkCauseFamily.StewardRoute, "free text", RoundValueClass.Wasted, "manual-route", OperatorActorKind.Agent),
            (ReworkCauseFamily.Unclassified, "free text", RoundValueClass.Wasted, "unclassified", null)
        ];
        Assert.Equal(Enum.GetValues<ReworkCauseFamily>().Order(), cases.Select(c => c.Family).Order());
        foreach (var item in cases)
        {
            var goal = GoalWith(GoalStatus.Completed, AgentRole.Developer, "c1", "c2", item.Message,
                item.Family == ReworkCauseFamily.Clarification ? ProgressKind.HumanInputReceived : ProgressKind.TaskRetried);
            AppliedRetryIntent[] intents = item.Actor is { } actor
                ? [new("task", RoundValueFixture.Since.AddHours(2), actor)] : [];
            var round = RoundValueClassifier.Classify([goal], intents)
                .Single(r => r.Round.RoundIndex == (item.Family == ReworkCauseFamily.FirstPass ? 1 : 2));
            Assert.Equal(item.Family, round.Round.ReworkCause);
            Assert.Equal(item.Value, round.ValueClass);
            Assert.Equal(item.Cause, round.WasteCause);
        }
    }

    [Theory]
    [InlineData(GoalStatus.Cancelled)]
    [InlineData(GoalStatus.Superseded)]
    [InlineData(GoalStatus.Failed)]
    public void Classify_LostGoal_OverridesOrphanAndUnchangedReview(GoalStatus status)
    {
        var goal = GoalWith(status, AgentRole.Reviewer, "c1", "c1", "Invalidated upstream",
            omitFirstEnding: true);
        var rounds = RoundValueClassifier.Classify([goal]);
        Assert.Equal(WorkerRoundStopCause.Unknown, rounds[0].Round.StopCause);
        Assert.All(rounds, r =>
        {
            Assert.Equal(RoundGoalOutcome.Lost, r.Outcome);
            Assert.Equal(RoundValueClass.Wasted, r.ValueClass);
            Assert.Equal("abandoned-goal", r.WasteCause);
        });
    }

    [Theory]
    [InlineData(null, null, RoundValueClass.ExpectedOverhead, null)]
    [InlineData("", "", RoundValueClass.ExpectedOverhead, null)]
    [InlineData("c1", "C1", RoundValueClass.ExpectedOverhead, null)]
    [InlineData("c1", "c2", RoundValueClass.ExpectedOverhead, null)]
    [InlineData("c1", "c1", RoundValueClass.Wasted, "unchanged-commit-review")]
    public void Classify_ReviewCommits_RequiresNonemptyOrdinalMatch(string? first, string? second,
        RoundValueClass value, string? cause)
    {
        var rounds = RoundValueClassifier.Classify([GoalWith(GoalStatus.Completed,
            AgentRole.Reviewer, first, second, "Invalidated upstream")]);
        Assert.Equal(RoundValueClass.ExpectedOverhead, rounds[0].ValueClass);
        Assert.Null(rounds[0].WasteCause);
        Assert.Equal(value, rounds[1].ValueClass);
        Assert.Equal(cause, rounds[1].WasteCause);
    }

    [Fact]
    public void HasLaterSuccess_SubscriptionTesterNonLatestRound_UsesStoredRecord()
    {
        var task = SubscriptionTesterGoal().Tasks.Single();

        Assert.True(FalseFailBridgeDetector.HasLaterSuccess(task, task.VerificationHistory,
            task.VerificationHistory[0].CompletedAt));
    }

    [Fact]
    public void SubscriptionTesterNonLatestRound_LiveIsStaleWhileStoredIsVerifiedSuccess()
    {
        var task = SubscriptionTesterGoal().Tasks.Single();
        var success = task.VerificationHistory[1];

        var live = DispatchFailureClassifier.Classify(task, success);
        Assert.Equal(DispatchOutcomeKind.VerificationIncomplete, live.Kind);
        Assert.Contains("terminal_contract=Stale", live.EvidenceSummary, StringComparison.Ordinal);
        Assert.Equal(DispatchOutcomeKind.VerifiedSuccess,
            DispatchFailureClassifier.ClassifyStoredRound(task, success).Kind);
    }

    [Fact]
    public void Classify_SubscriptionTesterGuardRound_BridgesThroughNonLatestStoredSuccess()
    {
        var goal = SubscriptionTesterGoal();

        var round = RoundValueClassifier.Classify([goal]).Single(r => r.Round.RoundIndex == 1);

        Assert.Equal(RoundGoalOutcome.Pending, round.Outcome);
        Assert.Equal(RoundValueClass.Wasted, round.ValueClass);
        Assert.Equal("classifier-false-fail-bridge", round.WasteCause);
    }

    private static Goal SubscriptionTesterGoal()
    {
        const string command = "codex verify";
        const string workDir = @"C:\repo";
        const string output = """
            WORKER_RESULT:
            files: none
            commands: inspected packaged test receipts
            tests: pass - packaged test receipt records 150 passed, 0 failed
            commit: none
            blockers: none
            model_fit: fixture
            skills: none
            confidence: high
            END_WORKER_RESULT
            """;
        var dispatches = Enumerable.Range(1, 3).Select(hour => new TaskDispatchSnapshot(
            "codex-cli", command, workDir, RoundValueFixture.Since.AddHours(hour),
            WorkerProviderKind: ProviderKind.OpenAICodexCli)).ToArray();
        var guard = new TaskVerificationSnapshot(command, workDir, 1, string.Empty,
            "Developer/Tester dispatch exited 0 but did not produce required relevant file-change evidence",
            dispatches[0].DispatchedAt.AddMinutes(15),
            DispatchStartedAt: dispatches[0].DispatchedAt.AddSeconds(2),
            EvidenceSource: VerificationEvidenceSource.Dispatch, DispatchAttemptAt: dispatches[0].DispatchedAt);
        var success = new TaskVerificationSnapshot(command, workDir, 0, output, string.Empty,
            dispatches[1].DispatchedAt.AddMinutes(15), WorkerResultPresent: true,
            DispatchStartedAt: dispatches[1].DispatchedAt.AddSeconds(2), AuthoritativeStandardOutput: output,
            EvidenceSource: VerificationEvidenceSource.Dispatch, DispatchAttemptAt: dispatches[1].DispatchedAt);
        var task = new TaskSnapshot("subscription-tester", "Verify subscription worker receipts", AgentRole.Tester,
            WorkTaskStatus.Assigned, null, null, null, [guard, success], dispatches[2], null,
            DispatchHistory: dispatches);
        ProgressEventSnapshot[] events =
        [
            new("goal", task.Id, ProgressKind.TaskFailed, "file-change guard", guard.CompletedAt),
            new("goal", task.Id, ProgressKind.TaskCompleted, "verified test receipts", success.CompletedAt)
        ];
        return AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [new GoalSnapshot("goal", "Subscription Tester stored rounds", GoalStatus.Active, [task], events)], []))
            .Goals.Single();
    }

    private static Goal GoalWith(GoalStatus status, AgentRole role, string? first, string? second,
        string message, ProgressKind kind = ProgressKind.TaskRetried, bool omitFirstEnding = false)
    {
        var task = RoundValueFixture.Task("task", role, WorkTaskStatus.Completed,
            RoundValueFixture.Dispatch("2026-09-24T01:00:00Z", first),
            RoundValueFixture.Dispatch("2026-09-24T03:00:00Z", second));
        var events = new List<ProgressEventSnapshot>();
        if (!omitFirstEnding)
        {
            events.Add(new("goal", "task", ProgressKind.TaskCompleted, "done", RoundValueFixture.Since.AddHours(1.5)));
            events.Add(new("goal", "task", kind, message, RoundValueFixture.Since.AddHours(2)));
        }
        events.Add(new("goal", "task", ProgressKind.TaskCompleted, "done", RoundValueFixture.Since.AddHours(3.5)));
        return AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [new GoalSnapshot("goal", "work", status, [task], events)], [])).Goals.Single();
    }
}
