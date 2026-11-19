using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: the probe owns a disposable repository, SQLite stores, and process custody.
public sealed class CompletionBuildOperatorLaunchTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact]
    public async Task HeldControlLaunchesHostAndRecordsProgressBeforeRelease()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (receipts, root) = await RunControl("held");
        var startTick = Assert.Single(receipts.Where(r => r is ("tick", _, "start")));
        Assert.NotEqual(Assert.Single(receipts.Where(r => r.Kind == "candidate")).Pid, startTick.Pid);
        Assert.Equal(new[] { "compiler-checkpoint", "held-control-proven", "operator-release", "control-passed" },
            receipts.Select(r => r.Kind).Where(kind =>
                kind is "compiler-checkpoint" or "held-control-proven" or "operator-release" or "control-passed"));
        SharedTestSupport.RemoveTempDirectory(root);
    }

    [Fact]
    public async Task StaleCandidateControlCompilesFromDeepEvidenceRoot()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (receipts, root) = await RunControl("stale-candidate", minimumRootLength: 136);
        Assert.Equal(new[] { "compiler-checkpoint", "real-compiler-result", "control-passed" },
            receipts.Select(r => r.Kind).Where(kind =>
                kind is "compiler-checkpoint" or "real-compiler-result" or "control-passed"));
        SharedTestSupport.RemoveTempDirectory(root);
    }

    [Fact(Skip = "The completion-build operator procedure requires Windows.", SkipUnless = nameof(IsWindows))]
    public async Task HeldControlScriptRunsToFirstReceiptAndPasses()
    {
        var (probeDll, source, root) = CreateControlPaths();
        var evidence = Path.Combine(root, "evidence");

        var result = await RunHeldScript(probeDll, source, evidence);

        Assert.False(result.TimedOut, $"The held control script did not exit. Evidence: {evidence}");
        Assert.True(result.ExitCode == 0,
            $"The held control script exited {result.ExitCode}. Evidence: {evidence}\n{result.StandardOutput}\n{result.StandardError}");
        Assert.Contains($"PASS operator controls=1 evidence={evidence}",
            result.StandardOutput.Split('\n').Select(line => line.TrimEnd('\r')));

        var receipt = Path.Combine(evidence, "held", "receipts.jsonl");
        Assert.True(File.Exists(receipt), $"Missing held control receipt: {receipt}");
        Assert.Single(File.ReadLines(receipt).Where(line =>
        {
            using var record = JsonDocument.Parse(line);
            return record.RootElement.GetProperty("kind").GetString() == "control-passed"
                && record.RootElement.GetProperty("payload").GetProperty("control").GetString() == "held";
        }));
        SharedTestSupport.RemoveTempDirectory(root);
    }

    [Fact(Skip = "The completion-build operator procedure requires Windows.", SkipUnless = nameof(IsWindows))]
    public async Task ScriptRejectsExistingOutputRootWithNonZeroExit()
    {
        var (probeDll, source, root) = CreateControlPaths();
        var evidence = Directory.CreateDirectory(Path.Combine(root, "evidence")).FullName;

        var result = await RunHeldScript(probeDll, source, evidence);

        Assert.False(result.TimedOut, $"The held control script did not reject the existing evidence directory: {evidence}");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("OutputRoot must be a new directory", result.StandardError);
        Assert.DoesNotContain("PASS operator controls=", result.StandardOutput);
        Assert.False(Directory.Exists(Path.Combine(evidence, "held")));
        SharedTestSupport.RemoveTempDirectory(root);
    }

    private static Task<WorkerProcessRunResult> RunHeldScript(string probeDll, string source, string evidence)
    {
        var powerShell = TestPowerShell.Executable;
        Assert.True(Path.IsPathFullyQualified(powerShell) && File.Exists(powerShell) &&
            string.Equals(Path.GetFileName(powerShell), "pwsh.exe", StringComparison.OrdinalIgnoreCase),
            $"PowerShell 7 is required for the operator procedure. Resolved shell: {powerShell}");
        var script = Path.Combine(source, "scripts", "Invoke-CompletionBuildOperatorControl.ps1");
        return WorkerProcessRunner.RunBufferedDirectAsync(
            new(powerShell,
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                    "-ProbeDll", probeDll, "-SourceRepository", source, "-OutputRoot", evidence, "-Control", "held"],
                source, Timeout: TimeSpan.FromMinutes(20)),
            TestContext.Current.CancellationToken);
    }

    private static (string ProbeDll, string Source, string Root) CreateControlPaths()
    {
        const string probe = "Mcg.AgentOrchestrator.SupervisorLaunchProbe";
        string[] closureFiles = [probe + ".dll", probe + ".runtimeconfig.json", "Mcg.AgentOrchestrator.App.dll"];
        var closure = FixtureProbeLocator.FindDirectory(typeof(CompletionBuildOperatorLaunchTests), probe, closureFiles);
        Assert.True(closure is not null, string.Join(Environment.NewLine,
            FixtureProbeLocator.TriedPaths(typeof(CompletionBuildOperatorLaunchTests), probe, closureFiles)));
        var source = VerifiedRepositoryRoot.Find();
        var buildStorage = DotnetBuildStorageLayout.ResolveIsolatedRootBase(null,
            Environment.GetEnvironmentVariable("LOCALAPPDATA"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.GetTempPath(), isWindows: true);
        var root = Path.Combine(Path.GetDirectoryName(buildStorage)!, "cbe-" + Guid.NewGuid().ToString("N")[..12]);
        return (Path.Combine(closure, closureFiles[0]), source, root);
    }

    private static async Task<((string? Kind, int Pid, string? Action)[] Receipts, string Root)> RunControl(
        string control, int minimumRootLength = 0)
    {
        var (probeDll, source, root) = CreateControlPaths();
        // Reproduce the evidence depth at which the old profile layout failed before compilation.
        var evidence = root.Length < minimumRootLength
            ? Path.Combine(root, new string('x', minimumRootLength - root.Length))
            : root;

        // Use the same entry point and arguments as Invoke-CompletionBuildOperatorControl.ps1.
        var result = await WorkerProcessRunner.RunBufferedDirectAsync(
            new("dotnet", [probeDll, "completion-build", evidence, source, control],
                source, Timeout: TimeSpan.FromMinutes(20)),
            TestContext.Current.CancellationToken);
        Assert.True(result.ExitCode == 0,
            $"Control '{control}' exited {result.ExitCode}. Evidence: {evidence}{Environment.NewLine}{result.StandardError}");

        var receipts = File.ReadLines(Path.Combine(evidence, "receipts.jsonl")).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            var entry = document.RootElement;
            var kind = entry.GetProperty("kind").GetString();
            var action = kind == "tick" ? entry.GetProperty("payload").GetProperty("action").GetString() : null;
            return (Kind: kind, Pid: entry.GetProperty("pid").GetInt32(), Action: action);
        }).ToArray();

        return (receipts, root);
    }
}
