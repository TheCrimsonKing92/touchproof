using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

public sealed class ReverseDependencyBreadthBoundTests
{
    private const string GitCliHubPath = "src/Mcg.AgentOrchestrator.Execution/Processes/GitCli.cs";
    private const string PipeDrainHubPath = "src/Mcg.AgentOrchestrator.Execution/Processes/PipeDrain.cs";
    private const string SqliteHubPath = "src/Mcg.AgentOrchestrator.Execution/Persistence/SqliteOrchestratorStateRepository.cs";

    [Fact]
    public void Apply_OverBound_RemovesOnlyReverseCheckAndPreservesSummary()
    {
        var unrelated = new RepositoryTestImpactCheck("changed tests", [], "existing reason");
        var plan = new RepositoryTestImpactPlan(true, false, "Original summary.", [ReverseCheck(34), unrelated]);

        var bounded = ReverseDependencyBreadthBound.Apply(plan, []);

        Assert.Same(unrelated, Assert.Single(bounded.Checks));
        Assert.StartsWith("Original summary. reach-deferred-breadth:", bounded.Summary);
        Assert.Contains("breadth was deferred to the full gate", bounded.Summary);
        Assert.EndsWith("selected_classes=34 exceeds bound=33", bounded.Summary);
    }

    [Fact]
    public void Apply_Exactly33DistinctClasses_KeepsCheck()
    {
        var plan = new RepositoryTestImpactPlan(true, false, "Original summary.", [ReverseCheck(33)]);

        Assert.Same(plan, ReverseDependencyBreadthBound.Apply(plan, []));
    }

    [Theory]
    [InlineData(GitCliHubPath)]
    [InlineData(PipeDrainHubPath)]
    [InlineData(SqliteHubPath)]
    public void Apply_HubFileBelowBound_RemovesReverseCheck(string hubPath)
    {
        var plan = new RepositoryTestImpactPlan(true, false, "", [ReverseCheck(1)]);

        var bounded = ReverseDependencyBreadthBound.Apply(plan, [hubPath]);

        Assert.Empty(bounded.Checks);
        Assert.StartsWith("reach-deferred-breadth:", bounded.Summary);
        Assert.Contains("breadth was deferred to the full gate", bounded.Summary);
        Assert.EndsWith($"reason=hub_file={hubPath}", bounded.Summary);
    }

    [Theory]
    [InlineData("src/Mcg.AgentOrchestrator.Execution/Processes/Other.cs")]
    [InlineData("src/Other/GitCli.cs")]
    [InlineData("nested/" + GitCliHubPath)]
    public void Apply_NonHubBelowBound_KeepsCheck(string changedPath)
    {
        var plan = new RepositoryTestImpactPlan(true, false, "Original summary.", [ReverseCheck(1)]);

        Assert.Same(plan, ReverseDependencyBreadthBound.Apply(plan, [changedPath]));
    }

    [Fact]
    public void Apply_OverBoundAndHubFiles_ReportsEveryReasonInStableOrder()
    {
        var plan = new RepositoryTestImpactPlan(true, false, "", [ReverseCheck(34)]);

        var bounded = ReverseDependencyBreadthBound.Apply(plan,
            [@".\SRC\MCG.AGENTORCHESTRATOR.EXECUTION\PROCESSES\GITCLI.CS", SqliteHubPath, SqliteHubPath]);

        Assert.Empty(bounded.Checks);
        Assert.EndsWith(
            $"reason=selected_classes=34 exceeds bound=33; hub_file={SqliteHubPath}; hub_file={GitCliHubPath}",
            bounded.Summary);
        Assert.DoesNotContain('\n', bounded.Summary);
    }

    [Fact]
    public void Apply_HubFileWithoutReverseCheck_KeepsPlan()
    {
        var plan = new RepositoryTestImpactPlan(true, false, "Original summary.", []);

        Assert.Same(plan, ReverseDependencyBreadthBound.Apply(plan, [GitCliHubPath]));
    }

    private static RepositoryTestImpactCheck ReverseCheck(int distinctClassCount)
    {
        var classes = Enumerable.Range(0, distinctClassCount).Select(index => $"DependentTests{index}").ToArray();
        return new RepositoryTestImpactCheck(ReverseDependencyEvidenceCostGate.CheckName,
            [], "reverse-dependent coverage", RepositoryTestProject.Infrastructure, [.. classes, classes[0]]);
    }
}
