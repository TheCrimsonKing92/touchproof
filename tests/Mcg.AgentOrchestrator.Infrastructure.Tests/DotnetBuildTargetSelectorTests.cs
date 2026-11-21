using Mcg.AgentOrchestrator.Infrastructure;

public sealed class DotnetBuildTargetSelectorTests
{
    [Fact]
    public void BuildsRootSolutionsInNameOrderWithSlnxPreferred()
    {
        using var fixture = new WorkerBuildRequestFixture(seedRepository: root =>
        {
            File.WriteAllText(Path.Combine(root, "B.sln"), "ignored because B.slnx is present");
            File.WriteAllText(Path.Combine(root, "B.slnx"),
                """<Solution><Project Path="src/Feature/Feature.csproj" /></Solution>""");
            File.WriteAllText(Path.Combine(root, "A.slnx"), "<Solution />");
            File.WriteAllText(Path.Combine(root, "src", "Nested.slnx"), "<Solution />");
        });

        var selection = new DotnetProjectDiscoveryAdapter().SelectBuildTargets(fixture.RepositoryRoot);

        Assert.Null(selection.Error);
        Assert.Equal(["A.slnx", "B.slnx"], selection.Targets);
        Assert.Equal([WorkerBuildRequestFixture.Project], selection.Projects);
    }

    [Fact]
    public void SelectsTrackedProjectsWhenThereIsNoRootSolution()
    {
        using var fixture = new WorkerBuildRequestFixture(extraProject: true);
        var untracked = Path.Combine(fixture.RepositoryRoot, "Untracked.csproj");
        File.WriteAllText(untracked, "<Project />");

        var selection = new DotnetProjectDiscoveryAdapter().SelectBuildTargets(fixture.RepositoryRoot);

        Assert.Null(selection.Error);
        Assert.Equal([WorkerBuildRequestFixture.Project, WorkerBuildRequestFixture.OtherProject], selection.Targets);
        Assert.Equal(selection.Targets, selection.Projects);
    }

    [Fact]
    public void MalformedSelectedSolutionIsAnError()
    {
        using var fixture = new WorkerBuildRequestFixture(seedRepository: root =>
            File.WriteAllText(Path.Combine(root, "Broken.slnx"), "<broken>"));

        var selection = new DotnetProjectDiscoveryAdapter().SelectBuildTargets(fixture.RepositoryRoot);

        Assert.NotNull(selection.Error);
        Assert.Null(selection.AbsenceProof);
        Assert.Empty(selection.Targets);
    }
}
