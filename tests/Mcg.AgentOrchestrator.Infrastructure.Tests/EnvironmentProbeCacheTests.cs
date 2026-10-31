using Mcg.AgentOrchestrator.Infrastructure;

public sealed class EnvironmentProbeCacheTests
{
    [Fact]
    public void UnchangedPassIsReused_PathBuildContractAndNamedVariableChangesReprobe_AndPassExpires()
    {
        var runner = new EnvironmentContractTestFixture.Runner { Pass = true };
        var time = new Clock();
        var cache = new EnvironmentProbeCache(runner, time);
        var contract = new EnvironmentContract(1, [EnvironmentContractTestFixture.Contract.Tools[0] with { Variables = ["CUSTOM_HOME"] }]);
        var directory = Path.GetTempPath();
        void Probe(ConductorChildEnvironment environment, string build = "build", EnvironmentContract? declaration = null)
        {
            var current = declaration ?? contract;
            var fingerprint = EnvironmentFingerprint.Create(build, current, environment, directory);
            cache.Probe(current, fingerprint, environment, directory);
        }
        var original = EnvironmentContractTestFixture.Environment();
        Probe(original);
        Probe(original);
        Assert.Equal(1, runner.Calls);
        Probe(EnvironmentContractTestFixture.Environment("changed-path"));
        Assert.Equal(2, runner.Calls);
        Probe(original, "new-build");
        Assert.Equal(3, runner.Calls);
        Probe(new(new Dictionary<string, string?> { ["PATH"] = "sentinel-path", ["CUSTOM_HOME"] = "changed" }));
        Assert.Equal(4, runner.Calls);
        Probe(original, declaration: new(1, [contract.Tools[0] with { Arguments = ["-V"] }]));
        Assert.Equal(5, runner.Calls);
        time.Now += TimeSpan.FromMinutes(15);
        Probe(original);
        Assert.Equal(6, runner.Calls);
    }

    [Fact]
    public void FailedProbe_IsNeverReused()
    {
        var runner = new EnvironmentContractTestFixture.Runner();
        var cache = new EnvironmentProbeCache(runner);
        var environment = EnvironmentContractTestFixture.Environment();
        cache.Probe(EnvironmentContractTestFixture.Contract, "fingerprint", environment, Path.GetTempPath());
        runner.Pass = true;
        Assert.All(cache.Probe(EnvironmentContractTestFixture.Contract, "fingerprint", environment, Path.GetTempPath()), result => Assert.True(result.Passed));
        Assert.Equal(2, runner.Calls);
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
