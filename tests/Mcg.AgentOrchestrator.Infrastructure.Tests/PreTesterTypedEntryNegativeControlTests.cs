using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: scenarios own their in-memory kernels and use a fixed clock without external stores.
public sealed class PreTesterTypedEntryNegativeControlTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("started", true)]
    [InlineData("started", false)]
    [InlineData("abandoned", true)]
    [InlineData("abandoned", false)]
    [InlineData("red", true)]
    [InlineData("red", false)]
    public void Readers_AlteredStoredMessage_UseTypedEntry(string outcome, bool truncated)
    {
        var kernel = new AgentOrchestratorKernel(new FixedClock());
        var tester = new TaskSpec(TaskId.New(), "Verify", AgentRole.Tester);
        var goal = kernel.CreateGoal("Typed precedence", [tester]);
        var expected = new PreTesterEvidenceEntry(outcome, "typed-candidate", "typed-receipt",
            ["Core.Tests:TypedTests", "Infrastructure.Tests:OtherTypedTests"], ["MissingTests"],
            "typed-result.json", ["TypedTests.Fails"], outcome == "abandoned" ? "malformed-selection-name" : null);
        var message = PreTesterEvidenceIndexLines.FormatMarker(expected);
        if (outcome == "started")
            kernel.RecordFindingEvidenceRequest(goal.Id, tester.Id, expected, message);
        else
            kernel.RecordFindingEvidenceRun(goal.Id, tester.Id, expected, message);
        var snapshot = kernel.ExportGoalSnapshot(goal.Id);
        var recorded = Assert.Single(snapshot.Timeline, evt => evt.PreTesterEvidence is not null);
        string altered;
        if (truncated)
        {
            var longMessage = PreTesterEvidenceIndexLines.FormatMarker(expected with
            {
                Selections = Enumerable.Range(0, 400)
                    .Select(index => $"Infrastructure.Tests:WrongSelection{index:D4}WithLongNameTests").ToArray(),
                FailingTests = ["WrongTests.Fails"]
            });
            Assert.True(longMessage.Length > TimelineMessageBound.CharacterLimit);
            altered = TimelineMessageBound.Bound(longMessage, goal.Id.Value, tester.Id.Value);
            Assert.Contains("[omitted ", altered, StringComparison.Ordinal);
        }
        else
            altered = PreTesterEvidenceIndexLines.FormatMarker(expected with
            {
                CandidateSha = "wrong-candidate", ReceiptId = "wrong-receipt",
                Selections = ["Core.Tests:WrongTests"], NotRun = [],
                ResultPath = "wrong-result.json", FailingTests = ["WrongTests.Fails"]
            });
        Assert.NotEqual(recorded.Message, altered);
        snapshot = snapshot with { Timeline = snapshot.Timeline.Select(evt =>
            evt == recorded ? evt with { Message = altered } : evt).ToArray() };
        var restored = Assert.Single(AgentOrchestratorKernel.FromSnapshot(new([snapshot], [])).Goals);
        Assert.Equal(altered, Assert.Single(restored.Timeline,
            evt => evt.PreTesterEvidence is not null).Message);
        Assert.NotEqual(expected, PreTesterEvidenceIndexLines.Parse(altered));

        Assert.Equal(expected, PreTesterEvidenceIndexLines.Latest(restored, tester.Id, expected.CandidateSha));
        var lines = PreTesterEvidenceIndexLines.ForTester(restored, tester, expected.CandidateSha);
        if (outcome == "started") Assert.Empty(lines);
        else Assert.Equal(new[]
        {
            $"evidence_index: selection=Core.Tests:TypedTests,Infrastructure.Tests:OtherTypedTests; verdict={outcome}; " +
            $"receipt=typed-receipt; state={outcome}; candidate_sha=typed-candidate; " +
            "provenance=developer-deferred-pre-tester; not_run=MissingTests; result_path=typed-result.json",
            "evidence_not_run: MissingTests", "evidence_failing_tests: TypedTests.Fails"
        }, lines);
        var writes = new List<PreTesterEvidenceEntry>();
        var recovered = PreTesterStoredEntryRecovery.ReadAndHeal(restored, tester.Id, expected.CandidateSha,
            (entry, _) => writes.Add(entry));
        Assert.Equal(expected, recovered.Entry);
        Assert.False(recovered.Healed);
        Assert.Empty(writes);

        var history = new CrossGoalTestFailureHistory(null, null, () => [restored])
            .Read(GoalId.New(), ["TypedTests.Fails", "WrongTests.Fails"], Now);
        if (outcome == "red")
        {
            Assert.Equal("TypedTests.Fails", Assert.Single(history.Keys));
            Assert.Equal(goal.Id.Value, Assert.Single(history["TypedTests.Fails"]));
        }
        else Assert.Empty(history);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
