using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;
using Fixture = PreTesterCoupledClassSelectionsTests.Fixture;

public sealed class PreReviewHubFileBreadthDeferralTests
{
    private const string HubPath = "src/Mcg.AgentOrchestrator.Execution/Processes/GitCli.cs";
    private const string ChangedClass = "HubChangedTests";
    private const string FailedGateClass = "FailedGateTests";
    private static readonly string[] ExpectedSelections =
    [
        "Infrastructure.Tests: FullyQualifiedName~" + ChangedClass,
        "Infrastructure.Tests: FullyQualifiedName~" + FailedGateClass
    ];

    [Fact]
    public void BuildContext_AfterHubDeferral_SelectsExactlyChangedAndFailedGateTests()
    {
        var context = BuildHubContext();

        Assert.False(context.MappingNeedsInput);
        Assert.False(context.NoApplicableTests);
        Assert.Equal(ExpectedSelections, context.SelectedFocusedTests);
        Assert.Contains($"hub_file={HubPath}", context.MappingReason);
    }

    [Fact]
    public void BuildContext_OverBound_DefersBreadthBeforeHistoryCostGate()
    {
        using var fixture = new Fixture();
        fixture.Write(Fixture.Source, "public class NewContract {}");
        for (var index = 0; index < 34; index++)
            fixture.Test($"DependentTests{index}", "NewContract");

        string[] changedFiles = [Fixture.Source];
        var unbounded = RepositoryTestImpactPlanner.Plan(RepositoryChangeClassifier.Classify(changedFiles), fixture.Root);
        var reverseCheck = Assert.Single(unbounded.Checks, check => check.Name == ReverseDependencyEvidenceCostGate.CheckName);
        Assert.Equal(34, reverseCheck.TestClassSelections?.Count);

        var context = ConductorDriver.BuildPreReviewEvidenceContext("candidate", changedFiles, fixture.Root);

        Assert.True(context.NoApplicableTests);
        Assert.False(context.MappingNeedsInput);
        Assert.Empty(context.SelectedFocusedTests);
        Assert.Contains("breadth was deferred to the full gate", context.MappingReason);
        Assert.Contains("selected_classes=34 exceeds bound=33", context.MappingReason);
        Assert.DoesNotContain("reach-skipped-cost", context.MappingReason);
    }

    [Fact]
    public void AdvanceOnce_HubBreadthDeferred_RecordsGreenAndDispatchesReviewerWithoutRetry()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
            PassVerification(kernel, goal, task);

        var receipts = new List<PreReviewEvidenceReceipt>();
        var retries = new List<TaskId>();
        var escalations = new List<string>();
        var dispatches = new List<TaskId>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => BuildHubContext(),
            runFocusedEvidence: (_, request) =>
            {
                Assert.Equal(string.Join("; ", ExpectedSelections), request);
                return new FocusedEvidenceRunResult(request, Accepted: true, Passed: true,
                    Summary: "Changed and failed-gate classes passed.",
                    Checks: ExpectedSelections.Select(selection =>
                        Assert.Single(PassingPreReviewEvidence(selection).Checks)).ToArray());
            },
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
            {
                Assert.Equal(reviewer.Id, taskId);
                receipts.Add(receipt);
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retries.Add(taskId);
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            writeEscalation: (_, _, message) => escalations.Add(message),
            dispatchAndStart: candidate =>
            {
                dispatches.Add(candidate.Tasks.First(task => task.Status == WorkTaskStatus.Assigned).Id);
                return DispatchStartOutcome.Started();
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        var receipt = Assert.Single(receipts);
        Assert.Equal(PreReviewEvidenceDisposition.Green, receipt.Disposition);
        Assert.Equal(ExpectedSelections, receipt.SelectedFocusedTests);
        Assert.Empty(retries);
        Assert.DoesNotContain(escalations, message =>
            message.Contains("PRE_REVIEW_MAPPING_RETRY_DID_NOT_CONVERGE", StringComparison.Ordinal) ||
            message.Contains("PRE_REVIEW_SELECTION_APPARATUS_FAILURE", StringComparison.Ordinal));
        Assert.Equal(reviewer.Id, Assert.Single(dispatches));
    }

    private static PreReviewEvidenceContext BuildHubContext()
    {
        var changes = RepositoryChangeClassifier.Classify(
            [HubPath, $"tests/Mcg.AgentOrchestrator.Infrastructure.Tests/{ChangedClass}.cs"]);
        string[] command = ["dotnet", "test", "--project", Fixture.Project];
        var reverse = new RepositoryTestImpactCheck(ReverseDependencyEvidenceCostGate.CheckName,
            [.. command, "--filter", "FullyQualifiedName~DependentTests"],
            "reverse-dependent coverage", RepositoryTestProject.Infrastructure, ["DependentTests"]);
        var changed = new RepositoryTestImpactCheck("infrastructure tests", command,
            "changed-test coverage", RepositoryTestProject.Infrastructure) { ChangedTestClasses = [ChangedClass] };
        var plan = new RepositoryTestImpactPlan(true, false, "Original summary.", [reverse, changed]);
        var bounded = ReverseDependencyBreadthBound.Apply(plan, changes.Files.Select(file => file.Path));
        var context = ConductorDriver.BuildPreReviewEvidenceContext("hub-candidate", changes, bounded);
        return PreReviewEvidenceContextBuilder.WithFailedGateClasses(context, [("Infrastructure.Tests", FailedGateClass)]);
    }
}
