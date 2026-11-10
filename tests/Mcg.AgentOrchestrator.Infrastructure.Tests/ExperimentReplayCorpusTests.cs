using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;
using Xunit;
using static ExperimentReplayTestData;

// Parallel-safe: all stores and fixture files have private directories.
public sealed class ExperimentReplayCorpusTests
{
    [Theory]
    [InlineData(true, false, 0, "unknown")]
    [InlineData(true, true, 1, "unknown")]
    [InlineData(false, true, 1, "test-failed")]
    [InlineData(true, true, 0, "passed")]
    public void GateOutcome_RequiresCompleteConsistentReceipts(bool passed, bool complete, int failures, string expected)
    {
        Assert.Equal(expected, ExperimentReplayCorpusImporter.ClassifyOutcome(passed, false, complete, failures));
    }

    [Fact]
    public void Import_SameRound_IsIdempotentAndRejectsChangedProvenance()
    {
        using var fixture = new ExperimentReplayTestData();
        var store = fixture.Store();
        Assert.True(store.Import(Case()));
        Assert.False(store.Import(Case()));
        Assert.Single(store.Cases("home", Now));
        Assert.Throws<InvalidDataException>(() => store.Import(Case() with { CandidateCommit = Main }));
    }

    [Fact]
    public void Map_NearbyWrongCandidate_RemainsUnknown()
    {
        var round = Round();
        var gate = Gate() with { CandidateCommit = Base };
        var result = Assert.Single(ExperimentReplayCorpusImporter.Map([round], [gate], Now));
        Assert.Equal("unknown", result.Replayability);
        Assert.Empty(result.Gates);
        Assert.Contains("no exact-linked subsequent solo gate", result.Reasons);
    }

    [Fact]
    public void Map_ExplicitDispatchLink_PreservesFailureAndPassingRetry()
    {
        var first = Gate();
        var retry = first with { Evidence = first.Evidence with { AttemptId = "retry", StartedAt = Now.AddMinutes(-5), Outcome = "passed", FailingTests = [] } };
        var result = Assert.Single(ExperimentReplayCorpusImporter.Map([Round()], [retry, first], Now));
        Assert.Equal(Base, result.BaseCommit);
        Assert.Equal(Candidate, result.CandidateCommit);
        Assert.Equal(Main, result.ObservedMain);
        Assert.Equal("task", result.TaskId);
        Assert.Equal("replayable", result.Replayability);
        Assert.Empty(result.Reasons);
        Assert.Equal(new[] { "red", "retry" }, result.Gates.Select(g => g.AttemptId));
        Assert.Equal(new[] { "test-failed", "passed" }, result.Gates.Select(g => g.Outcome));
        Assert.Equal("Example.Test", Assert.Single(result.Gates[0].FailingTests));
    }

    [Fact]
    public void Map_MatchingTupleWithoutDispatchProof_RemainsUnknown()
    {
        var gate = Gate() with { TaskId = null, DispatchedAt = null, DispatchLinkSource = null };
        var rows = ExperimentReplayCorpusImporter.Map([Round()], [gate], Now);
        Assert.Single(rows);
        Assert.All(rows, r => { Assert.Equal("unknown", r.Replayability); Assert.Empty(r.Gates); });
    }

    [Fact]
    public void Map_ExplicitLink_CannotTransferToAnotherTaskOrDispatch()
    {
        var rows = ExperimentReplayCorpusImporter.Map([Round(), Round() with { TaskId = "other" },
            Round() with { DispatchAt = Now.AddHours(-3) }], [Gate()], Now);
        Assert.Single(rows.Where(r => r.Replayability == "replayable"));
        Assert.Equal(2, rows.Count(r => r.Gates.Count == 0));
        var linked = Assert.Single(rows.Where(r => r.Gates.Count > 0));
        Assert.Contains(new ExperimentReplaySource("dispatch-link", "hash"), Assert.Single(linked.Gates).Sources);
    }

