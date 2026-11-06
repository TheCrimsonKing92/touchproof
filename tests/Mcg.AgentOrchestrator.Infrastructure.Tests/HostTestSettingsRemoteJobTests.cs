using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using static HostTestSettingsFixture;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

// Parallel-safe: temporary configurations and fake SSH transport; no child processes.
public sealed class HostTestSettingsRemoteJobTests
{
    [Fact]
    public async Task Jobs_UseTheirOwnHostOverlayAndKeepManifestDefaultForOtherExecutor()
    {
        using var fixture = new HostTestSettingsFixture();
        var manifest = Manifest;
        var configuration = fixture.Load("""
            {"executors":[
              {"id":"one","transport":"ssh","runnerAlias":"runner","adminAlias":"admin","remoteRepository":"C:/repo/bare.git",
               "testSettings":{"MCG_TEST_HOST_CAPACITY_SLOTS":"2","MCG_TEST_HOST_ONLY":"added"}},
              {"id":"two","transport":"ssh","runnerAlias":"runner2","adminAlias":"admin2","remoteRepository":"C:/repo/bare.git"}],
             "lanes":["lane"]}
            """);
        Assert.True(configuration.Enabled);
        using var coordinator = fixture.Coordinator(configuration, manifest);
        var identity = Identity() with { VerifyingCommitSha = "123456789abcdef" };
        foreach (var (host, slots) in new[] { ("one", "2"), ("two", "4") })
        {
            await using var fake = new FakeSshRemoteLaneTransport();
            var executor = new SshRemoteLaneExecutor(configuration, fake.Root, Path.Combine(fake.Root, "attempt", "result"),
                fake.Clock, fake.Transport, fake.Git, TimeSpan.Zero);
            var request = coordinator.CreateRequest("lane", InfrastructureTestsProject, "FullyQualifiedName~Probe", identity, host);
            if (host == "two") Assert.Same(manifest.DeclaredTestSettings, request.TestSettings);
            var submission = await executor.SubmitAsync(request, default);
            Assert.Null(submission.FailureReason);
            fake.Handle = Assert.IsType<SshRemoteLaneHandle>(submission.Handle);
            var copy = Assert.Single(fake.Calls.Where(call => call.Arguments.Contains("job.json")));
            using var job = JsonDocument.Parse(File.ReadAllText(Path.Combine(copy.Directory, "job.json")));
            var settings = job.RootElement.GetProperty("testSettings");
            Assert.Equal(slots, settings.GetProperty(Slots).GetString());
            Assert.Equal("kept", settings.GetProperty("MCG_TEST_MANIFEST_ONLY").GetString());
            Assert.Equal(host == "one", settings.TryGetProperty("MCG_TEST_HOST_ONLY", out _));
            Assert.Equal(host, job.RootElement.GetProperty("testSettingsHost").GetString());
        }
        Assert.Equal("4", manifest.DeclaredTestSettings[Slots]);
        Assert.Equal("2", configuration.Executors[0].TestSettings![Slots]);
    }

    [Fact]
    public async Task GateRequest_UsesFrozenHostSettingsAfterConfigurationFileChanges()
    {
        using var fixture = new HostTestSettingsFixture();
        var config = fixture.Load("""{"executors":[{"id":"one","testSettings":{"MCG_TEST_HOST_CAPACITY_SLOTS":"2"}}],"lanes":["infrastructure tests: lane"]}""");
        var fake = new FakeRemoteLaneExecutor();
        using var coordinator = fixture.Coordinator(config, executor: fake);
        File.WriteAllText(fixture.ConfigurationPath, "{}");
        var entry = Assert.IsType<RemoteLaneExecutorEntry>(coordinator.TryClaimIdleExecutor());
        var outcome = await coordinator.RunRemoteAsync(new()
        {
            Name = "infrastructure tests: lane", Type = "dotnet-test", Project = InfrastructureTestsProject,
            Arguments = ["--filter", "FullyQualifiedName~Probe"]
        }, entry, RemoteLaneDeadline.Floor, RemoteLaneDeadline.Floor, default);
        var request = Assert.Single(fake.Requests);
        Assert.Equal("2", request.TestSettings![Slots]);
        Assert.Equal("one", request.TestSettingsHost);
        Assert.Same(request.TestSettings, outcome.Request.TestSettings);
    }
}
