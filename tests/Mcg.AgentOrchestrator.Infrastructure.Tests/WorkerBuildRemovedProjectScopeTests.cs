using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = WorkerBuildProjectScopeTests.Fixture;

// Parallel-safe: each case owns its repository, pinned baseline and cleanup root.
public sealed class WorkerBuildRemovedProjectScopeTests
{
    [Theory]
    [InlineData("committed")]
    [InlineData("staged")]
    [InlineData("dirty")]
    public void Resolve_RemovedProject_OnlyBuildsSurvivingRequest(string removal)
    {
        using var fixture = new Fixture();
        var removedDirectory = Path.Combine(fixture.Root, "Execution");
        if (removal == "dirty") Directory.Delete(removedDirectory, recursive: true);
        else
        {
            fixture.Git("rm", "-r", "Execution");
            if (removal == "committed") fixture.Commit();
        }
        Assert.False(Directory.Exists(removedDirectory));
        Assert.False(Path.Exists(Path.Combine(removedDirectory, "Execution.cs")));
        Assert.Contains("Execution/Execution.cs", fixture.Git("diff", "--name-only",
            fixture.Metadata().GoalBaseline, "--"), StringComparison.Ordinal);

        var scope = WorkerBuildProjectScope.Resolve(fixture.Root, ["Tests/Tests.csproj"]);

        Assert.Equal(["Tests/Tests.csproj"], scope.RequestedProjects);
        Assert.Empty(scope.RequiredProjects);
        Assert.Equal(["Tests/Tests.csproj"], scope.Projects);
        Assert.DoesNotContain(scope.Projects, project => project.StartsWith("Execution/", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_ExistingUnownedSource_StillThrows()
    {
        using var fixture = new Fixture();
        fixture.Write("Unowned.cs", "public class Unowned { }");
        Assert.True(File.Exists(Path.Combine(fixture.Root, "Unowned.cs")));

        var error = Assert.Throws<IOException>(() =>
            WorkerBuildProjectScope.Resolve(fixture.Root, ["Tests/Tests.csproj"]));

        Assert.Contains("unresolved compiled source: Unowned.cs", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_RemovedSourceWithMalformedOwner_StillThrowsProjectError()
    {
        using var fixture = new Fixture();
        fixture.Write("Execution/Gone.fs", "module Gone");
        fixture.Commit();
        fixture.Write("Execution/Execution.csproj", "<Project");
        File.Delete(Path.Combine(fixture.Root, "Execution/Gone.fs"));
        Assert.False(Path.Exists(Path.Combine(fixture.Root, "Execution/Gone.fs")));
        Assert.Equal("<Project", File.ReadAllText(Path.Combine(fixture.Root, "Execution/Execution.csproj")));

        var error = Assert.Throws<IOException>(() =>
            WorkerBuildProjectScope.Resolve(fixture.Root, ["Tests/Tests.csproj"]));

        Assert.Contains("Execution/Gone.fs:", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("unresolved compiled source: Execution/Gone.fs", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_RemovedSourceWithSurvivingOwner_StillRequiresOwner()
    {
        using var fixture = new Fixture();
        File.Delete(Path.Combine(fixture.Root, "Core/Core.cs"));
        Assert.False(Path.Exists(Path.Combine(fixture.Root, "Core/Core.cs")));
        Assert.True(File.Exists(Path.Combine(fixture.Root, "Core/Core.csproj")));

        var scope = WorkerBuildProjectScope.Resolve(fixture.Root, ["Tests/Tests.csproj"]);

        Assert.Equal(["Core/Core.csproj"], scope.RequiredProjects);
        Assert.Equal(["Core/Core.csproj", "Tests/Tests.csproj"], scope.Projects);
    }
}
