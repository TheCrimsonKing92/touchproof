using Mcg.AgentOrchestrator.Core;

public sealed class WorkerToolsInstallDispatchTests
{
    [Fact]
    public void DeveloperDispatch_UsesPinnedInstallAndReusesIt()
    {
        using var fixture = new WorkerToolsDispatchFixture();
        var first = fixture.Prepare(AgentRole.Developer);
        var install = Assert.IsType<string>(first.Task.LastDispatch?.WorkerToolsDirectory);
        Assert.Equal("pinned", File.ReadAllText(Path.Combine(install, "version.txt")));
        Assert.Equal("published", Assert.Single(File.ReadAllLines(fixture.BuildLog)));

        var environment = fixture.CaptureStart(first.Kernel, first.Goal, first.Task).Environment;
        Assert.Equal(install, environment["PATH"]!.Split(Path.PathSeparator)[0]);
        var telemetry = Assert.IsType<string>(environment["WORKER_TOOLS_TELEMETRY"]);
        Assert.Equal(fixture.LogRoot, Directory.GetParent(Path.GetDirectoryName(telemetry)!)!.FullName);
        Assert.True(Directory.Exists(Path.GetDirectoryName(telemetry)));

        // A new installer on a new dispatch must use the durable cache without running publish.
        var second = fixture.Prepare(AgentRole.Developer);
        Assert.Equal(install, second.Task.LastDispatch?.WorkerToolsDirectory);
        Assert.Equal("published", Assert.Single(File.ReadAllLines(fixture.BuildLog)));
        var secondEnvironment = fixture.CaptureStart(second.Kernel, second.Goal, second.Task).Environment;
        Assert.NotEqual(telemetry, secondEnvironment["WORKER_TOOLS_TELEMETRY"]);
    }
}
