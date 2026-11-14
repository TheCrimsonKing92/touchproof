using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: result construction and unsupported-tool rejection never start a process.
public sealed class UnitCommandMeasurementResultTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(37)]
    [InlineData(8192)]
    [InlineData(10000)]
    public void Failed_OutputTail_KeepsBoundedFinalPortion(int length)
    {
        var output = string.Concat(Enumerable.Range(0, length).Select(index => (char)('a' + index % 26)));

        var result = UnitCommandMeasurementResult.Failed("command failed",
            UnitCommandMeasurementFailureCause.ExitedNonZero, output);

        Assert.Null(result.Seconds);
        Assert.Equal("command failed", result.Failure);
        Assert.Equal(UnitCommandMeasurementFailureCause.ExitedNonZero, result.Cause);
        Assert.Equal(output[^Math.Min(length, 8192)..], result.OutputTail);
        Assert.Equal(Math.Min(length, 8192), result.OutputTail!.Length);
    }

    [Fact]
    public void Failed_TruncationSplitsSurrogate_DropsOrphanedLowSurrogate()
    {
        var output = "prefix\U0001F600" + new string('x', 8191);

        var result = UnitCommandMeasurementResult.Failed("failed",
            UnitCommandMeasurementFailureCause.Other, output);

        Assert.Equal(new string('x', 8191), result.OutputTail);
    }

    [Fact]
    public void Failed_OmittedOutput_HasEmptyTail()
    {
        var result = UnitCommandMeasurementResult.Failed("failed",
            UnitCommandMeasurementFailureCause.CommandDidNotStart);

        Assert.Equal(UnitCommandMeasurementFailureCause.CommandDidNotStart, result.Cause);
        Assert.Equal(string.Empty, result.OutputTail);
    }

    [Fact]
    public void Succeeded_ObservedDuration_HasNoFailureMetadata()
    {
        var result = UnitCommandMeasurementResult.Succeeded(3.25);

        Assert.Equal(3.25, result.Seconds);
        Assert.Null(result.Failure);
        Assert.Null(result.Cause);
        Assert.Null(result.OutputTail);
    }

    [Fact]
    public void Failed_UndefinedCause_RejectsUnclassifiedFailure()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UnitCommandMeasurementResult.Failed("failed",
            (UnitCommandMeasurementFailureCause)int.MaxValue));
    }

    [Fact]
    public void Measure_UnsupportedTool_ClassifiesFailureWithoutOutput()
    {
        var result = new ProcessUnitCommandMeasurer().Measure("unused-root", "unit",
            UnitCommandKinds.Test, "unsupported test");

        Assert.Null(result.Seconds);
        Assert.Equal("Only learned dotnet or go commands can be measured.", result.Failure);
        Assert.Equal(UnitCommandMeasurementFailureCause.NotMeasurableTool, result.Cause);
        Assert.Equal(string.Empty, result.OutputTail);
    }
}
