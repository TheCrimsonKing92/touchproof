using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: private artifacts, injected event sink, no spawned processes.
public sealed class AcceptanceBalancedLaneResidualTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "balanced-residual-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScannerMiss_ExecutesInResidual_AndPlanRecordsNamesAndZero(bool missed)
    {
        var expansion = AcceptanceBalancedLaneExpander.Expand([], [new("Tests.Known", null)],
            new Dictionary<string, double>(), new());
        var residual = Assert.Single(expansion.Lanes, lane => lane.Name == "Infrastructure residual");
        Assert.Equal(residual, Assert.Single(AcceptanceLaneMembership.LanesIncluding(expansion.Lanes, "Tests.Missed")));
        Assert.DoesNotContain(residual, AcceptanceLaneMembership.LanesIncluding(expansion.Lanes, "Tests.Known"));
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "residual.trx");
        File.WriteAllText(path, missed ? """
            <TestRun><TestDefinitions><UnitTest id="x"><TestMethod className="Tests.Missed" name="Example" /></UnitTest></TestDefinitions>
            <Results><UnitTestResult testId="x" outcome="Passed" duration="00:00:01" /></Results>
            <ResultSummary outcome="Completed"><Counters total="1" executed="1" notExecuted="0" /></ResultSummary></TestRun>
            """ : """
            <TestRun><ResultSummary outcome="Completed"><Counters total="0" executed="0" notExecuted="0" /></ResultSummary></TestRun>
            """);
        var events = new List<AcceptanceGateProgress>();
        var prefix = Path.Combine(_root, "attempt");
        var writer = AcceptanceBalancedLanePlanWriter.Publish(new(AcceptanceGateLanePlan.CurrentSchemaVersion,
            "attempt", expansion.Lanes.Select(lane => lane.Name).ToArray(), []), prefix, null, events.Add,
            TimeProvider.System, _root, expansion.Layout);
        var check = new Check { Name = "infrastructure tests: " + residual.Name,
            Project = AcceptancePolicyShardPlanner.InfrastructureTestsProject, IsResolvedInfrastructureLane = true };
        writer.Record(check, new(check.Name, true, 0, null, TestResultPaths: [path]));
        var checks = new List<AcceptanceCheckResult>();
        writer.AddResidualSummary(checks);
        Assert.Contains("residual-count=" + (missed ? 1 : 0), Assert.Single(checks).ResultSummary!);
        writer.Complete("completed");
        Assert.Equal(2, events.Count);
        using var document = JsonDocument.Parse(File.ReadAllText(prefix + AcceptanceGateLanePlan.ArtifactSuffix));
        var layout = document.RootElement.GetProperty("balancedLayout");
        Assert.Equal(missed ? 1 : 0, layout.GetProperty("residualCount").GetInt32());
        Assert.True(layout.GetProperty("residualObserved").GetBoolean());
        Assert.Equal(missed ? ["Tests.Missed"] : Array.Empty<string>(),
            layout.GetProperty("residualClasses").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(expansion.Layout!.Lanes[0].PredictedSeconds,
            layout.GetProperty("lanes")[0].GetProperty("predictedSeconds").GetDouble());
    }

    [Fact]
    public void EmptyReceipt_IsAcceptedOnlyForResidual_AndOtherFailuresStayRed()
    {
        var evidence = new TrxCompletionEvidence(0, 0, 0, "Completed", true, null);
        Assert.False(AcceptanceShardCompletionAdjudicator.DecideTestShard(false, 0, evidence).Passed);
        Assert.True(AcceptanceShardCompletionAdjudicator.DecideTestShard(false, 0, evidence, allowEmptySelection: true).Passed);
        Assert.False(AcceptanceShardCompletionAdjudicator.DecideTestShard(true, 0, evidence, allowEmptySelection: true).Passed);
        Assert.False(AcceptanceShardCompletionAdjudicator.DecideTestShard(false, 2, evidence, allowEmptySelection: true).Passed);
        Assert.False(AcceptanceShardCompletionAdjudicator.DecideTestShard(false, 0,
            evidence with { FailedPredicate = "missing-trx", Passed = false }, allowEmptySelection: true).Passed);
    }

    [Fact]
    public void EmptyResidualReceipt_IsLeftOutOfGateTestResultPaths()
    {
        var paths = AcceptanceVerificationResult.CollectTestResultPaths([
            new("infrastructure tests: Balanced 1", true, 0, null, TestResultPaths: ["balanced.trx"], ExecutedTestCount: 4),
            new("infrastructure tests: Infrastructure residual", true, 0, null, TestResultPaths: ["residual.trx"], ExecutedTestCount: 0),
            new("infrastructure tests: Balanced 2", false, 1, null, TestResultPaths: ["failed-empty.trx"], ExecutedTestCount: 0),
            new("infrastructure tests: Balanced 3", true, 0, null, TestResultPaths: ["unknown-count.trx"])
        ]);

        Assert.Equal(["balanced.trx", "failed-empty.trx", "unknown-count.trx"], paths);
    }

    [Theory]
    [InlineData("failed", false)]
    [InlineData("faulted", false)]
    [InlineData("cancelled", false)]
    [InlineData("completed", true)]
    public void FailedAttemptsAndReusedResults_NeverEnterHistory(string outcome, bool reused)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "sample.trx");
        File.WriteAllText(path, """
            <TestRun><TestDefinitions><UnitTest id="x"><TestMethod className="Tests.A" name="Example" /></UnitTest></TestDefinitions>
            <Results><UnitTestResult testId="x" outcome="Passed" duration="00:00:01" /></Results></TestRun>
            """);
        var layout = AcceptanceBalancedLanePacker.Pack([new("Tests.A", null)], new Dictionary<string, double>(), new());
        var writer = AcceptanceBalancedLanePlanWriter.Publish(new(AcceptanceGateLanePlan.CurrentSchemaVersion,
            "attempt", ["Balanced 1"], []), null, null, _ => { }, TimeProvider.System, _root, layout);
        var check = new Check { Name = "infrastructure tests: Balanced 1", IsResolvedInfrastructureLane = true,
            Project = AcceptancePolicyShardPlanner.InfrastructureTestsProject };
        writer.Record(check, new(check.Name, true, 0, null, TestResultPaths: [path], TestResultIsExplicitCrossAttemptReuse: reused));
        writer.Complete(outcome);
        Assert.Empty(AcceptanceClassDurationHistoryStore.ReadMedians(_root, 5));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
