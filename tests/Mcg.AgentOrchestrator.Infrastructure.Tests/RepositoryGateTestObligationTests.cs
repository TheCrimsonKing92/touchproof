using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: in-memory goals and fixture-owned receipt/TRX files, with no child processes.
public sealed class RepositoryGateTestObligationTests
{
    private const string Selector = "record.TestReadInvalidFixtures";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CurrentRepositoryResult_PassedOrFailedRow_DeterminesOutcome(bool passed)
    {
        using var fixture = new Fixture();
        fixture.Home.WriteAttempt([(Selector, "Passed")]);
        var sourceId = fixture.WriteReceipt(passed);

        var result = fixture.Evaluate();

        Assert.Equal(passed ? GateTestObligationEvaluator.Outcome.Satisfied : GateTestObligationEvaluator.Outcome.Failed,
            result.Outcome);
        Assert.Equal(sourceId, result.SourceId);
        Assert.Equal(new[] { Selector }, result.Selectors);
        Assert.Null(result.PendingDetail);
    }

    [Theory]
    [InlineData("missing-receipt")]
    [InlineData("stale-tuple")]
    [InlineData("other-goal")]
    [InlineData("wrong-repository-reference")]
    [InlineData("outside-goal-reference")]
    [InlineData("missing-result-file")]
    [InlineData("empty-results")]
    [InlineData("malformed-evidence")]
    [InlineData("malformed-receipt")]
    public void IneligibleRepositoryResult_WithPassingHomeRow_RemainsPending(string defect)
    {
        using var fixture = new Fixture();
        fixture.Home.WriteAttempt([(Selector, "Passed")]);
        fixture.WriteReceipt(defect: defect);

        var result = fixture.Evaluate();

        Assert.Equal(GateTestObligationEvaluator.Outcome.Pending, result.Outcome);
        Assert.Null(result.SourceId);
        Assert.Contains("crosswalk", result.PendingDetail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Crosswalk")]
    [InlineData(" crosswalk")]
    [InlineData("crosswalk ")]
    [InlineData("unknown")]
    public void UndeclaredKey_WithMatchingResults_NeverUsesHomeOrOtherRepository(string key)
    {
        using var fixture = new Fixture(key);
        fixture.Home.WriteAttempt([(Selector, "Passed")]);
        fixture.WriteReceipt();

        var result = fixture.Evaluate();

        Assert.Equal(GateTestObligationEvaluator.Outcome.Pending, result.Outcome);
        Assert.Null(result.SourceId);
        Assert.Contains(key, result.PendingDetail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("null")]
    [InlineData("throws")]
    public void UnavailableTuple_WithMatchingRepositoryResult_RemainsPending(string scenario)
    {
        using var fixture = new Fixture();
        fixture.WriteReceipt();
        Func<string?>? currentTuple = scenario switch
        {
            "absent" => null,
            "null" => () => null,
            _ => () => throw new IOException("tuple unavailable")
        };

        var result = fixture.Evaluate(currentTuple);

        Assert.Equal(GateTestObligationEvaluator.Outcome.Pending, result.Outcome);
        Assert.Null(result.SourceId);
        Assert.Contains("crosswalk", result.PendingDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void UndeclaredRepositories_WithPassingHomeRow_RemainsPending()
    {
        using var fixture = new Fixture(declareRepositories: false);
        fixture.Home.WriteAttempt([(Selector, "Passed")]);

        var result = fixture.Evaluate();

        Assert.Equal(GateTestObligationEvaluator.Outcome.Pending, result.Outcome);
        Assert.Null(result.SourceId);
        Assert.Contains("crosswalk", result.PendingDetail, StringComparison.Ordinal);
    }

    private sealed class Fixture : IDisposable
    {
        internal ProspectiveEvidenceGateFixture Home { get; } = new();
        private Goal Goal { get; }
        private static RepositoryCandidateTuple Tuple(string crosswalk = "crosswalk") => new([
            new("default", new CandidateIdentity("home", "base", "manifest")),
            new("crosswalk", new CandidateIdentity(crosswalk, "base", "manifest"))]);

        internal Fixture(string key = "crosswalk", bool declareRepositories = true)
        {
            if (declareRepositories)
                Home.Kernel.DeclareGoalRepositories(Home.Goal.Id, new(["default", "crosswalk"],
                    new Dictionary<string, string> { ["default"] = Home.Root, ["crosswalk"] = Path.Combine(Home.Root, "crosswalk") }));
            var snapshot = Home.Kernel.ExportSnapshot();
            var kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = [snapshot.Goals.Single() with
            {
                GateTestObligations = [new("repository-obligation", 1, [Selector], null, "repository receipt",
                    DateTimeOffset.UnixEpoch, RepositoryKey: key)]
            }] });
            Goal = kernel.GetGoal(Home.Goal.Id);
        }

        internal GateTestObligationEvaluator.Result Evaluate() => Evaluate(() => Tuple().Canonical);

        internal GateTestObligationEvaluator.Result Evaluate(Func<string?>? currentTuple) => Assert.Single(
            GateTestObligationEvaluator.Evaluate(Goal, Home.OrchestratorDirectory, Home.CandidateSha,
                currentRepositoryTuple: currentTuple));

        internal string WriteReceipt(bool passed = true, string? defect = null)
        {
            var store = new MultiRepositoryAcceptanceReceiptStore(Home.OrchestratorDirectory);
            var attempt = Guid.NewGuid().ToString("N");
            var trx = Path.Combine(Home.Root, "crosswalk.go-test.trx");
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            new XDocument(new XElement(ns + "TestRun",
                new XElement(ns + "Results", new XElement(ns + "UnitTestResult",
                    new XAttribute("testId", "go"), new XAttribute("testName", Selector),
                    new XAttribute("outcome", passed ? "Passed" : "Failed"))),
                new XElement(ns + "TestDefinitions", new XElement(ns + "UnitTest",
                    new XAttribute("id", "go"), new XAttribute("name", Selector),
                    new XElement(ns + "TestMethod", new XAttribute("adapterTypeName", "executor://go-test/v1"),
                        new XAttribute("className", "example.org/crosswalk/record"),
                        new XAttribute("codeBase", "record"), new XAttribute("name", "TestReadInvalidFixtures")))))).Save(trx);
            var evidenceGoal = defect == "outside-goal-reference" ? GoalId.New() : Goal.Id;
            var crosswalk = store.WriteEvidence(evidenceGoal, attempt, "crosswalk",
                new { Passed = passed, TestResultPaths = defect == "empty-results" ? Array.Empty<string>() : new[] { trx } });
            var home = store.WriteEvidence(Goal.Id, attempt, "default", new { Passed = true, TestResultPaths = new[] { trx } });
            if (defect == "malformed-evidence") File.WriteAllText(crosswalk, "invalid JSON");
            if (defect == "missing-result-file") File.Delete(trx);
            if (defect == "wrong-repository-reference") crosswalk = home;
            if (defect != "missing-receipt")
            {
                var receiptGoal = defect == "other-goal" ? GoalId.New() : Goal.Id;
                store.Write(new(receiptGoal,
                    Tuple(defect == "stale-tuple" ? "old-crosswalk" : "crosswalk").Canonical,
                    [new("default", true, home, null), new("crosswalk", passed, crosswalk, null)],
                    DateTimeOffset.UnixEpoch), Goal.Repositories!.Repositories);
                if (defect == "other-goal")
                    File.Copy(Path.Combine(store.DirectoryPath, receiptGoal.Value + ".json"),
                        Path.Combine(store.DirectoryPath, Goal.Id.Value + ".json"));
            }
            if (defect == "malformed-receipt")
                File.WriteAllText(Path.Combine(store.DirectoryPath, Goal.Id.Value + ".json"), "invalid JSON");
            return $"repository:crosswalk:{attempt}";
        }

        public void Dispose() => Home.Dispose();
    }
}
