using Mcg.AgentOrchestrator.Infrastructure;

public sealed class EnvironmentProbeEnvironmentBlockTests
{
    [Fact]
    public async Task ProbeAndDispatchHost_ReceiveTheSameExplicitEnvironmentBlock()
    {
        var environment = EnvironmentContractTestFixture.Environment("sentinel-worker-path");
        var runner = new EnvironmentContractTestFixture.Runner { Pass = true };
        await runner.RunAsync(EnvironmentContractTestFixture.Contract.Tools[0], environment, Path.GetTempPath());
        var startInfo = DispatchHostStartInfoFactory.Create(Path.GetTempPath(), "host.dll", "parameters.json", "start-gate", environment);
        Assert.Equal("sentinel-worker-path", runner.ObservedEnvironment!.Variables["PATH"]);
        Assert.Equal(runner.ObservedEnvironment.Variables["PATH"], startInfo.Environment["PATH"]);
        Assert.All(environment.Variables, pair => Assert.Equal(pair.Value, startInfo.Environment[pair.Key]));
        Assert.False(startInfo.UseShellExecute);
    }
}
