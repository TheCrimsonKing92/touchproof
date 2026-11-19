using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: the probe owns a disposable repository, SQLite stores, and process custody.
public sealed class CompletionBuildOperatorLaunchTests
{
    [Fact]
    public async Task HeldControlLaunchesHostAndRecordsProgressBeforeRelease()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string probe = "Mcg.AgentOrchestrator.SupervisorLaunchProbe";
        string[] closureFiles = [probe + ".dll", probe + ".runtimeconfig.json", "Mcg.AgentOrchestrator.App.dll"];
        var closure = FixtureProbeLocator.FindDirectory(typeof(CompletionBuildOperatorLaunchTests), probe, closureFiles);
        Assert.True(closure is not null, string.Join(Environment.NewLine,
            FixtureProbeLocator.TriedPaths(typeof(CompletionBuildOperatorLaunchTests), probe, closureFiles)));
        var source = VerifiedRepositoryRoot.Find();
        // Use the short Windows build-storage parent so MSBuild can bootstrap its tools.
        var buildStorage = DotnetBuildStorageLayout.ResolveIsolatedRootBase(null,
            Environment.GetEnvironmentVariable("LOCALAPPDATA"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.GetTempPath(), isWindows: true);
        var root = Path.Combine(Path.GetDirectoryName(buildStorage)!, "cbe-" + Guid.NewGuid().ToString("N")[..12]);

        // Use the same entry point and arguments as Invoke-CompletionBuildOperatorControl.ps1.
        var result = await WorkerProcessRunner.RunBufferedDirectAsync(
            new("dotnet", [Path.Combine(closure, closureFiles[0]), "completion-build", root, source, "held"],
                source, Timeout: TimeSpan.FromMinutes(20)),
            TestContext.Current.CancellationToken);
        Assert.True(result.ExitCode == 0,
            $"Held control exited {result.ExitCode}. Evidence: {root}{Environment.NewLine}{result.StandardError}");

        var receipts = File.ReadLines(Path.Combine(root, "receipts.jsonl")).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            var entry = document.RootElement;
            var kind = entry.GetProperty("kind").GetString();
            var action = kind == "tick" ? entry.GetProperty("payload").GetProperty("action").GetString() : null;
            return (Kind: kind, Pid: entry.GetProperty("pid").GetInt32(), Action: action);
        }).ToArray();

        var startTick = Assert.Single(receipts.Where(r => r is ("tick", _, "start")));
        Assert.NotEqual(Assert.Single(receipts.Where(r => r.Kind == "candidate")).Pid, startTick.Pid);
        Assert.Equal(new[] { "compiler-checkpoint", "held-control-proven", "operator-release", "control-passed" },
            receipts.Select(r => r.Kind).Where(kind =>
                kind is "compiler-checkpoint" or "held-control-proven" or "operator-release" or "control-passed"));
        SharedTestSupport.RemoveTempDirectory(root);
    }
}
