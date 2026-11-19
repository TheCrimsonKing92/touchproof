using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: the probe owns a disposable repository, SQLite stores, and process custody.
public sealed class CompletionBuildOperatorLaunchTests
{
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

    private static async Task<((string? Kind, int Pid, string? Action)[] Receipts, string Root)> RunControl(
        string control, int minimumRootLength = 0)
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
        // Reproduce the evidence depth at which the old profile layout failed before compilation.
        var evidence = root.Length < minimumRootLength
            ? Path.Combine(root, new string('x', minimumRootLength - root.Length))
            : root;

        // Use the same entry point and arguments as Invoke-CompletionBuildOperatorControl.ps1.
        var result = await WorkerProcessRunner.RunBufferedDirectAsync(
            new("dotnet", [Path.Combine(closure, closureFiles[0]), "completion-build", evidence, source, control],
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
