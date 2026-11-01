using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: unique response-file directory; only composes commands, never starts a process.
public sealed class AcceptanceBalancedLaneCommandLengthTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "balanced-command-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void LargeBalancedAndResidualFilters_SpillBelowTheShellLimit()
    {
        AcceptanceTestClassDescriptor[] classes = Enumerable.Range(0, 1200).Select(index =>
            new AcceptanceTestClassDescriptor($"Example.Infrastructure.AcceptanceBalancedFixture{index:D4}Tests", null)).ToArray();
        var expansion = AcceptanceBalancedLaneExpander.Expand([], classes, new Dictionary<string, double>(), new() { LaneCount = 4 });
        var largest = expansion.Layout!.Lanes.OrderByDescending(lane => lane.PredictedSeconds).First();
        foreach (var lane in expansion.Lanes.Where(lane => lane.Name == largest.Name || lane.AllowsEmptySelection))
        {
            var args = Arguments(lane);
            var spill = AcceptanceMtpFilterResponseFile.Plan(args);
            Assert.NotNull(spill.ResponseFilePath);
            Assert.True(args.Sum(argument => argument.Length + 3) > AcceptanceCmdCommandLineLimit.MaxCmdCommandLineLength);
            var start = AcceptanceShellProcessStartInfo.Build(args, _root,
                @"\\.\pipe\balanced-out", @"\\.\pipe\balanced-err", true, null, false, out _);
            if (OperatingSystem.IsWindows())
            {
                Assert.InRange(start.Arguments.Length, 1, AcceptanceCmdCommandLineLimit.MaxCmdCommandLineLength);
                Assert.Contains("@", start.Arguments);
                Assert.Equal(spill.ResponseFileText, File.ReadAllText(spill.ResponseFilePath!));
            }
            Assert.Equal(AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter(lane.Filter),
                spill.ResponseFileText!.Split('\n').Where(value => value != "--filter-not-trait" && value != "Category=HostIntegration"));
        }
    }

    [Fact]
    public void ResidualInvocation_RemovesMinimum_AndIgnoresOnlyZeroTestsExit()
    {
        var residual = new AcceptanceTestLane("residual", "FullyQualifiedName!=Tests.A") { AllowsEmptySelection = true };
        var residualArgs = Arguments(residual);
        Assert.DoesNotContain("--minimum-expected-tests", residualArgs);
        Assert.Equal("8", residualArgs[Array.IndexOf(residualArgs, "--ignore-exit-code") + 1]);
        var normal = Arguments(residual with { AllowsEmptySelection = false });
        Assert.Contains("--minimum-expected-tests", normal);
        Assert.DoesNotContain("--ignore-exit-code", normal);
    }

    private string[] Arguments(AcceptanceTestLane lane)
    {
        Directory.CreateDirectory(_root);
        const string project = AcceptancePolicyShardPlanner.InfrastructureTestsProject;
        var settings = new AcceptanceGateEngineSettings { MtpInvocations = [new()
        {
            Project = project,
            ExecutablePathTemplate = "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
            Arguments = ["{executable}", "--minimum-expected-tests", "1", "--results-directory", "{resultsDirectory}",
                "--report-trx-filename", "{trxFileName}"]
        }] };
        var check = new Check { Name = "infrastructure tests: " + lane.Name, Type = "dotnet-test", Project = project,
            IsResolvedInfrastructureLane = true, AllowsEmptySelection = lane.AllowsEmptySelection,
            Arguments = ["--filter", lane.Filter] };
        var environment = new DotnetBuildEnvironment("fixture", _root, _root, Path.Combine(_root, "lock"), [], "fixture");
        return AcceptanceCheckCommandBuilder.BuildMtpTestArguments(check, settings, environment,
            new([Path.Combine(_root, "results.trx")], []), true,
            (_, filter) => AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter(filter));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
