using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: in-memory dictionaries only; no process environment is read or changed.
public sealed class GcEnvironmentLaunchComparisonTests
{
    [Fact]
    public void Comparison_IgnoresUnrelatedDifferences()
    {
        var expected = new Dictionary<string, string?>
        {
            ["DOTNET_GCConserveMemory"] = "3",
            ["DOTNET_GCHeapCount"] = "4",
            [DotnetBuildStorageLayout.IsolatedRootOverrideVariable] = "storage-root",
            ["PATH"] = "baseline-path",
            ["UNRELATED_REMOVED"] = "baseline-only"
        };
        var actual = new Dictionary<string, string?>
        {
            [DotnetBuildStorageLayout.IsolatedRootOverrideVariable] = "storage-root",
            ["DOTNET_GCHeapCount"] = "4",
            ["DOTNET_GCConserveMemory"] = "3",
            ["PATH"] = "changed-path",
            ["MCG_ORCHESTRATOR_PROTECTED_PID"] = "800",
            ["dotnet_gcheapcount"] = "unrelated-case",
            [DotnetBuildStorageLayout.IsolatedRootOverrideVariable.ToLowerInvariant()] = "unrelated-case",
            [DotnetBuildStorageLayout.IsolatedRootOverrideVariable + "_OTHER"] = "unrelated-prefix"
        };

        GcEnvironmentEvidenceLaunchTests.AssertControlledEntriesEqual(expected, actual);
        GcEnvironmentEvidenceLaunchTests.AssertControlledEntriesEqual(actual, expected);
    }

    [Theory]
    [InlineData("DOTNET_GCConserveMemory")]
    [InlineData("DOTNET_GCHeapCount")]
    [InlineData("DOTNET_GCgen0size")]
    [InlineData("DOTNET_GC")]
    [InlineData(DotnetBuildStorageLayout.IsolatedRootOverrideVariable)]
    public void Comparison_RejectsControlledValueDifference(string key)
    {
        var expected = new Dictionary<string, string?> { [key] = "baseline" };
        var actual = new Dictionary<string, string?> { [key] = "changed" };

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            GcEnvironmentEvidenceLaunchTests.AssertControlledEntriesEqual(expected, actual));
    }

    [Theory]
    [InlineData("DOTNET_GCConserveMemory", "3")]
    [InlineData("DOTNET_GCConserveMemory", null)]
    [InlineData("DOTNET_GCHeapCount", "4")]
    [InlineData("DOTNET_GCHeapCount", null)]
    [InlineData("DOTNET_GCgen0size", "100")]
    [InlineData("DOTNET_GCgen0size", null)]
    [InlineData("DOTNET_GC", "value")]
    [InlineData("DOTNET_GC", null)]
    [InlineData(DotnetBuildStorageLayout.IsolatedRootOverrideVariable, "storage-root")]
    [InlineData(DotnetBuildStorageLayout.IsolatedRootOverrideVariable, null)]
    public void Comparison_RejectsControlledAdditionOrRemoval(string key, string? value)
    {
        var missing = new Dictionary<string, string?>();
        var present = new Dictionary<string, string?> { [key] = value };

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            GcEnvironmentEvidenceLaunchTests.AssertControlledEntriesEqual(missing, present));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            GcEnvironmentEvidenceLaunchTests.AssertControlledEntriesEqual(present, missing));
    }
}
