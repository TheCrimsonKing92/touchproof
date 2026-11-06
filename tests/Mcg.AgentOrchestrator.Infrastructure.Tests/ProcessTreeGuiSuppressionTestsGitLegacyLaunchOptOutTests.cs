using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ProcessTreeGuiSuppressionTestsGitLegacyLaunchOptOutTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    public async Task OptOut_WhenPresent_RestoresShimFlagsAndEnvironment(string value)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var document = await GitDirectLaunchTestHarness.Run("version", value);
        var report = document.RootElement;
        GitDirectLaunchTestHarness.AssertWindowless(report);
        Assert.False(report.GetProperty("cacheCreatedBefore").GetBoolean());
        Assert.False(report.GetProperty("cacheCreatedAfter").GetBoolean());
        Assert.Equal("git", report.GetProperty("fileName").GetString());
        Assert.Equal(report.GetProperty("shim").GetString(), report.GetProperty("image").GetString(), ignoreCase: true);
        Assert.Equal(HandleListProcessLauncher.CreateNoWindow | HandleListProcessLauncher.UnicodeEnvironment |
                     HandleListProcessLauncher.ExtendedStartup, report.GetProperty("flags").GetUInt32());
        AssertEnvironmentUnchanged(report);
    }

    [Fact]
    public async Task OptOut_AfterDirectLaunch_BypassesTheInitializedCache()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var document = await GitDirectLaunchTestHarness.Run("toggle");
        var report = document.RootElement;
        Assert.False(report.GetProperty("cacheCreatedBefore").GetBoolean());
        Assert.True(report.GetProperty("cacheCreatedAfter").GetBoolean());
        Assert.Equal("git", report.GetProperty("fileName").GetString());
        Assert.Equal(report.GetProperty("shim").GetString(), report.GetProperty("image").GetString(), ignoreCase: true);
        Assert.Equal(HandleListProcessLauncher.CreateNoWindow | HandleListProcessLauncher.UnicodeEnvironment |
                     HandleListProcessLauncher.ExtendedStartup, report.GetProperty("flags").GetUInt32());
        AssertEnvironmentUnchanged(report);
    }

    [Fact]
    public async Task PersistentReader_WithOptOut_KeepsItsExistingConsolePolicy()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var document = await GitDirectLaunchTestHarness.Run("reader", "1");
        var report = document.RootElement;
        GitDirectLaunchTestHarness.AssertWindowless(report);
        Assert.Equal("git", report.GetProperty("fileName").GetString());
        Assert.Equal(report.GetProperty("shim").GetString(), report.GetProperty("image").GetString(), ignoreCase: true);
        Assert.Equal(HandleListProcessLauncher.UnicodeEnvironment | HandleListProcessLauncher.ExtendedStartup,
            report.GetProperty("flags").GetUInt32());
        Assert.Equal("HEAD missing", report.GetProperty("readerResponse").GetString());
        AssertEnvironmentUnchanged(report);
    }

    private static void AssertEnvironmentUnchanged(JsonElement report)
    {
        static Dictionary<string, string?> EnvironmentOf(JsonElement value) => value.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString(), StringComparer.OrdinalIgnoreCase);
        var expected = EnvironmentOf(report.GetProperty("baselineEnvironment"));
        var actual = EnvironmentOf(report.GetProperty("launchEnvironment"));
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (key, value) in expected) Assert.Equal(value, actual[key]);
    }
}
