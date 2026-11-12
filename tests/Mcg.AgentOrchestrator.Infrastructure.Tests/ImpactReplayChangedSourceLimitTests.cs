using Mcg.AgentOrchestrator.App.Cli;

// Parallel-safe: fixture state and all CLI writers are private to each test.
public sealed class ImpactReplayChangedSourceLimitTests
{
    [Fact]
    public void SevenChangedSources_CompatibilityOptionDoesNotChangeSelection()
    {
        using var fixture = new ImpactReplayFixture(changedSourceCount: 7);
        fixture.AddAttempt("seven-sources");
        var (defaultReport, _) = fixture.Run();
        var baseline = Assert.Single(defaultReport.Attempts);
        Assert.Equal("Resolved", baseline.SelectionOutcome);
        Assert.Null(baseline.DegradationKind);
        var (overridden, _) = fixture.Run(extraArguments: ["--max-changed-source-files", "20"]);
        var attempt = Assert.Single(overridden.Attempts);
        Assert.Equal("Resolved", attempt.SelectionOutcome);
        Assert.Equal(7, attempt.ChangedSourceFileCount);
        Assert.Equal(20, overridden.Measurement!.MaximumChangedSourceFiles);
        Assert.Contains("AlphaTests", attempt.SelectedTestClasses);
        Assert.Equal(baseline.SelectedTestClasses, attempt.SelectedTestClasses);
        var belowCount = Assert.Single(fixture.Run(extraArguments: ["--max-changed-source-files", "1"]).Report.Attempts);
        Assert.Equal("Resolved", belowCount.SelectionOutcome);
        Assert.Equal(baseline.SelectedTestClasses, belowCount.SelectedTestClasses);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("wrong")]
    [InlineData("2147483648")]
    [InlineData("+20")]
    public void InvalidLimit_ReturnsArgumentError(string value)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = CliImpactReplayCommand.Run(["impact-replay", "--max-changed-source-files", value, "--out", "unused"],
            "unused", "unused", null, output, error);
        Assert.Equal(2, code);
        Assert.Contains(CliCommandHelp.ImpactReplayUsage, error.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrRepeatedLimit_ReturnsArgumentError(bool repeated)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        string[] args = repeated ? ["impact-replay", "--max-changed-source-files", "20", "--max-changed-source-files", "20"]
            : ["impact-replay", "--max-changed-source-files"];
        Assert.Equal(2, CliImpactReplayCommand.Run(args, "unused", "unused", null, output, error));
    }

    [Fact]
    public void Help_AdvertisesReplayLimit()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CliImpactReplayCommand.Run(["impact-replay", "--help"], "unused", "unused", null, output, error));
        Assert.Contains("--max-changed-source-files <n>", output.ToString());
        Assert.Empty(error.ToString());
    }
}
