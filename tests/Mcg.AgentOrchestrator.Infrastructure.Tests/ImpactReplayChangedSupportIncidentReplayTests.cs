using Mcg.AgentOrchestrator.Infrastructure;

// Reads the verified checkout without mutating the tree.
public sealed class ImpactReplayChangedSupportIncidentReplayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncidentSupportChanges_ReachGoalBranchScenarioTemplateTests(bool writerOnly)
    {
        const string directory = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/";
        const string writer = directory + "FixtureRepositoryWriter.cs";
        var checkout = VerifiedRepositoryRoot.Find();
        string[] changed = writerOnly ? [writer] :
            [directory + "InProcessGitFunnel.cs", writer, directory + "GoalRepositoryWorktreeProvisionerFixture.cs",
             directory + "MultiRepositoryAcceptanceGateFixture.cs"];
        var routing = new ImpactReplayChangedSourceRouting(checkout);
        Assert.All(changed, path => Assert.True(routing.IsTestSource(path), path));

        var result = ImpactReplayChangedTestSupportSelection.Read(checkout, changed, routing);

        Assert.Equal(changed.Order(StringComparer.Ordinal), result.SupportSelections.Select(summary => summary.Path));
        var writerSummary = Assert.Single(result.SupportSelections, summary => summary.Path == writer);
        Assert.True(writerSummary.ClosureCount > writerSummary.DirectMatchCount);
        Assert.True(result.Classes.Contains("GoalBranchScenarioTemplateTests") ||
            result.SupportSelections.Any(summary => summary.FullRunTriggered &&
                summary.AffectedProjects.Contains(AcceptancePolicyShardPlanner.InfrastructureTestsProject)));
    }
}
