using Mcg.AgentOrchestrator.Infrastructure;
using static HostTestSettingsFixture;

// Parallel-safe: owns dictionaries and files; never mutates the parent environment.
public sealed class HostTestSettingsLocalEnvironmentTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LocalSettings_OverrideManifestAfterSealAndDropAmbient(bool mtp)
    {
        using var fixture = new HostTestSettingsFixture();
        var config = fixture.Load("""{"executors":[],"lanes":[],"localTestSettings":{"MCG_TEST_HOST_CAPACITY_SLOTS":"2"}}""");
        Assert.Equal("empty", config.DisabledReason); // Local overrides survive disabled remote dispatch.
        var manifest = Manifest;
        var snapshot = AcceptanceHostTestSettings.Resolve(manifest.DeclaredTestSettings, config);
        var request = new AcceptanceProcessRequest { EngineSettings = manifest, HostTestSettings = snapshot.ForLocal("lane") };
        var environment = new Dictionary<string, string?> { [Slots] = "99", ["MCG_TEST_AMBIENT_ONLY"] = "ambient" };
        var arguments = mtp
            ? new[] { "dotnet", "test.dll", "--results-directory", "results", "--report-trx-filename", "lane.trx" }
            : new[] { "dotnet", "test", "test.csproj", "--results-directory", "results", "--logger", "trx;LogFileName=lane.trx" };
        AcceptanceShellProcessStartInfo.ConfigureCheckEnvironment(environment, arguments, fixture.Root, request);
        Assert.Equal("2", environment[Slots]);
        Assert.Equal("kept", environment["MCG_TEST_MANIFEST_ONLY"]);
        Assert.False(environment.ContainsKey("MCG_TEST_AMBIENT_ONLY"));
        Assert.Equal(LaneSlotTimingReader.PathFor(Path.Combine(fixture.Root, "results", "lane.trx")),
            environment[LaneSlotTimingReader.VariableName]);
        Assert.Equal("4", manifest.DeclaredTestSettings[Slots]);
        Assert.Equal("local", request.HostTestSettings.Host);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"MCG_TEST_HOST_CAPACITY_SLOTS\":\"2\",\"MCG_TEST_INPUT_X\":\"x\"}")]
    public void InvalidLocalMap_IsReportedAndEntireOverlayDiscarded(string map)
    {
        using var fixture = new HostTestSettingsFixture();
        var config = fixture.Load("{\"executors\":[{\"id\":\"one\"}],\"lanes\":[\"lane\"],\"localTestSettings\":" + map + "}");
        Assert.True(config.Enabled);
        Assert.Contains("localTestSettings", config.LocalTestSettingsFault);
        Assert.Null(config.LocalTestSettings);
        var manifest = Manifest;
        var snapshot = AcceptanceHostTestSettings.Resolve(manifest.DeclaredTestSettings, config);
        Assert.Same(manifest.DeclaredTestSettings, snapshot.Local);
        var environment = new Dictionary<string, string?> { [Slots] = "99" };
        AcceptanceShellProcessStartInfo.ConfigureCheckEnvironment(environment,
            ["dotnet", "test.dll", "--results-directory", "results", "--report-trx-filename", "lane.trx"], fixture.Root,
            new() { EngineSettings = manifest, HostTestSettings = snapshot.ForLocal("lane") });
        Assert.Equal("4", environment[Slots]);
    }
}
