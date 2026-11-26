using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using TestResult = Mcg.AgentOrchestrator.Infrastructure.TestResult;
using Worktree = FocusedEvidenceUnexecutedClassCauseTests.Worktree;

public sealed class FocusedEvidenceNotRunPlanRemainderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialVstestReceipt_ListsOnlyTheUnexecutedClass(bool includeSkipped)
    {
        using var worktree = new Worktree();
        worktree.Declare(InfrastructureTestsProject, "AlphaTests", "BetaTests");
        worktree.Declare(CoreTestsProject, "GammaTests");
        var check = VstestCheck("partial selection");
        var fullyExecuted = VstestCheck("complete selection");
        var coverage = new FocusedEvidenceCoverage([
            new("Infrastructure.Tests:AlphaTests", [check.Name]),
            new("Infrastructure.Tests:BetaTests", [check.Name]),
            new("Infrastructure.Tests:GammaTests", [check.Name]),
            new("Infrastructure.Tests:DeltaTests", [fullyExecuted.Name])]);
        var records = new List<TestResult>
        {
            Result("Fixture.AlphaTests.Runs", TestResultStatus.Passed),
            Result("Fixture.BetaTests.Fails", TestResultStatus.Failed)
        };
        if (includeSkipped)
            records.Add(Result("Fixture.GammaTests.DoesNotRun", TestResultStatus.Skipped));
        var report = Path.Combine(worktree.Root, "partial.ctrf.json");
        NormalizedTestResultWriter.Write(report, "vstest", records);
        var completeReport = Path.Combine(worktree.Root, "complete.ctrf.json");
        NormalizedTestResultWriter.Write(completeReport, "vstest",
            [Result("Fixture.DeltaTests.Runs", TestResultStatus.Passed)]);
        AcceptanceCheckResult[] results = [
            new(check.Name, false, 1, "", ExecutedTestCount: 2) { NormalizedTestResultPaths = [report] },
            new(fullyExecuted.Name, true, 0, "", ExecutedTestCount: 1) { NormalizedTestResultPaths = [completeReport] }];

        var plan = FocusedEvidenceNotRunPlan.Compare([check, fullyExecuted], results, coverage, true, worktree.Root);

        var expected = new FindingEvidenceSelection("Infrastructure.Tests", "GammaTests");
        var missingCheck = Assert.Single(plan.Checks);
        Assert.Equal(check.Name, missingCheck.CheckName);
        Assert.Equal("not-run:execution-not-demonstrated", missingCheck.Reason);
        Assert.Equal(expected, Assert.Single(plan.Selections));
        Assert.Equal(expected, Assert.Single(missingCheck.UndemonstratedSelections!));
        Assert.Equal([
            "existing summary",
            "partial selection not-run:execution-not-demonstrated",
            $"undemonstrated Infrastructure.Tests:GammaTests not-run:execution-not-demonstrated cause=wrong-project:{CoreTestsProject}"
        ], plan.AppendSummary("existing summary").Split(Environment.NewLine));
    }

    [Fact]
    public void PositiveCountWithoutAReceipt_DoesNotIdentifyAnExecutedClass()
    {
        using var worktree = new Worktree();
        worktree.Declare(InfrastructureTestsProject, "AlphaTests");
        var check = VstestCheck("no identities");
        var coverage = new FocusedEvidenceCoverage([new("Infrastructure.Tests:AlphaTests", [check.Name])]);
        var result = new AcceptanceCheckResult(check.Name, true, 0, "", ExecutedTestCount: 1);

        var plan = FocusedEvidenceNotRunPlan.Compare([check], [result], coverage, true, worktree.Root);

        Assert.Equal(new FindingEvidenceSelection("Infrastructure.Tests", "AlphaTests"), Assert.Single(plan.Selections));
        Assert.EndsWith("undemonstrated Infrastructure.Tests:AlphaTests not-run:execution-not-demonstrated cause=no-result-recorded",
            plan.AppendSummary(""));
    }

    private static AcceptanceManifestCheck VstestCheck(string name) =>
        new() { Name = name, Type = "dotnet-test", Runner = "vstest", Project = InfrastructureTestsProject };

    internal static TestResult Result(string id, TestResultStatus status) =>
        new(id, id, null, [], status, null, null, null, [], null, null, "vstest");
}
