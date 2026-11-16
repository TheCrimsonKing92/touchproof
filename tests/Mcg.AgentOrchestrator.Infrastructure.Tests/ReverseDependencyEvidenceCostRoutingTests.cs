using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
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
        var filtered = ReverseDependencyEvidenceCostGate.Apply(plan, _ => new(50, 100, 0), _ => { });
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
        Assert.DoesNotContain("reach-skipped-cost", plan.Summary);
        lines.Clear();
        Assert.Same(plan, ReverseDependencyEvidenceCostGate.Apply(plan, _ => new(10, 100, null), lines.Add));
        Assert.Contains("decision=run", Assert.Single(lines));
        Assert.DoesNotContain("reach-skipped-cost", plan.Summary);
        var absent = plan with { Checks = [Check("other", "DependentTests")] };
        Assert.Same(absent, ReverseDependencyEvidenceCostGate.Apply(absent,
            _ => throw new InvalidOperationException("must not read inputs"), _ => throw new InvalidOperationException("must not log")));
        Assert.DoesNotContain("reach-skipped-cost", absent.Summary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_Skip_RecordsClassesThroughReceipt(bool retainOtherCheck)
    {
        var other = Check("Developer declared", "OtherTests");
        var plan = new RepositoryTestImpactPlan(true, false, "original summary", [
            Check(ReverseDependencyEvidenceCostGate.CheckName, "DependentTests"),
            Check(ReverseDependencyEvidenceCostGate.CheckName, "SecondDependentTests"),
            Check(ReverseDependencyEvidenceCostGate.CheckName, "DependentTests")]);
        if (retainOtherCheck) plan = plan with { Checks = [.. plan.Checks, other] };
        var lines = new List<string>();

        var filtered = ReverseDependencyEvidenceCostGate.Apply(plan, selections =>
        {
            Assert.Equal(new[] { "DependentTests", "SecondDependentTests" }, selections);
            return new(50, 100, 0);
        }, lines.Add);

        Assert.DoesNotContain(filtered.Checks, check => check.Name == ReverseDependencyEvidenceCostGate.CheckName);
        if (retainOtherCheck) Assert.Same(other, Assert.Single(filtered.Checks));
        else Assert.Empty(filtered.Checks);
        Assert.StartsWith(plan.Summary + " reach-skipped-cost:", filtered.Summary);
        var skippedClasses = "DependentTests, SecondDependentTests";
        Assert.Contains(skippedClasses, filtered.Summary);
        Assert.Equal(filtered.Summary.IndexOf(skippedClasses, StringComparison.Ordinal),
            filtered.Summary.LastIndexOf(skippedClasses, StringComparison.Ordinal));
        var decisionLine = Assert.Single(lines);
        Assert.Contains("decision=skip", decisionLine);
        Assert.EndsWith(decisionLine, filtered.Summary);
        Assert.True(filtered.Summary.IndexOf(decisionLine, StringComparison.Ordinal) >
            filtered.Summary.IndexOf(skippedClasses, StringComparison.Ordinal));

        var context = PreReviewEvidenceContextBuilder.Build("candidate",
            RepositoryChangeClassifier.Classify([Fixture.Source]), filtered);
        Assert.False(context.MappingNeedsInput);
        Assert.Equal(!retainOtherCheck, context.NoApplicableTests);
        Assert.Contains(filtered.Summary, context.MappingReason);
        var (_, goal) = ConductorDriverTests.SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        var receipt = PreReviewEvidenceReceipts.Record((_, _, _) => { }, goal, reviewer, context, 1,
            retainOtherCheck ? PreReviewEvidenceDisposition.Green : PreReviewEvidenceDisposition.NoApplicableTests,
            retainOtherCheck
                ? [new AcceptanceCheckResult("reviewer focused evidence: Infrastructure.Tests FullyQualifiedName~OtherTests", true, 0, null)]
                : [], [], null);
        Assert.Equal(context.MappingReason, receipt.MappingReason);
        Assert.Contains("reach-skipped-cost", receipt.MappingReason);
        Assert.Contains(skippedClasses, receipt.MappingReason);
        Assert.Contains(decisionLine, receipt.MappingReason);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("reader")]
    public void Apply_UnavailableSelection_ReportsPlaceholder(string unavailable)
    {
        var check = Check(ReverseDependencyEvidenceCostGate.CheckName, "DependentTests");
        check = check with { TestClassSelections = unavailable switch
        {
            "missing" => null,
            "empty" => [],
            _ => check.TestClassSelections
        } };
        var plan = new RepositoryTestImpactPlan(true, false, "", [check]);
        var lines = new List<string>();

        var filtered = ReverseDependencyEvidenceCostGate.Apply(plan, _ => unavailable switch
        {
            "missing" => throw new InvalidOperationException("must not read inputs without classes"),
            "reader" => ReverseDependencyEvidenceCostInputs.Unavailable("reader\r\nfailed"),
            _ => new(50, 100, 0)
        }, lines.Add);

        Assert.Empty(filtered.Checks);
        Assert.StartsWith("reach-skipped-cost: selection classes unavailable", filtered.Summary);
        Assert.DoesNotContain("DependentTests", filtered.Summary);
        Assert.EndsWith(Assert.Single(lines), filtered.Summary);
        if (unavailable != "empty") Assert.Contains("input_unavailable=", filtered.Summary);
        Assert.DoesNotContain("\r", filtered.Summary);
        Assert.DoesNotContain("\n", filtered.Summary);
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
