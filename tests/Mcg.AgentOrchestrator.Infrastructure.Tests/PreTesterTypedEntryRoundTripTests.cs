using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its kernel, temporary database and non-pooled connections.
public sealed class PreTesterTypedEntryRoundTripTests
{
    [Fact]
    public async Task SaveLoad_FourHundredSelections_RestoresEveryTypedFieldInOrder()
    {
        var kernel = new AgentOrchestratorKernel();
        var tester = new TaskSpec(TaskId.New(), "Verify", AgentRole.Tester);
        var goal = kernel.CreateGoal("Persist complete typed evidence", [tester]);
        var selections = Enumerable.Range(0, 400)
            .Select(index => $"Infrastructure.Tests:Selection{index:D4}WithLongClassNameTests").ToArray();
        var expected = new PreTesterEvidenceEntry("abandoned", "candidate-400", "receipt-400",
            selections, ["Core.Tests:MissingTests", "Infrastructure.Tests:UnavailableTests"],
            @"C:\evidence\result-400.json", ["ExampleTests.First", "ExampleTests.Second"],
            "truncated-stored-message");
        kernel.RecordFindingEvidenceRun(goal.Id, tester.Id, expected,
            PreTesterEvidenceIndexLines.FormatMarker(expected));
        var snapshot = kernel.ExportGoalSnapshot(goal.Id);
        var recorded = Assert.Single(snapshot.Timeline,
            evt => evt.Kind == ProgressKind.FindingEvidenceRunRecorded);
        Assert.Equal(400, Assert.IsType<PreTesterEvidenceEntry>(recorded.PreTesterEvidence).Selections.Count);
        Assert.True(recorded.Message.Length > TimelineMessageBound.CharacterLimit);
        // Persist a genuinely bounded message so reconstructing from text cannot satisfy this test.
        var bounded = TimelineMessageBound.Bound(recorded.Message, goal.Id.Value, tester.Id.Value);
        Assert.Contains("[omitted ", bounded, StringComparison.Ordinal);
        snapshot = snapshot with { Timeline = snapshot.Timeline.Select(evt =>
            evt == recorded ? evt with { Message = bounded } : evt).ToArray() };

        var root = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            await repository.SaveGoalSnapshotsAsync([snapshot]);
            var loaded = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(goal.Id));
            var restored = Assert.Single(AgentOrchestratorKernel.FromSnapshot(new([loaded], [])).Goals);
            var evt = Assert.Single(restored.Timeline,
                item => item.Kind == ProgressKind.FindingEvidenceRunRecorded);
            var actual = Assert.IsType<PreTesterEvidenceEntry>(evt.PreTesterEvidence);

            Assert.Equal(bounded, evt.Message);
            Assert.Equal(expected.Outcome, actual.Outcome);
            Assert.Equal(expected.CandidateSha, actual.CandidateSha);
            Assert.Equal(expected.ReceiptId, actual.ReceiptId);
            Assert.Equal(400, actual.Selections.Count);
            Assert.Equal(selections, actual.Selections);
            Assert.Equal(expected.NotRun, actual.NotRun);
            Assert.Equal(expected.ResultPath, actual.ResultPath);
            Assert.Equal(expected.FailingTests, actual.FailingTests);
            Assert.Equal(expected.AbandonmentReason, actual.AbandonmentReason);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
