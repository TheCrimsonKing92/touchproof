[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ProcessTreeGuiSuppressionTestsGitDirectLaunchShellChildrenTests
{
    [Fact]
    public async Task RebaseExecAndPostRewriteHook_ResolveShAndProduceMarkers()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var document = await GitDirectLaunchTestHarness.Run("shell");
        var report = document.RootElement;
        Assert.Equal(GitDirectLaunchTestHarness.ExpectedRealGit(report), report.GetProperty("image").GetString(), ignoreCase: true);
        var system = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(report.GetProperty("image").GetString()!)))!.ToUpperInvariant();
        Assert.Equal(new[] { "exec:" + system, "exec:" + system },
            report.GetProperty("execMarkers").EnumerateArray().Select(line => line.GetString()).ToArray());
        Assert.Equal(new[] { "hook:2" }, report.GetProperty("hookMarkers").EnumerateArray().Select(line => line.GetString()).ToArray());
        Assert.False(report.GetProperty("rebaseDirectoryExists").GetBoolean());
    }
}
