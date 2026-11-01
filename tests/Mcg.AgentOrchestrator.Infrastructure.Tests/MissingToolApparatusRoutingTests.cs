using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: attribution and routing use only in-memory receipts and journals.
public sealed class MissingToolApparatusRoutingTests
{
    [Fact]
    public void MissingToolCause_UsesExistingApparatusRoute()
    {
        var check = MissingToolCheck();
        var summary = Attribute([check]);

        Assert.Equal("missing-tool", Assert.Single(summary.RequiredUnmetCriteria).FailureClassification);
        var attribution = Assert.Single(summary.CheckAttributions!);
        Assert.Equal(check.Name, attribution.CheckName);
        Assert.Equal(AcceptanceFailureCause.EnvironmentalApparatus, attribution.Cause);
        Assert.True(ConductorDriver.IsEnvironmentalApparatusAcceptanceRun(summary));
    }

    [Fact]
    public void AdditionalCompileFailure_PreventsApparatusRoute()
    {
        var summary = Attribute([
            MissingToolCheck(),
            GoalAcceptanceVerifier.AttachFailureCauseEvidence(new AcceptanceCheckResult(
                "compile-check", false, 1, "error CS0103: The name 'x' does not exist"))
        ]);

        Assert.Equal(2, summary.RequiredUnmetCriteria.Count);
        Assert.Contains(summary.CheckAttributions!, attribution =>
            attribution.Cause == AcceptanceFailureCause.EnvironmentalApparatus);
        Assert.Contains(summary.CheckAttributions!, attribution =>
            attribution.CheckName == "compile-check" && attribution.Cause == AcceptanceFailureCause.NotClassified);
        Assert.False(ConductorDriver.IsEnvironmentalApparatusAcceptanceRun(summary));
    }

    private static AcceptanceCheckResult MissingToolCheck() =>
        GoalAcceptanceVerifier.AttachFailureCauseEvidence(new AcceptanceCheckResult(
            "declared-build", false, 1,
            "error: rustup could not choose a version of cargo to run, because one wasn't specified explicitly, and no default is configured."));

    private static AcceptanceVerificationSummary Attribute(IReadOnlyList<AcceptanceCheckResult> checks)
    {
        var current = new GoalId("12345678123456781234567812345678");
        var journals = new Dictionary<GoalId, GoalOperationJournalSummary>();
        var baseline = CleanTestBaseline.Resolve(journals, current, "main-a", null);
        var failedNames = checks.Select(check => check.Name).ToArray();
        var attributions = CleanTestBaseline.Attribute(baseline, failedNames, journals,
            current, "main-a", checks);
        return new AcceptanceVerificationSummary(false, checks, FailedChecks: failedNames,
            BranchHeadSha: "candidate-a", MainHeadSha: "main-a", CheckAttributions: attributions);
    }
}
