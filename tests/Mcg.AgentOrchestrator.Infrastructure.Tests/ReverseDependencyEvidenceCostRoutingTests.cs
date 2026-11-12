using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Fixture = PreTesterCoupledClassSelectionsTests.Fixture;

// Parallel-safe: source fixtures are private; no processes or shared stores.
public sealed class ReverseDependencyEvidenceCostRoutingTests
{
    [Fact]
    public void Apply_CoreOnlySkip_PreservesOtherEvidenceSources()
    {
        using var fixture = new Fixture();
        fixture.Write(Fixture.Source, "public class NewContract {}");
        fixture.Test("DependentTests", "NewContract");
        var summary = RepositoryChangeClassifier.Classify([Fixture.Source]);
        var plan = RepositoryTestImpactPlanner.Plan(summary, fixture.Root);
        var reverse = Assert.Single(plan.Checks.Where(check => check.Name == ReverseDependencyEvidenceCostGate.CheckName));
        Assert.Contains("DependentTests", reverse.TestClassSelections!);
        var other = new[]
        {
            Check("Developer declared", "DependentTests"), Check("brief named", "BriefTests"),
            Check("open finding", "FindingTests"), Check("changed-file guard", "GuardTests")
        };
        plan = plan with { Checks = [.. plan.Checks, .. other] };
        var lines = new List<string>();
        var filtered = ReverseDependencyEvidenceCostGate.Apply(plan, _ => new(50, 100, 0), lines.Add);
        Assert.Single(lines);
        Assert.Contains("decision=skip", lines[0]);
        Assert.Equal(plan.Checks.Where(check => check != reverse), filtered.Checks);
        var context = PreReviewEvidenceContextBuilder.Build("candidate", summary, filtered);
        Assert.Equal(new[] { "Infrastructure.Tests: FullyQualifiedName~DependentTests", "Infrastructure.Tests: FullyQualifiedName~BriefTests",
            "Infrastructure.Tests: FullyQualifiedName~FindingTests", "Infrastructure.Tests: FullyQualifiedName~GuardTests" }, context.SelectedFocusedTests);
        Assert.NotNull(context.FocusedRequest);
        Assert.False(context.MappingNeedsInput);
    }

    [Fact]
    public void Apply_OnlyReverseCheckSkipped_DefersToGate()
    {
        var plan = new RepositoryTestImpactPlan(true, false, "core", [Check(
            ReverseDependencyEvidenceCostGate.CheckName, "DependentTests")]);
        var filtered = ReverseDependencyEvidenceCostGate.Apply(plan, _ => new(50, 100, null), _ => { });
        var context = PreReviewEvidenceContextBuilder.Build("candidate",
            RepositoryChangeClassifier.Classify([Fixture.Source]), filtered);
        Assert.True(context.NoApplicableTests);
        Assert.Null(context.FocusedRequest);
    }

    [Fact]
    public void Apply_RunOrAbsent_ReturnsOriginalPlan()
    {
        var check = Check(ReverseDependencyEvidenceCostGate.CheckName, "DependentTests");
        var plan = new RepositoryTestImpactPlan(true, false, "core", [check]);
        var lines = new List<string>();
        Assert.Same(plan, ReverseDependencyEvidenceCostGate.Apply(plan, _ => new(10, 100, 0.5), lines.Add));
        Assert.Contains("decision=run", Assert.Single(lines));
        var absent = plan with { Checks = [Check("other", "DependentTests")] };
        Assert.Same(absent, ReverseDependencyEvidenceCostGate.Apply(absent,
            _ => throw new InvalidOperationException("must not read inputs"), _ => throw new InvalidOperationException("must not log")));
    }

    [Fact]
    public void BuildContext_CoreOnlyWithoutHistory_AppliesSkipOnWiredPath()
    {
        using var fixture = new Fixture();
        fixture.Write(Fixture.Source, "public class NewContract {}");
        fixture.Test("DependentTests", "NewContract");
        var plan = RepositoryTestImpactPlanner.Plan(RepositoryChangeClassifier.Classify([Fixture.Source]), fixture.Root);
        Assert.Contains(plan.Checks, check => check.Name == ReverseDependencyEvidenceCostGate.CheckName);
        var context = ConductorDriver.BuildPreReviewEvidenceContext("candidate", [Fixture.Source], fixture.Root);
        Assert.True(context.NoApplicableTests);
        Assert.Empty(context.SelectedFocusedTests);
    }

    private static RepositoryTestImpactCheck Check(string name, string testClass) => new(name,
        ["dotnet", "test", "--project", Fixture.Project, "--filter", "FullyQualifiedName~" + testClass],
        "test source", RepositoryTestProject.Infrastructure, [testClass]);
}
