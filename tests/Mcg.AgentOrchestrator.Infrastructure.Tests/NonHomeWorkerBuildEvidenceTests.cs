using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class NonHomeWorkerBuildEvidenceTests
{
    [Fact]
    public async Task PromptedCommandBuildsTheSelectedSolutionAndEvidenceIsConsumed()
    {
        using var fixture = new WorkerBuildRequestFixture(goalBranch: true, extraProject: true,
            compilerMode: WorkerBuildRequestFixture.CompilerMode.Real, isNonHome: true,
            seedRepository: root => File.WriteAllText(Path.Combine(root, "Fixture.slnx"), """
                <Solution>
                  <Project Path="src/Feature/Feature.csproj" />
                  <Project Path="src/Other/Other.csproj" />
                </Solution>
                """));
        var root = fixture.RepositoryRoot;
        Assert.False(Directory.Exists(Path.Combine(root, "scripts")));
        Directory.CreateDirectory(Path.Combine(root, "src", "New"));
        File.WriteAllText(Path.Combine(root, "src", "New", "New.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        File.WriteAllText(Path.Combine(root, "src", "New", "New.cs"), "public sealed class NewProject { }");

        var goal = fixture.Kernel.GetGoal(fixture.GoalId);
        var task = fixture.Kernel.GetTask(fixture.GoalId, fixture.TaskId);
        var context = WorkerContextArtifacts.Write(goal, task, root, targetHome: fixture.TargetHome);
        var guidance = Assert.Single(File.ReadLines(Path.Combine(context, "current-task.md"))
            .Where(line => line.Contains("worker-build-request --selected-targets", StringComparison.Ordinal)));
        var command = guidance.Split('`')[1];
        var dll = command.Split('"')[1];
        Assert.True(Path.IsPathFullyQualified(dll));
        Assert.True(File.Exists(dll), dll);
        var relativeDll = Path.GetRelativePath(root, dll);
        Assert.True(Path.IsPathRooted(relativeDll) || relativeDll.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));

        var invocation = await fixture.RunHelper(command: command + "; exit $LASTEXITCODE");

        Assert.Equal(WorkerBuildOutcome.Success, invocation.Outcome);
        Assert.Equal(["Fixture.slnx", "src/New/New.csproj"], invocation.BuildTargets);
        Assert.Equal([WorkerBuildRequestFixture.Project, "src/New/New.csproj", WorkerBuildRequestFixture.OtherProject],
            invocation.Projects);
        var evidence = fixture.Consume();
        Assert.False(evidence.MissingEvidence, evidence.Diagnostic);
        Assert.False(evidence.CompilerFailed, evidence.Diagnostic);
        Assert.Contains("consumed=True", evidence.Diagnostic);
    }
}