    [Fact]
    public void Map_DuplicateDispatchIdentity_RemainsUnknown()
    {
        var rows = ExperimentReplayCorpusImporter.Map([Round(), Round()], [Gate()], Now);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => { Assert.Equal("unknown", row.Replayability); Assert.Empty(row.Gates); });
    }

    [Theory]
    [InlineData("Failed", "test-failed")]
    [InlineData("Timeout", "apparatus-failed")]
    [InlineData("Error", "apparatus-failed")]
    [InlineData("Aborted", "apparatus-failed")]
    public void ReadGates_ValidAttemptSurvivesMalformedNeighbor_AndFatalRowsStaySeparate(string outcome, string expected)
    {
        using var fixture = new ExperimentReplayTestData();
        var directory = Directory.CreateDirectory(Path.Combine(fixture.Root, "acceptance-gate-attempts", "goal")).FullName;
        var trx = Path.Combine(directory, "tests.trx");
        File.WriteAllText(trx, $"<TestRun><UnitTestResult testId='1' testName='Example.Fails' outcome='Failed'/><UnitTestResult testId='2' testName='Example.Other' outcome='{outcome}'/></TestRun>");
        var metadata = Path.Combine(directory, "valid.attempt.json");
        File.WriteAllText(metadata, JsonSerializer.Serialize(new { attemptId = "valid", goalId = "goal", kind = "gate",
            startedAt = Now.AddMinutes(-10), branchHeadSha = Candidate, mainHeadSha = Main, testResultPaths = new[] { trx } }));
        File.WriteAllText(Path.Combine(directory, "valid.result.json"), JsonSerializer.Serialize(new {
            branchHeadSha = Candidate, mainHeadSha = Main, acceptance = new { passed = false, testResultPaths = new[] { trx } } }));
        File.WriteAllText(Path.Combine(directory, "malformed.attempt.json"), "{broken");
        var skipped = new List<ImpactReplayDiagnostic>();
        var retained = ImpactReplayAttemptSource.Read(fixture.Root, Now.AddDays(-30), skipped);
        Assert.Single(skipped);
        Assert.Equal(metadata, Assert.Single(retained).MetadataPath);
        var diagnostics = new List<string>();
        var gate = Assert.Single(ExperimentReplayCorpusImporter.ReadGates(retained, Path.Combine(fixture.Root, "evidence"), diagnostics, default));
        Assert.Equal(expected, gate.Evidence.Outcome);
        Assert.Contains(gate.Evidence.Sources, s => s.Sha256 == ExperimentReplayStore.Source(metadata).Sha256);
        Assert.Contains(diagnostics, d => d.Contains("task/dispatch linkage was not recorded", StringComparison.Ordinal));
        Assert.Null(gate.DispatchLinkSource);
    }

    [Fact]
    public void Map_InventoryWindow_KeepsMostRecentHundredIncludingUnknown()
    {
        var rounds = Enumerable.Range(0, 105).Select(i => Round() with { TaskId = $"task-{i:D3}", CompletedAt = Now.AddMinutes(-i) })
            .Append(Round() with { TaskId = "old", CompletedAt = Now.AddDays(-31) });
        var rows = ExperimentReplayCorpusImporter.Map(rounds, [], Now);
        Assert.Equal(100, rows.Count);
        Assert.Equal("task-000", rows[0].TaskId);
        Assert.Equal("task-099", rows[^1].TaskId);
        Assert.All(rows, r => Assert.Equal("unknown", r.Replayability));
    }

    private static ExperimentReplayCorpusImporter.Round Round() => new("home", "goal", "task", Now.AddHours(-2), Now.AddHours(-1), Base, Candidate, Main, new("dispatch", "hash"));
    private static ExperimentReplayCorpusImporter.Gate Gate() => new("goal", Candidate, Main,
        new("red", Now.AddMinutes(-10), "test-failed", ["Example.Test"], [new("gate", "hash")]),
        "task", Now.AddHours(-2), new("dispatch-link", "hash"));
}
