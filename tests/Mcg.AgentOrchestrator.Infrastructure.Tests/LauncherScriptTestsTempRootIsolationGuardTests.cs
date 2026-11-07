using System.Security.Cryptography;
using Mcg.AgentOrchestrator.App.Orchestration;

[Xunit.Collection("ProcessSpawning")]
public sealed class LauncherScriptTestsTempRootIsolationGuardTests
{
    private static readonly string[] ScenarioNames =
    [
        "launcher-json-test", "launcher-double-dash-test", "launcher-failure-test",
        "goal-worktree-launch-json-test"
    ];

    [Xunit.Fact]
    public void Four_launcher_scenarios_confine_artifacts_and_preserve_source_state()
    {
        var roots = new List<string> { LauncherScriptTests.FindLauncherSourceRoot() };
        var configuredRoot = Environment.GetEnvironmentVariable(OrchestratorWorkspace.RepoRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredRoot))
            roots.Add(configuredRoot);
        var realRoots = roots.Select(LauncherScriptTests.NormalizeLauncherPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var before = realRoots.Select(Snapshot).ToArray();
        var observedNames = new List<string>();
        var temporaryRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Observe(LauncherScriptTests.LauncherScenarioArtifacts artifacts)
        {
            observedNames.Add(artifacts.Name);
            Assert.True(temporaryRoots.Add(LauncherScriptTests.NormalizeLauncherPath(artifacts.RepositoryRoot)),
                "Each launcher scenario must have its own temporary repository root.");
            foreach (var path in new[] { artifacts.StdoutPath, artifacts.StderrPath, artifacts.JournalPath }
                         .Concat(Directory.EnumerateFiles(artifacts.RepositoryRoot, "*", SearchOption.AllDirectories)))
            {
                LauncherScriptTests.AssertLauncherPathWithinRoot(artifacts.RepositoryRoot, path);
                foreach (var realRoot in realRoots)
                {
                    var prefix = Path.TrimEndingDirectorySeparator(realRoot) + Path.DirectorySeparatorChar;
                    Assert.False(LauncherScriptTests.NormalizeLauncherPath(path)
                        .StartsWith(prefix, StringComparison.OrdinalIgnoreCase),
                        $"Scenario '{artifacts.Name}' wrote or reported a real-root artifact: {path}");
                }
            }
        }

        LauncherScriptTests.AssertStartOrchestratorCommandReceipt(["goals"], null, Observe);
        LauncherScriptTests.AssertStartOrchestratorCommandDoubleDash(Observe);
        LauncherScriptTests.AssertStartOrchestratorCommandLaunchFailure(Observe);
        GoalWorktreeTestsSqliteTooling.AssertStartOrchestratorCommandLaunchJson(Observe);
        Assert.Equal(ScenarioNames, observedNames);

        for (var index = 0; index < realRoots.Length; index++)
        {
            var after = Snapshot(realRoots[index]);
            var original = before[index];
            Assert.Equal(original.Logs.Keys.Order(StringComparer.OrdinalIgnoreCase),
                after.Logs.Keys.Order(StringComparer.OrdinalIgnoreCase));
            foreach (var (path, state) in original.Logs)
                Assert.Equal(state, after.Logs[path]);
            Assert.Equal(original.Journal, after.Journal);
        }
    }

    private static SourceState Snapshot(string repositoryRoot)
    {
        var stateRoot = Path.Combine(repositoryRoot, ".orchestrator");
        var logsRoot = Path.Combine(stateRoot, "logs");
        var logs = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(logsRoot))
        {
            foreach (var path in Directory.EnumerateFiles(logsRoot, "operator-*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                if (ScenarioNames.Any(scenario => name.Contains(scenario, StringComparison.OrdinalIgnoreCase)))
                    logs.Add(path, ReadFileState(path)!);
            }
        }
        return new SourceState(logs, ReadFileState(Path.Combine(stateRoot, "last-drive.json")));
    }

    private static FileState? ReadFileState(string path)
    {
        if (!File.Exists(path))
            return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new FileState(stream.Length, File.GetLastWriteTimeUtc(path), Convert.ToHexString(SHA256.HashData(stream)));
    }

    private sealed record SourceState(Dictionary<string, FileState> Logs, FileState? Journal);
    private sealed record FileState(long Length, DateTime LastWriteTimeUtc, string ContentHash);
}
