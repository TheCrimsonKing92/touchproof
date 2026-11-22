using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ConductorVerbStopTests
{
    [Fact]
    public void StopCreatesRequestOnlyForRunningOwnerAndLeavesGoalAliasesDistinct()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var path = Path.Combine(fixture.Workspace.ExecutionDirectory, ".conduct-stop");
        using var output = new StringWriter();
        Assert.Equal(0, CliConductorCommand.Run(["conductor", "stop"], fixture.Workspace,
            lockProbe: new ConductorVerbStartTests.FixedProbe(null), output: output));
        Assert.False(File.Exists(path));
        Assert.Contains("No conductor is running", output.ToString());
        output.GetStringBuilder().Clear();
        Assert.Equal(0, CliConductorCommand.Run(["conductor", "stop"], fixture.Workspace,
            lockProbe: new ConductorVerbStartTests.FixedProbe(7654), output: output));
        Assert.True(File.Exists(path));
        Assert.Contains("7654", output.ToString());
        Assert.Contains("detach, not a drain", output.ToString());
        Assert.False(CliConductorCommand.IsCommand(["status", "abcdef12"]));
        Assert.False(CliConductorCommand.IsCommand(["stop", "abcdef12", "r", "--as", "cancel"]));
    }

    [Theory]
    [InlineData(null, 1800d)]
    [InlineData("2.5", 2.5)]
    public void StopDrainWritesDrainSignalWithoutStopFile(string? seconds, double expectedSeconds)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        using var output = new StringWriter();
        string[] args = seconds is null
            ? ["conductor", "stop", "--drain"]
            : ["conductor", "stop", "--drain", "--drain-timeout-seconds", seconds];

        Assert.Equal(0, CliConductorCommand.Run(args, fixture.Workspace,
            lockProbe: new ConductorVerbStartTests.FixedProbe(7654), output: output));
        var stopPath = fixture.Workspace.ConductorStopFilePath;
        var drainPath = ConductorDrainSignal.PathFor(stopPath);
        Assert.False(File.Exists(stopPath));
        using var request = System.Text.Json.JsonDocument.Parse(File.ReadAllText(drainPath));
        Assert.Equal(expectedSeconds, request.RootElement.GetProperty("TimeoutSeconds").GetDouble());
        Assert.Contains("Drain requested for conductor pid 7654", output.ToString());
        Assert.Contains(drainPath, output.ToString());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e999")]
    [InlineData("invalid")]
    [InlineData(null)]
    public void StopDrainRejectsInvalidTimeoutBeforeWriting(string? seconds)
    {
        string[] args = seconds is null
            ? ["conductor", "stop", "--drain", "--drain-timeout-seconds"]
            : ["conductor", "stop", "--drain", "--drain-timeout-seconds", seconds];
        AssertRejected(args);
    }

    [Fact]
    public void StopDrainTimeoutWithoutDrainIsRejected() =>
        AssertRejected(["conductor", "stop", "--drain-timeout-seconds", "10"]);

    [Fact]
    public void StopDrainWithoutOwnerWritesNothing()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        using var output = new StringWriter();
        Assert.Equal(0, CliConductorCommand.Run(["conductor", "stop", "--drain"], fixture.Workspace,
            lockProbe: new ConductorVerbStartTests.FixedProbe(null), output: output));
        Assert.Contains("No conductor is running; nothing to stop.", output.ToString());
        AssertNoFiles(fixture.Workspace);
    }

    private static void AssertRejected(string[] args)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        using var error = new StringWriter();
        Assert.Equal(1, CliConductorCommand.Run(args, fixture.Workspace,
            lockProbe: new ConductorVerbStartTests.FixedProbe(7654), error: error));
        Assert.Equal(CliCommandHelp.ConductorUsage + Environment.NewLine, error.ToString());
        AssertNoFiles(fixture.Workspace);
    }

    private static void AssertNoFiles(OrchestratorWorkspace workspace)
    {
        Assert.False(File.Exists(workspace.ConductorStopFilePath));
        Assert.False(File.Exists(ConductorDrainSignal.PathFor(workspace.ConductorStopFilePath)));
    }
}
