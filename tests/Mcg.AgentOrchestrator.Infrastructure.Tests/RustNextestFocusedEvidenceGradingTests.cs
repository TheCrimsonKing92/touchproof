using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using TestResult = Mcg.AgentOrchestrator.Infrastructure.TestResult;
using Grade = Mcg.AgentOrchestrator.Infrastructure.CrosswalkFocusedEvidence.SelectionGrade;
using static RustNextestFocusedEvidenceTests;

// Parallel-safe: per-test normalized receipts and injected execution; no process/global state.
public sealed class RustNextestFocusedEvidenceGradingTests
{
    [Theory]
    [InlineData("passed", "Satisfied")]
    [InlineData("failed", "Failed")]
    [InlineData("error", "Failed")]
    [InlineData("skipped", "NotDemonstrated")]
    public void Grade_ExactReportedName_UsesSharedOutcomeSemantics(string status, string expected)
    {
        var row = Row(ModuleTest, Enum.Parse<TestResultStatus>(status, true));
        Assert.Equal(expected, RustNextestFocusedEvidence.Grade([row], ModuleTest).ToString());
        Assert.Equal(Grade.NotDemonstrated, RustNextestFocusedEvidence.Grade([row], "parses_projects_and_rejects_malformed_registries"));
        Assert.Equal(Grade.NotDemonstrated, RustNextestFocusedEvidence.Grade([row], "fleet::tests"));
        Assert.Equal(Grade.NotDemonstrated, RustNextestFocusedEvidence.Grade([row with { DisplayName = "other" }], ModuleTest));
        Assert.Equal(Grade.Failed, RustNextestFocusedEvidence.Grade(
            [row with { Status = TestResultStatus.Passed }, row with { Status = TestResultStatus.Failed }], ModuleTest));
    }

    [Theory]
    [InlineData("passed", true, null)]
    [InlineData("failed", false, null)]
    [InlineData("skipped", false, "not-run:execution-not-demonstrated")]
    [InlineData("absent", false, "not-run:no-matching-test")]
    [InlineData("no-tests", false, "not-run:no-matching-test")]
    [InlineData("missing-report", false, "not-run:execution-not-demonstrated")]
    [InlineData("unreadable-report", false, "not-run:execution-not-demonstrated")]
    public async Task Arm_SelectionEvidence_RequiresEverySelectedName(string scenario, bool green, string? reason)
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            var check = Plan($"Cargo.toml:{ModuleTest};Cargo.toml:{IntegrationTest}",
                AcceptanceGateEngineSettings.Parse(Manifest), out var coverage);
            var path = Path.Combine(root, "receipt.json");
            var rows = scenario switch
            {
                "no-tests" or "missing-report" or "unreadable-report" => Array.Empty<TestResult>(),
                "absent" => [Row(IntegrationTest, TestResultStatus.Passed)],
                _ => new[] { Row(IntegrationTest, TestResultStatus.Passed),
                    Row(ModuleTest, Enum.Parse<TestResultStatus>(scenario, true)) }
            };
            if (rows.Length > 0) NormalizedTestResultWriter.Write(path, check.Name, rows);
            if (scenario == "unreadable-report") File.WriteAllText(path, "{");
            var result = new AcceptanceCheckResult(check.Name,
                scenario is "passed" or "skipped" or "absent", scenario == "no-tests" ? 4 : scenario == "failed" ? 1 : 0,
                "injected command result", ExecutedTestCount: rows.Count(row => row.Status != TestResultStatus.Skipped))
            {
                NormalizedTestResultPaths = rows.Length > 0 || scenario == "unreadable-report" ? [path] : [],
                CrosswalkDiagnostics = scenario == "no-tests"
                    ? [new(CrosswalkDiagnosticCategory.NonzeroExit, "exit 4"),
                       new(CrosswalkDiagnosticCategory.NoExecution, "no test rows")] : []
            };
            var notRun = FocusedEvidenceNotRunPlan.Compare([check], [result], coverage, true);
            var arm = await FocusedEvidenceExecution.RunFocusedEvidenceArmAsync(
                _ => throw new InvalidOperationException("No execution owner is used by this isolated arm."),
                (_, _, _, _, _, _, _, _, _) => Task.FromResult<IReadOnlyList<AcceptanceCheckResult>>([result]),
                _ => "", FindingEvidenceArm.Candidate, "candidate-sha", root, null,
                [check], coverage, null, null, null, CancellationToken.None, continueAfterFailure: true);
            Assert.Equal(green, arm.Passed);
            Assert.Equal(green, arm.Disposition == FindingEvidenceArmDisposition.Green);
            if (reason is null) Assert.Empty(notRun.Checks);
            else
            {
                var entry = Assert.Single(notRun.Checks);
                Assert.Equal(reason, entry.Reason);
                Assert.Contains(new FindingEvidenceSelection("Cargo.toml", ModuleTest), entry.Selections);
                Assert.Contains($"undemonstrated Cargo.toml:{ModuleTest} {reason}", arm.Summary);
                if (scenario is "absent" or "skipped") Assert.Single(entry.Selections);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Compare_UnrecordedAfterFailure_PreservesStoppedReason()
    {
        var check = Plan("Cargo.toml:" + ModuleTest, AcceptanceGateEngineSettings.Parse(Manifest), out var coverage);
        var earlier = new GoalAcceptanceVerifier.AcceptanceManifestCheck { Name = "earlier" };
        var plan = FocusedEvidenceNotRunPlan.Compare([earlier, check],
            [new(earlier.Name, false, 1, "failed", ExecutedTestCount: 1)], coverage, false);
        Assert.Equal("not-run:apparatus-stopped-after-failure", Assert.Single(plan.Checks).Reason);
    }

    private static TestResult Row(string name, TestResultStatus status) =>
        new("binary::" + name, name, null, [], status, null, null, null, [], null, null, "fixture");
}
