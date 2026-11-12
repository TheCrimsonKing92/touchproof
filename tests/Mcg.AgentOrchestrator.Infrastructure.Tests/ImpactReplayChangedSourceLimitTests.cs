using Mcg.AgentOrchestrator.App.Cli;

// Parallel-safe: fixture state and all CLI writers are private to each test.
public sealed class ImpactReplayChangedSourceLimitTests
{
    [Fact]
    public void SevenChangedSources_ResolveWithoutChangedFileLimit()
    {
        using var fixture = new ImpactReplayFixture(changedSourceCount: 7);
        fixture.AddAttempt("seven-sources");
        var (defaultReport, _) = fixture.Run();
        var baseline = Assert.Single(defaultReport.Attempts);
        Assert.Equal("Resolved", baseline.SelectionOutcome);
        Assert.Null(baseline.DegradationKind);
        var (overridden, _) = fixture.Run();
        var attempt = Assert.Single(overridden.Attempts);
        Assert.Equal("Resolved", attempt.SelectionOutcome);
        Assert.Equal(7, attempt.ChangedSourceFileCount);
        Assert.Contains("AlphaTests", attempt.SelectedTestClasses);
        Assert.Equal(baseline.SelectedTestClasses, attempt.SelectedTestClasses);
        var belowCount = Assert.Single(fixture.Run().Report.Attempts);
        Assert.Equal("Resolved", belowCount.SelectionOutcome);
        Assert.Equal(baseline.SelectedTestClasses, belowCount.SelectedTestClasses);
    }

    [Fact]
    public void RemovedLimit_ReturnsUsageWithoutStartingReplay()
    {
        using var fixture = new ImpactReplayObservationFixture();
        var historyLoaded = false;
        var inventoryLoaded = false;
        fixture.Source.BeforeHistory = () => historyLoaded = true;
        fixture.Source.BeforeInventory = () => inventoryLoaded = true;

        var code = CliImpactReplayCommand.Run(
            ["impact-replay", "--max-changed-source-files", "20", "--out", fixture.Destination],
            fixture.Root, fixture.Root, null, fixture.Output, fixture.Error,
            clock: fixture.Clock, source: fixture.Source);

        Assert.Equal(2, code);
        Assert.Equal(CliCommandHelp.ImpactReplayUsage + Environment.NewLine, fixture.Error.ToString());
        Assert.Empty(fixture.Output.ToString());
        Assert.Empty(fixture.Events());
        Assert.False(historyLoaded);
        Assert.False(inventoryLoaded);
        Assert.False(Directory.Exists(fixture.Destination));
        Assert.False(File.Exists(fixture.Checkpoint));
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
    public void Help_OmitsRemovedReplayLimit()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CliImpactReplayCommand.Run(["impact-replay", "--help"], "unused", "unused", null, output, error));
        Assert.DoesNotContain("max-changed-source-files", output.ToString());
        Assert.DoesNotContain("Maximum changed source files", output.ToString());
        Assert.DoesNotContain("max-changed-source-files", CliCommandHelp.ImpactReplayUsage);
        Assert.DoesNotContain("Maximum changed source files", CliCommandHelp.ImpactReplayUsage);
        Assert.Empty(error.ToString());
    }
}
