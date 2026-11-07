using Mcg.AgentOrchestrator.App.Cli;

// Parallel-safe: real git operates only on fixture repositories; command I/O is in memory.
public sealed class CliPushAdoptedCommandTests
{
    [Fact]
    public void CatalogHelpAndFlags_ExposePushAdopted()
    {
        Assert.Contains("push-adopted", CliArgumentParser.RecognizedCommands);
        Assert.True(CliCommandHelp.IsCommandSpecificHelp(["push-adopted", "--help"]));
        CliCommandHelp.ThrowIfInvalidFlags(["push-adopted", "abc", "--remote", "origin", "--branch", "main"]);
        Assert.Throws<ArgumentException>(() => CliCommandHelp.ThrowIfInvalidFlags(["push-adopted", "--unknown"]));
        var help = File.ReadAllText(Path.Combine(VerifiedRepositoryRoot.Find(),
            "src/Mcg.AgentOrchestrator.App/Cli/CliCommandHelp.cs"));
        Assert.Contains("  push-adopted      Scan and publish", help);
    }

    [Theory]
    [InlineData("clean", 0)]
    [InlineData("scan-match", 1)]
    [InlineData("non-fast-forward", 1)]
    [InlineData("push-failed", 1)]
    public void FixtureRemote_UsesSamePushedAndHeldOutcomes(string outcome, int expected)
    {
        using var f = new AdoptedBuildPushFixture();
        if (outcome == "non-fast-forward") f.Git("checkout", "--orphan", "other");
        var commit = f.Commit(outcome == "scan-match" ? AdoptedBuildLeakScannerTests.Token : "adopted");
        f.FailPush = outcome == "push-failed";
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(expected, CliPushAdoptedCommand.Run(
            ["push-adopted", commit, "--remote", "origin", "--branch", "main"], f.Service(), output, error));
        Assert.Equal(expected == 0 ? commit : f.Baseline, f.RemoteTip);
        Assert.Contains(expected == 0 ? "ADOPTED_BUILD_PUSHED" : "ADOPTED_BUILD_PUSH_HELD", Assert.Single(f.Events));
        if (expected != 0)
        {
            Assert.Contains("reason=" + outcome, f.Events[0]);
            Assert.Contains("push-adopted", Assert.Single(f.Attention));
        }
        else Assert.Empty(f.Attention);
        Assert.Equal("", error.ToString());
    }

    [Theory]
    [InlineData("--branch")]
    [InlineData("--force")]
    public void InvalidFlags_DoNotInvokeService(string flag)
    {
        using var f = new AdoptedBuildPushFixture();
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, CliPushAdoptedCommand.Run(["push-adopted", f.Baseline, flag], f.Service(), output, error));
        Assert.Empty(f.Calls);
        Assert.Empty(f.Events);
        Assert.Contains("Usage: push-adopted", error.ToString());
    }
}
