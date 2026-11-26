using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;
using Fixture = PreTesterCoupledClassSelectionsTests.Fixture;

public sealed class PreReviewHubFileBreadthDeferralTests
{
    private const string HubPath = "src/Mcg.AgentOrchestrator.Execution/Processes/GitCli.cs";
    private const string InfrastructurePrefix = "Infrastructure.Tests: FullyQualifiedName~";
    private const string ChangedClass = "HubChangedTests";
    private const string ObjectiveClass = "ObjectiveNamedTests";
    private const string ObligationClass = "ObligationNamedTests";
    private const string FailedGateClass = "FailedGateTests";
    private static readonly string[] ExpectedSelections =
    [
        InfrastructurePrefix + ChangedClass,
        InfrastructurePrefix + FailedGateClass
    ];
    private static readonly string[] ExpectedNamedSelections =
    [
        InfrastructurePrefix + ChangedClass,
        InfrastructurePrefix + ObjectiveClass,
        InfrastructurePrefix + ObligationClass,
        InfrastructurePrefix + FailedGateClass
    ];

    [Fact]
    public void BuildContext_AfterHubDeferral_SelectsExactlyChangedAndFailedGateTests()
    {
        var context = BuildContext();

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
    public void BuildContext_OverBound_AddsNamedClassesInStableOrder()
    {
        using var fixture = new Fixture();
        fixture.Write(Fixture.Source, "public class NewContract {}");
        for (var index = 0; index < 34; index++)
            fixture.Test($"DependentTests{index}", "NewContract");
        fixture.Test(ObjectiveClass);
        fixture.Test("AnotherObjectiveTests");
        fixture.Test(ObligationClass);
        var (_, goal) = SoftwareGoal($"Cover {ObjectiveClass}.Works, AnotherObjectiveTests and {ObjectiveClass}.");
        RecordObligation(goal, ObjectiveClass, $"FullyQualifiedName~{ObligationClass}");

        var context = ConductorDriver.BuildPreReviewEvidenceContext("candidate", [Fixture.Source], fixture.Root, goal: goal);

        string[] expected =
        [
            InfrastructurePrefix + "AnotherObjectiveTests|FullyQualifiedName~" + ObjectiveClass,
            InfrastructurePrefix + ObligationClass
        ];
        Assert.Equal(expected, context.SelectedFocusedTests);
        Assert.Equal(string.Join("; ", expected), context.FocusedRequest);
        Assert.Contains("selected_classes=34 exceeds bound=33", context.MappingReason);
        Assert.Contains("breadth was deferred to the full gate", context.MappingReason);
        Assert.False(context.MappingNeedsInput);
        Assert.False(context.NoApplicableTests);
    }

    [Fact]
    public void BuildContext_WithoutBreadthDeferral_KeepsOriginalSelection()
    {
        using var fixture = new Fixture();
        var (_, goal) = NamedClassGoal(fixture);

        var baseline = BuildContext(hubChanged: false);
        var context = BuildContext(goal, fixture.Root, hubChanged: false);
        var deferred = BuildContext(goal, fixture.Root);

        Assert.Equal(baseline.SelectedFocusedTests, context.SelectedFocusedTests);
        Assert.Equal(baseline.FocusedRequest, context.FocusedRequest);
        Assert.Equal(baseline.MappingReason, context.MappingReason);
        Assert.DoesNotContain(context.SelectedFocusedTests, selection =>
            selection.Contains(ObjectiveClass, StringComparison.Ordinal) ||
            selection.Contains(ObligationClass, StringComparison.Ordinal));
        Assert.Equal(ExpectedNamedSelections, deferred.SelectedFocusedTests);
    }

    [Fact]
    public void BuildContext_UnresolvedOrAmbiguousNames_KeepsUniqueClassesWithoutMappingInput()
    {
        const string ambiguous = "AmbiguousNamedTests";
        using var fixture = new Fixture();
        fixture.Test(ambiguous);
        fixture.Write($"tests/Mcg.AgentOrchestrator.Core.Tests/{ambiguous}.cs",
            $"public class {ambiguous} {{ [Fact] public void Works() {{ }} }}");
        fixture.Test(ObligationClass);
        var (_, goal) = SoftwareGoal($"Cover MissingNamedTests and {ambiguous}.");
        RecordObligation(goal, $"FullyQualifiedName~{ambiguous}|FullyQualifiedName~{ObligationClass}",
            "AlsoMissingTests");

        var context = BuildContext(goal, fixture.Root);

        Assert.Equal(
            new[] { ExpectedSelections[0], InfrastructurePrefix + ObligationClass, ExpectedSelections[1] },
            context.SelectedFocusedTests);
        Assert.False(context.MappingNeedsInput);
        Assert.False(context.RequiresSourceCleanup);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdvanceOnce_HubBreadthDeferred_RecordsGreenAndDispatchesReviewerWithoutRetry(bool namedClasses)
    {
        using var fixture = new Fixture();
        var (kernel, goal) = namedClasses ? NamedClassGoal(fixture) : SoftwareGoal();
        var expected = namedClasses ? ExpectedNamedSelections : ExpectedSelections;
        var context = namedClasses ? BuildContext(goal, fixture.Root) : BuildContext();
        Assert.False(context.MappingNeedsInput);
        Assert.False(context.NoApplicableTests);
        Assert.Equal(expected, context.SelectedFocusedTests);
        Assert.Contains("breadth was deferred to the full gate", context.MappingReason);
        Assert.Contains($"hub_file={HubPath}", context.MappingReason);

        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
            PassVerification(kernel, goal, task);

        var receipts = new List<PreReviewEvidenceReceipt>();
        var retries = new List<TaskId>();
        var escalations = new List<string>();
        var dispatches = new List<TaskId>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => context,
            runFocusedEvidence: (_, request) =>
            {
                Assert.Equal(string.Join("; ", expected), request);
                return new FocusedEvidenceRunResult(request, Accepted: true, Passed: true,
                    Summary: "Selected classes passed.",
                    Checks: expected.Select(selection =>
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
        Assert.Equal(expected, receipt.SelectedFocusedTests);
        Assert.Empty(retries);
        Assert.DoesNotContain(escalations, message =>
            message.Contains("PRE_REVIEW_MAPPING_RETRY_DID_NOT_CONVERGE", StringComparison.Ordinal) ||
            message.Contains("PRE_REVIEW_SELECTION_APPARATUS_FAILURE", StringComparison.Ordinal));
        Assert.Equal(reviewer.Id, Assert.Single(dispatches));
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) NamedClassGoal(Fixture fixture)
    {
        fixture.Test(ChangedClass);
        fixture.Test(ObjectiveClass);
        fixture.Test(ObligationClass);
        fixture.Test("StaleObligationTests");
        var (kernel, goal) = SoftwareGoal($"Cover {ObjectiveClass}.Works and {ChangedClass}.");
        RecordObligation(goal, ObjectiveClass, $"FullyQualifiedName~{ObligationClass}");
        goal.RecordGateTestObligation(new GateTestObligation("previous", goal.AuthoritativeBrief.Version - 1,
            ["StaleObligationTests"], null, "Prior brief coverage", DateTimeOffset.UnixEpoch));
        return (kernel, goal);
    }

    private static void RecordObligation(Goal goal, params string[] selectors) =>
        goal.RecordGateTestObligation(new GateTestObligation("current", goal.AuthoritativeBrief.Version,
            selectors, null, "Current brief coverage", DateTimeOffset.UnixEpoch));

    private static PreReviewEvidenceContext BuildContext(
        Goal? goal = null, string? worktreePath = null, bool hubChanged = true)
    {
        var changes = RepositoryChangeClassifier.Classify(
            [hubChanged ? HubPath : Fixture.Source, $"tests/Mcg.AgentOrchestrator.Infrastructure.Tests/{ChangedClass}.cs"]);
        string[] command = ["dotnet", "test", "--project", Fixture.Project];
        var reverse = new RepositoryTestImpactCheck(ReverseDependencyEvidenceCostGate.CheckName,
            [.. command, "--filter", "FullyQualifiedName~DependentTests"],
            "reverse-dependent coverage", RepositoryTestProject.Infrastructure, ["DependentTests"]);
        var changed = new RepositoryTestImpactCheck("infrastructure tests", command,
            "changed-test coverage", RepositoryTestProject.Infrastructure) { ChangedTestClasses = [ChangedClass] };
        var plan = new RepositoryTestImpactPlan(true, false, "Original summary.", [reverse, changed]);
        var bounded = ReverseDependencyBreadthBound.Apply(plan, changes.Files.Select(file => file.Path));
        var context = ConductorDriver.BuildPreReviewEvidenceContext("candidate", changes, bounded, goal, worktreePath);
        return PreReviewEvidenceContextBuilder.WithFailedGateClasses(context, [("Infrastructure.Tests", FailedGateClass)]);
    }
}
