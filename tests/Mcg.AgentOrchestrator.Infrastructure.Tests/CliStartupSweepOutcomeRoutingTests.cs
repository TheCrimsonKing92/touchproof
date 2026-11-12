using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private roots, injected diagnostics and metadata; no global console changes.
public sealed class CliStartupSweepOutcomeRoutingTests
{
    [Xunit.Fact]
    public void Sweep_CliStartup_WritesOnlyDiagnosticFile()
    {
        using var fixture = new OrphanMetadataSweepFixture();
        using var diagnostics = new StringWriter();
        var workspace = Workspace(fixture.Root);
        var host = OrphanSweepHostWiring.Create(workspace, GoalWorktreeCleanupOptions.Default, false,
            diagnostics, () => [], new OrphanWorktreeSweepOutcomeLedger());
        var hooks = fixture.Hooks with
        {
            ReadStoredGoalMetadata = host.ReadStoredGoalMetadata,
            OrphanSweepOutcomeSink = host.OrphanSweepOutcomeSink
        };
        Assert.False(Directory.Exists(workspace.LogDirectory));

        OrphanWorktreeSweep.Run(fixture.Root, null, hooks,
            _ => throw new InvalidOperationException("No removal expected."), "cli-startup:attention");

        Assert.Equal(string.Empty, diagnostics.ToString());
        var line = Assert.Single(File.ReadAllLines(Path.Combine(workspace.LogDirectory, CliStartupSweepOutcomeLog.FileName)));
        Assert.StartsWith("orphan-sweep-outcome ", line);
        using var record = JsonDocument.Parse(line["orphan-sweep-outcome ".Length..]);
        Assert.Equal("goal-state-unavailable", record.RootElement.GetProperty("Reason").GetString());
        Assert.Equal("cli-startup:attention", record.RootElement.GetProperty("CallerRole").GetString());
        Assert.True(Directory.Exists(fixture.Folder));
    }

    [Xunit.Theory]
    [Xunit.InlineData(true, "cli-startup:conduct")]
    [Xunit.InlineData(true, "conductor-tick")]
    [Xunit.InlineData(false, "conductor-tick")]
    public void Write_ConductorOutcome_RemainsOnDiagnosticStream(bool conduct, string role)
    {
        using var fixture = new OrphanMetadataSweepFixture();
        using var diagnostics = new StringWriter();
        var workspace = Workspace(fixture.Root);
        var hooks = OrphanSweepHostWiring.Create(workspace, GoalWorktreeCleanupOptions.Default, conduct,
            diagnostics, () => [], new OrphanWorktreeSweepOutcomeLedger());
        var outcome = new OrphanWorktreeSweepOutcome(fixture.Folder, null, "removed", 1, null, role);

        hooks.OrphanSweepOutcomeSink(outcome);

        Assert.Equal("orphan-sweep-outcome " + JsonSerializer.Serialize(outcome) + diagnostics.NewLine, diagnostics.ToString());
        Assert.False(File.Exists(Path.Combine(workspace.LogDirectory, CliStartupSweepOutcomeLog.FileName)));
    }

    [Xunit.Fact]
    public void Create_SeparateHostHooks_ShareProcessSuppression()
    {
        using var fixture = new OrphanMetadataSweepFixture();
        using var diagnostics = new StringWriter();
        var workspace = Workspace(fixture.Root);
        var outcome = new OrphanWorktreeSweepOutcome(fixture.Folder, null, "goal-state-unavailable", 1, null, "cli-startup:a");
        OrphanSweepHostWiring.Create(workspace, GoalWorktreeCleanupOptions.Default, false, diagnostics, () => [])
            .OrphanSweepOutcomeSink(outcome);
        OrphanSweepHostWiring.Create(workspace, GoalWorktreeCleanupOptions.Default, false, diagnostics, () => [])
            .OrphanSweepOutcomeSink(outcome with { CallerRole = "cli-startup:b" });

        Assert.Single(File.ReadAllLines(Path.Combine(workspace.LogDirectory, CliStartupSweepOutcomeLog.FileName)));
        Assert.Equal(string.Empty, diagnostics.ToString());
    }

    [Xunit.Fact]
    public void Write_UnwritableLog_ContainsFailureWithoutConsoleFallback()
    {
        using var fixture = new OrphanMetadataSweepFixture();
        using var diagnostics = new StringWriter();
        var blocked = Path.Combine(fixture.Root, "blocked");
        File.WriteAllText(blocked, "file occupying directory");
        var workspace = Workspace(fixture.Root) with { LogDirectory = blocked };
        var hooks = OrphanSweepHostWiring.Create(workspace, GoalWorktreeCleanupOptions.Default, false,
            diagnostics, () => [], new OrphanWorktreeSweepOutcomeLedger());

        hooks.OrphanSweepOutcomeSink(new(fixture.Folder, null, "removed", 1, null, "cli-startup:a"));

        Assert.Equal(string.Empty, diagnostics.ToString());
        Assert.Equal("file occupying directory", File.ReadAllText(blocked));
    }

    [Xunit.Fact]
    public void Append_FullLogs_RotatesAndRetainsEightGenerations()
    {
        using var fixture = new OrphanMetadataSweepFixture();
        var logDirectory = Workspace(fixture.Root).LogDirectory;
        Directory.CreateDirectory(logDirectory);
        var path = Path.Combine(logDirectory, CliStartupSweepOutcomeLog.FileName);
        var outcome = new OrphanWorktreeSweepOutcome(fixture.Folder, null, "removed", 1, null, "cli-startup:a");
        for (var n = 0; n < 10; n++)
        {
            File.WriteAllText(path, new string('x', 1_048_576));
            CliStartupSweepOutcomeLog.Append(logDirectory, outcome);
        }

        var expectedFiles = Enumerable.Range(1, 8).Select(n => path + "." + n).Append(path);
        Assert.Equal(expectedFiles.Order(StringComparer.Ordinal),
            Directory.GetFiles(logDirectory).Order(StringComparer.Ordinal));
        Assert.Single(File.ReadAllLines(path));
        Assert.StartsWith("orphan-sweep-outcome ", File.ReadAllText(path));
    }

    [Xunit.Fact]
    public void Program_CleanupContext_UsesHostWiring()
    {
        var source = File.ReadAllText(Path.Combine(VerifiedRepositoryRoot.Find(), "src", "Mcg.AgentOrchestrator.App", "Program.cs"));
        Assert.Contains("new WorktreeCleanupContext(OrphanSweepHostWiring.Create(", source);
    }

    private static OrchestratorWorkspace Workspace(string root) => new(
        root, root, Path.Combine(root, ".orchestrator"), "test", false, "default", false,
        Path.Combine(root, "state.db"), Path.Combine(root, "agents.json"), Path.Combine(root, "profiles.json"),
        Path.Combine(root, "prompts"), Path.Combine(root, "logs"), Path.Combine(root, "continuation.json"),
        Path.Combine(root, "transcript.log"));
}
