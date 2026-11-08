using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: in-memory goals and injected evidence execution; no shared process state.
public sealed class RustNextestFocusedEvidenceConductorTests
{
    [Theory]
    [InlineData(RustNextestFocusedEvidenceTests.ModuleTest)]
    [InlineData(RustNextestFocusedEvidenceTests.IntegrationTest)]
    public void Request_CargoAlias_IsHonouredWithUnmodifiedRustName(string selector)
    {
        var settings = AcceptanceGateEngineSettings.Parse(RustNextestFocusedEvidenceTests.Manifest);
        var (requests, outcome) = RunRequest(settings, selector);
        Assert.True(outcome.Honoured);
        Assert.NotEqual(FindingEvidenceNotHonouredReason.UnparseableSelection, outcome.Reason);
        Assert.Equal("declared-test:" + selector, Assert.Single(requests));
        var check = RustNextestFocusedEvidenceTests.Plan(requests[0], settings, out _);
        Assert.Equal(["nextest", "run", "--profile", "ci", "-E", $"test(={selector})"], check.Arguments);
    }

    [Theory]
    [InlineData("test(=name)")]
    [InlineData("first|second")]
    [InlineData("first&second")]
    [InlineData("a:b")]
    [InlineData("a::::b")]
    [InlineData("a::")]
    [InlineData("a::é")]
    [InlineData("a b")]
    [InlineData(" name")]
    public void Request_InvalidRustName_RefusesBeforeExecution(string selector)
    {
        var (requests, outcome) = RunRequest(
            AcceptanceGateEngineSettings.Parse(RustNextestFocusedEvidenceTests.Manifest), selector);
        Assert.False(outcome.Honoured);
        Assert.Equal(FindingEvidenceNotHonouredReason.UnparseableSelection, outcome.Reason);
        Assert.Empty(requests);
    }

    [Fact]
    public void Normalize_RustFunction_DoesNotExpandDotnetSiblings()
    {
        Assert.True(FindingEvidenceRequestNormalization.TryNormalize(
            new FindingEvidenceRequest([new("Cargo.toml", RustNextestFocusedEvidenceTests.IntegrationTest)]),
            AcceptanceGateEngineSettings.Parse(RustNextestFocusedEvidenceTests.Manifest),
            (_, _) => throw new InvalidOperationException("Rust names must not expand .NET sibling classes."),
            out var normalized, out var executorRequest, out _, out var detail), detail);
        Assert.Equal(new FindingEvidenceSelection("declared-test", RustNextestFocusedEvidenceTests.IntegrationTest),
            Assert.Single(normalized.Selections));
        Assert.Equal("declared-test:" + RustNextestFocusedEvidenceTests.IntegrationTest, executorRequest);
    }

    [Fact]
    public void Request_DotnetManifest_StillRefusesRustPaths()
    {
        var settings = AcceptanceGateEngineSettings.Parse("""
            {"checks":[{"type":"dotnet-test","project":"tests/Sample.Tests/Sample.Tests.csproj"}]}
            """);
        var (requests, outcome) = RunRequest(settings, RustNextestFocusedEvidenceTests.ModuleTest);
        Assert.False(outcome.Honoured);
        Assert.Equal(FindingEvidenceNotHonouredReason.UnparseableSelection, outcome.Reason);
        Assert.Empty(requests);
    }

    private static (IReadOnlyList<string> Requests, FindingEvidenceOutcome Outcome) RunRequest(
        AcceptanceGateEngineSettings settings, string selector)
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
            PassVerification(kernel, goal, task);
        var finding = EvidenceFindingWithRequest("Selected tests need execution evidence.",
            id: "nextest-focused", project: "Cargo.toml", classes: [selector]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "Focused evidence request", findings: [finding]);
        var requests = new List<string>();
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            getFindingEvidenceEngineSettings: _ => settings,
            runFocusedEvidence: (_, request) =>
            {
                requests.Add(request);
                return new FocusedEvidenceRunResult(request, true, true, "Focused check passed", []);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        var outcome = reviewer.VerificationHistory.Last().MergedReviewFindings!
            .Single(item => item.StableId == "nextest-focused").EvidenceOutcome;
        Assert.NotNull(outcome);
        return (requests, outcome);
    }
}
