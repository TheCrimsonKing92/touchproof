using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: scenarios own their in-memory kernels and fixed clock; no external stores are read.
public sealed class PreTesterLegacyMessageFallbackTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("started")]
    [InlineData("abandoned")]
    [InlineData("red")]
    public void Readers_MessageOnlyEvents_PreserveParserDataAndOutcomeFiltering(string outcome)
    {
        var kernel = new AgentOrchestratorKernel(new FixedClock());
        var tester = new TaskSpec(TaskId.New(), "Verify", AgentRole.Tester);
        var goal = kernel.CreateGoal("Legacy evidence", [tester]);
        var message = PreTesterEvidenceIndexLines.FormatMarker(new(outcome, "legacy-candidate", "legacy-receipt",
            ["Core.Tests:LegacyTests"], ["MissingTests"], "legacy-result.json", ["LegacyTests.Fails"],
            outcome == "abandoned" ? "malformed-selection-name" : null));
        if (outcome == "started") kernel.RecordFindingEvidenceRequest(goal.Id, tester.Id, message);
        else kernel.RecordFindingEvidenceRun(goal.Id, tester.Id, message);
        var snapshot = kernel.ExportGoalSnapshot(goal.Id);
        var restored = Assert.Single(AgentOrchestratorKernel.FromSnapshot(new([snapshot], [])).Goals);
        var evt = Assert.Single(restored.Timeline, item => item.Kind is
            ProgressKind.FindingEvidenceRequestRecorded or ProgressKind.FindingEvidenceRunRecorded);
        Assert.Null(evt.PreTesterEvidence);
        Assert.Equal(message, evt.Message);
        var parsed = Assert.IsType<PreTesterEvidenceEntry>(PreTesterEvidenceIndexLines.Parse(message));
        Assert.Null(parsed.AbandonmentReason);
        Assert.Equal(parsed, PreTesterEvidenceEntryReader.Read(evt));
        Assert.Equal(parsed, PreTesterEvidenceIndexLines.Latest(restored, tester.Id, "legacy-candidate"));
        var recovered = PreTesterStoredEntryRecovery.ReadAndHeal(restored, tester.Id, "legacy-candidate",
            (_, _) => Assert.Fail("Untruncated legacy evidence must not be healed."));
        Assert.Equal(parsed, recovered.Entry);
        Assert.False(recovered.Healed);
        var lines = PreTesterEvidenceIndexLines.ForTester(restored, tester, "legacy-candidate");
        if (outcome == "started") Assert.Empty(lines);
        else Assert.Equal(new[]
        {
            $"evidence_index: selection=Core.Tests:LegacyTests; verdict={outcome}; receipt=legacy-receipt; " +
            $"state={outcome}; candidate_sha=legacy-candidate; provenance=developer-deferred-pre-tester; " +
            "not_run=MissingTests; result_path=legacy-result.json",
            "evidence_not_run: MissingTests", "evidence_failing_tests: LegacyTests.Fails"
        }, lines);
        var history = new CrossGoalTestFailureHistory(null, null, () => [restored])
            .Read(GoalId.New(), ["LegacyTests.Fails"], Now);
        if (outcome == "red") Assert.Equal(goal.Id.Value, Assert.Single(history["LegacyTests.Fails"]));
        else Assert.Empty(history);
    }

    [Fact]
    public void ReadAndHeal_TruncatedLegacyStart_AbandonsOnceAndRecognizesLegacyReason()
    {
        var kernel = new AgentOrchestratorKernel(new FixedClock());
        var tester = new TaskSpec(TaskId.New(), "Verify", AgentRole.Tester);
        var goal = kernel.CreateGoal("Recover legacy truncation", [tester]);
        var selections = Enumerable.Range(0, 400)
            .Select(index => $"Infrastructure.Tests:LegacySelection{index:D4}WithLongNameTests").ToArray();
        var full = PreTesterEvidenceIndexLines.FormatMarker(new("started", "legacy-candidate", "legacy-receipt",
            selections, [], null, []));
        Assert.True(full.Length > TimelineMessageBound.CharacterLimit);
        var truncated = TimelineMessageBound.Bound(full, goal.Id.Value, tester.Id.Value);
        Assert.Contains("[omitted ", truncated, StringComparison.Ordinal);
        kernel.RecordFindingEvidenceRequest(goal.Id, tester.Id, truncated);
        var parsed = Assert.IsType<PreTesterEvidenceEntry>(PreTesterEvidenceIndexLines.Parse(truncated));
        Assert.Equal(parsed, PreTesterEvidenceIndexLines.Latest(goal, tester.Id, "legacy-candidate"));
        Assert.Null(Assert.Single(goal.Timeline,
            evt => evt.Kind == ProgressKind.FindingEvidenceRequestRecorded).PreTesterEvidence);
        var writes = new List<string>();
        void RecordLegacy(PreTesterEvidenceEntry _, string message)
        {
            writes.Add(message);
            kernel.RecordFindingEvidenceRun(goal.Id, tester.Id, message);
        }

        var first = PreTesterStoredEntryRecovery.ReadAndHeal(goal, tester.Id, "legacy-candidate", RecordLegacy);
        Assert.Null(first.Entry);
        Assert.True(first.Healed);
        var abandonedMessage = Assert.Single(writes);
        Assert.EndsWith("; abandoned_reason=truncated-stored-message", abandonedMessage, StringComparison.Ordinal);
        var abandoned = Assert.IsType<PreTesterEvidenceEntry>(PreTesterEvidenceIndexLines.Parse(abandonedMessage));
        Assert.Equal(parsed with { Outcome = "abandoned" }, abandoned);
        Assert.Null(Assert.Single(goal.Timeline,
            evt => evt.Kind == ProgressKind.FindingEvidenceRunRecorded).PreTesterEvidence);

        var second = PreTesterStoredEntryRecovery.ReadAndHeal(goal, tester.Id, "legacy-candidate", RecordLegacy);
        Assert.Null(second.Entry);
        Assert.True(second.Healed);
        Assert.Single(writes);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
