using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each invocation owns its workspace and SQLite stores; console capture is scoped.
public sealed class CliBacklogUpdatePriorityTests : CliCommandTestBase
{
    private protected override bool UsesGit => false;

    [Theory]
    [InlineData("HIGH", "high")]
    [InlineData(" HIGH ", "high")]
    [InlineData("Critical", "critical")]
    [InlineData("normal", "normal")]
    [InlineData("Low", "low")]
    public async Task Update_stores_canonical_priority(string supplied, string expected)
    {
        var (workspace, store, item) = await CreateFixture();
        var output = ExecuteCliAndCapture(
            ["backlog-update", item.Id[..8], "--priority", supplied], new AgentOrchestratorKernel(), workspace);

        Assert.Contains($"Updated: [{item.Id}]", output);
        Assert.Equal(expected, (await store.GetByExactIdAsync(item.Id))!.Priority);
    }

    [Theory]
    [InlineData("urgent")]
    [InlineData("")]
    [InlineData(" \t ")]
    public async Task Invalid_priority_rejects_entire_update(string supplied)
    {
        var (workspace, store, item) = await CreateFixture();
        var error = Assert.Throws<ArgumentException>(() => ExecuteCliAndCapture(
            ["backlog-update", item.Id[..8], "--priority", supplied, "--title", "Replacement title",
                "--description", "Replacement body", "--tags", "replacement", "--status", "done"],
            new AgentOrchestratorKernel(), workspace));

        foreach (var level in new[] { "critical", "high", "normal", "low" })
            Assert.Contains(level, error.Message);
        Assert.Equal(JsonSerializer.Serialize(item),
            JsonSerializer.Serialize(await store.GetByExactIdAsync(item.Id)));
    }

    [Fact]
    public async Task Omitting_priority_preserves_stored_value()
    {
        var (workspace, store, item) = await CreateFixture();
        ExecuteCliAndCapture(["backlog-update", item.Id[..8], "--title", "Replacement title"],
            new AgentOrchestratorKernel(), workspace);

        var current = (await store.GetByExactIdAsync(item.Id))!;
        Assert.Equal("Replacement title", current.Title);
        Assert.Equal("Low", current.Priority);
    }

    private async Task<(OrchestratorWorkspace, BacklogStore, BacklogItem)> CreateFixture()
    {
        var root = CreateTempDirectory();
        CliBacklogUpdateWorkspaceTemplate.CopyTo(root);
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var store = new BacklogStore(workspace.BacklogStorePath);
        var item = await store.AddAsync("Original title", "Original body");
        await store.UpdateAsync(item.Id, new BacklogItemUpdate(Priority: "Low"));
        return (workspace, store, (await store.GetByExactIdAsync(item.Id))!);
    }
}
