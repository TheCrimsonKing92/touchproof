using Mcg.AgentOrchestrator.Infrastructure;
using static HostTestSettingsFixture;

// Parallel-safe: each case owns its configuration and executor claims.
public sealed class HostTestSettingsExecutorValidationTests
{
    [Theory]
    [InlineData("{\"mcg_test_x\":\"2\"}")]
    [InlineData("{\"MCG_TEST_SLOT_TIMING_FILE\":\"2\"}")]
    [InlineData("{\"MCG_TEST_BUILD_OUTPUT_ROOT\":\"2\"}")]
    [InlineData("{\"MCG_TEST_ONLY_ACCEPTANCE_LEGACY_START_THEN_ATTACH\":\"2\"}")]
    [InlineData("{\"MCG_TEST_INPUT_FOO\":\"2\"}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"MCG_TEST_HOST_CAPACITY_SLOTS\":2}")]
    [InlineData("{\"MCG_TEST_HOST_CAPACITY_SLOTS\":\"\"}")]
    [InlineData("{\"MCG_TEST_HOST_CAPACITY_SLOTS\":\"a\\nb\"}")]
    [InlineData("{\"MCG_TEST_HOST_CAPACITY_SLOTS\":\"a\\rb\"}")]
    [InlineData("{\"MCG_TEST_HOST_CAPACITY_SLOTS\":\"a\\u0000b\"}")]
    [InlineData("{\"MCG_TEST_HOST_CAPACITY_SLOTS\":\"2\",\"MCG_TEST_HOST_CAPACITY_SLOTS\":\"3\"}")]
    public void InvalidMap_DisablesOnlyItsExecutorWithTypedReason(string map)
    {
        using var fixture = new HostTestSettingsFixture();
        var config = fixture.Load("{\"executors\":[{\"id\":\"bad\",\"testSettings\":" + map +
            "},{\"id\":\"good\"}],\"lanes\":[\"lane\"]}");
        Assert.True(config.Enabled);
        Assert.Equal(2, config.Executors.Count);
        Assert.False(config.Executors[0].AdmissionEnabled);
        Assert.Equal(RemoteLaneExecutorEntry.InvalidTestSettings, config.Executors[0].DisabledReason);
        Assert.Null(config.Executors[0].TestSettings);
        Assert.True(config.Executors[1].AdmissionEnabled);
        Assert.Null(config.Executors[1].DisabledReason);
        using var coordinator = fixture.Coordinator(config);
        Assert.Equal("good", coordinator.TryClaimIdleExecutor()!.Id);
        Assert.Null(coordinator.TryClaimIdleExecutor());
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"testSettings\":{}")]
    public void MissingOrEmptyMap_PreservesManifest(string property)
    {
        using var fixture = new HostTestSettingsFixture();
        var config = fixture.Load("{\"executors\":[{\"id\":\"one\"" + property + "}],\"lanes\":[\"lane\"]}");
        Assert.True(config.Enabled);
        Assert.True(Assert.Single(config.Executors).AdmissionEnabled);
        var manifest = Manifest;
        using var coordinator = fixture.Coordinator(config, manifest);
        Assert.Same(manifest.DeclaredTestSettings,
            coordinator.CreateRequest("lane", "tests.csproj", "filter", Identity(), "one").TestSettings);
    }
}
