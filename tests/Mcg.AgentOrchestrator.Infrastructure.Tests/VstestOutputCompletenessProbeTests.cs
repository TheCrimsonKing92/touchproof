using Mcg.AgentOrchestrator.Infrastructure;

public sealed class VstestOutputCompletenessProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vstest-output", Guid.NewGuid().ToString("N"));
    private const string AssemblyName = "Example.Tests";

    [Theory]
    [InlineData(null)]
    [InlineData("Example.Tests.runtimeconfig.json")]
    [InlineData("testhost.dll")]
    public void Probe_RequiredFileRemoved_ReturnsTypedFailure(string? removed)
    {
        var output = VstestCompleteOutputFixture.Create(_root, AssemblyName, "debug");
        Assert.True(VstestOutputCompletenessProbe.Probe(output, AssemblyName).IsComplete);
        if (removed is not null) File.Delete(Path.Combine(output, removed));

        var outcome = VstestOutputCompletenessProbe.Probe(output, AssemblyName);

        Assert.Equal(removed is null, outcome.IsComplete);
        Assert.Equal(output, outcome.OutputDirectory);
        Assert.Equal(removed is null ? [] : new[] { removed }, outcome.MissingFiles);
        if (removed is null)
        {
            Assert.Null(outcome.Failure);
            Assert.Null(outcome.Message);
        }
        else
        {
            Assert.Equal(AcceptanceShardCompletionPredicates.IncompleteVstestOutput, outcome.Failure!.FailedPredicate);
            Assert.Contains(output, outcome.Message);
            Assert.Contains(removed, outcome.Message);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Probe_NoRequiredFiles_ReportsBothInOrder(bool directoryExists)
    {
        if (directoryExists) Directory.CreateDirectory(_root);
        var outcome = VstestOutputCompletenessProbe.Probe(_root, AssemblyName);
        Assert.False(outcome.IsComplete);
        Assert.Equal(new[] { AssemblyName + ".runtimeconfig.json", "testhost.dll" }, outcome.MissingFiles);
        Assert.Equal(AcceptanceShardCompletionPredicates.IncompleteVstestOutput, outcome.Failure!.FailedPredicate);
    }

    [Theory]
    [InlineData("debug")]
    [InlineData("debug_net10.0-windows")]
    public void ResolveOutputDirectories_AssemblyPivot_SelectsActualDirectory(string pivot)
    {
        var output = VstestCompleteOutputFixture.Create(_root, AssemblyName, pivot);
        Assert.Equal(new[] { output }, VstestOutputCompletenessProbe.ResolveOutputDirectories(
            _root, AssemblyName + ".csproj", []));
    }

    [Theory]
    [InlineData("--configuration", "Release", "release_net10.0-windows")]
    [InlineData("-c", "Release", "release_net10.0-windows")]
    [InlineData("--framework", "net10.0-windows", "debug_net10.0-windows")]
    [InlineData("-f", "net10.0-windows", "debug_net10.0-windows")]
    public void ResolveOutputDirectories_ExplicitSelection_IgnoresOtherPivots(
        string option, string value, string expectedPivot)
    {
        foreach (var pivot in new[] { "debug_net8.0-windows", "debug_net10.0-windows", "release_net10.0-windows" })
            VstestCompleteOutputFixture.Create(_root, AssemblyName, pivot);
        string[] arguments = option is "-f" or "--framework"
            ? ["-c", "Debug", option, value] : [option, value];
        Assert.Equal(new[] { Path.Combine(_root, "bin", AssemblyName, expectedPivot) },
            VstestOutputCompletenessProbe.ResolveOutputDirectories(_root, AssemblyName + ".csproj", arguments));
    }

    [Fact]
    public void ProbeProject_MultipleFrameworks_ReportsEveryIncompleteOutput()
    {
        var first = VstestCompleteOutputFixture.Create(_root, AssemblyName, "debug_net8.0-windows");
        var second = VstestCompleteOutputFixture.Create(_root, AssemblyName, "debug_net10.0-windows");
        File.Delete(Path.Combine(first, "testhost.dll"));
        File.Delete(Path.Combine(second, AssemblyName + ".runtimeconfig.json"));
        var failure = VstestOutputCompletenessProbe.ProbeProject(new() { Name = "tests" },
            AssemblyName + ".csproj", [], _root);
        Assert.NotNull(failure);
        Assert.Equal(AcceptanceShardCompletionPredicates.IncompleteVstestOutput, failure.CompletionDecision!.FailedPredicate);
        Assert.Contains(first, failure.OutputTail);
        Assert.Contains(second, failure.OutputTail);
        Assert.Contains("testhost.dll", failure.OutputTail);
        Assert.Contains(AssemblyName + ".runtimeconfig.json", failure.OutputTail);
    }

    [Fact]
    public void ProbeProject_NoSelectedAssembly_ReportsProjectRootAndBothFiles()
    {
        var output = VstestCompleteOutputFixture.Create(_root, AssemblyName, "debug");
        File.Delete(Path.Combine(output, AssemblyName + ".dll"));
        var failure = VstestOutputCompletenessProbe.ProbeProject(new() { Name = "tests" },
            AssemblyName + ".csproj", [], _root);
        Assert.NotNull(failure);
        Assert.Contains(Path.Combine(_root, "bin", AssemblyName), failure.OutputTail);
        Assert.Contains(AssemblyName + ".runtimeconfig.json, testhost.dll", failure.OutputTail);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
