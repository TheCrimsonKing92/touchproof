using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: candidate inventories are supplied without filesystem or process state.
public sealed class AcceptanceSelectedExecutionWholeProjectCoverageTests
{
    private static readonly string[] Changes = ["tests/Other.Tests/NewCoreTests.cs"];
    private static readonly AcceptanceTestClassDescriptor[] Classes =
        [new("Lane.Selected", null), new("Lane.Dropped", null), new("Uncovered.Shared", null), new("NoGateCheck.NewTests", null)];
    private static readonly AcceptanceTestLane[] Lanes =
        [new("Selectable", "FullyQualifiedName=Lane.Selected|FullyQualifiedName=Lane.Dropped")];
    private static readonly AcceptanceWholeProjectCheckInventory[] Checks =
        [new("core tests", "tests/Other.Tests/Other.Tests.csproj",
            [new("Core.NewCoreTests", null), new("Core.Shared", null)])];

    [Fact]
    public void WholeProject_ClassAndMethodSelectors_RecordCoverageWithoutChangingLanes()
    {
        var calls = 0;
        var baseline = Create([]);
        var plan = Create(["Core.NewCoreTests", "NewCoreTests.Check"], () => { calls++; return Checks; });

        Assert.True(plan.HasPlan);
        Assert.Null(plan.FullGateReason);
        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, plan.Outcome);
        Assert.Equal(1, calls);
        Assert.Equal(new[] { "Core.NewCoreTests" }, plan.ObligationClasses);
        Assert.Equal(new AcceptanceSelectedExecutionMethodFormResolution("NewCoreTests.Check", "Core.NewCoreTests"),
            Assert.Single(plan.MethodFormResolutions));
        Assert.Equal(new[]
        {
            new AcceptanceSelectedExecutionCheckCoverage("Core.NewCoreTests", "Core.NewCoreTests", "core tests"),
            new AcceptanceSelectedExecutionCheckCoverage("NewCoreTests.Check", "Core.NewCoreTests", "core tests")
        }, plan.WholeProjectCoverage);
        Assert.Equal(JsonSerializer.Serialize(baseline.Lanes), JsonSerializer.Serialize(plan.Lanes));
        Assert.Equal(baseline.ExecutionLanes, plan.ExecutionLanes);
    }

    [Theory]
    [InlineData("Missing")]
    [InlineData("NoGateCheck.NewTests")]
    [InlineData("FullyQualifiedName~Core.NewCoreTests")]
    [InlineData("Core.NewCoreTests|")]
    [InlineData("Shared")]
    public void UncoveredOrMalformedSelector_PreservesFullGateReason(string selector)
    {
        var plan = Create([selector], () => Checks);
        Assert.False(plan.HasPlan);
        Assert.Equal(ReverseDependencySelectionOutcome.Abandoned, plan.Outcome);
        Assert.Equal("UnresolvedObligationSelector: " + selector, plan.FullGateReason);
        Assert.Equal(Lanes, plan.ExecutionLanes);
        Assert.Empty(plan.WholeProjectCoverage);
        Assert.Empty(plan.ObligationClasses);
    }

    [Fact]
    public void LaneResolvedSelector_DoesNotReadProjectInventory()
    {
        var plan = Create(["Lane.Dropped"], () => throw new InvalidOperationException("Unexpected project scan"));
        Assert.True(plan.HasPlan);
        Assert.Equal(new[] { "Lane.Dropped" }, plan.ObligationClasses);
        Assert.Empty(plan.WholeProjectCoverage);
        Assert.Contains("Lane.Dropped", Assert.Single(plan.Lanes).SelectedClasses);
    }

    [Fact]
    public void MixedLaneAndProjectMatches_RequireCoverageForEveryClass()
    {
        var classes = Classes.Concat(new[] { new AcceptanceTestClassDescriptor("Lane.Shared", null) }).ToArray();
        AcceptanceTestLane[] lanes = [new("Selectable", "FullyQualifiedName~Lane.")];
        var failed = AcceptanceSelectedExecutionPlan.Create(Changes, ReverseDependencyTestSelection.Resolved(["Selected"]),
            lanes, classes, [], [], ["Shared"], () => Checks);
        Assert.Equal("UnresolvedObligationSelector: Shared", failed.FullGateReason);
        Assert.Empty(failed.WholeProjectCoverage);

        var covered = AcceptanceSelectedExecutionPlan.Create(Changes, ReverseDependencyTestSelection.Resolved(["Selected"]),
            lanes, classes.Where(item => item.FullName != "Uncovered.Shared")
                .Append(new AcceptanceTestClassDescriptor("Core.Shared", null)).ToArray(), [], [], ["Shared"], () => Checks);
        Assert.True(covered.HasPlan);
        Assert.Equal(new[] { "Core.Shared", "Lane.Shared" }, covered.ObligationClasses);
        Assert.Equal(new AcceptanceSelectedExecutionCheckCoverage("Shared", "Core.Shared", "core tests"),
            Assert.Single(covered.WholeProjectCoverage));
    }

    [Fact]
    public void MultipleProjectChecks_RecordEachCoveringCheck()
    {
        var plan = Create(["Shared"], () => [Checks[0],
            new("other tests", "tests/Third.Tests/Third.Tests.csproj", [new("Third.Shared", null)])],
            Classes.Where(item => item.FullName != "Uncovered.Shared").ToArray());
        Assert.True(plan.HasPlan);
        Assert.Equal(new[] { "Core.Shared", "Third.Shared" }, plan.ObligationClasses);
        Assert.Equal(new[] { "core tests", "other tests" }, plan.WholeProjectCoverage.Select(item => item.CheckName));
    }

    [Fact]
    public void OtherFullGateConditions_KeepPrecedenceWithoutProjectCoverage()
    {
        var unmapped = AcceptanceSelectedExecutionPlan.Create(Changes, ReverseDependencyTestSelection.Resolved(["NotMapped"]),
            Lanes, Classes, [], [], ["Missing"], () => Checks);
        Assert.Equal("UnmappedTestClass: NotMapped", unmapped.FullGateReason);
        var unavailable = AcceptanceSelectedExecutionPlan.Create(Changes, ReverseDependencyTestSelection.Resolved([]),
            Lanes, [], [], [], ["Core.NewCoreTests"], () => throw new InvalidOperationException("Unexpected project scan"));
        Assert.Equal(ReverseDependencySelectionOutcome.Unavailable, unavailable.Outcome);
        Assert.Equal("Candidate test-class inventory is unavailable.", unavailable.FullGateReason);
    }

    private static AcceptanceSelectedExecutionPlan Create(IReadOnlyList<string> selectors,
        Func<IReadOnlyList<AcceptanceWholeProjectCheckInventory>>? checks = null,
        IReadOnlyList<AcceptanceTestClassDescriptor>? classes = null) =>
        AcceptanceSelectedExecutionPlan.Create(Changes, ReverseDependencyTestSelection.Resolved(["Selected"]),
            Lanes, classes ?? Classes, [], [], selectors, checks);
}
