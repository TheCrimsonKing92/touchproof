using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every test owns its workspace and SQLite stores; console capture is scoped.
public sealed class CliBacklogUpdatePinTests : CliCommandTestBase
{
    private protected override bool UsesGit => false;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pin_and_clear_switches_persist_without_values(bool interactive)
    {
        var (workspace, store, item) = await CreateFixture();
        var kernel = new AgentOrchestratorKernel();
        var output = ExecuteCliAndCapture(Parse(interactive, item.Id, "--next"), kernel, workspace);
        var pinned = (await store.GetByExactIdAsync(item.Id))!;
        Assert.Contains($"Updated: [{item.Id}]", output);
        Assert.NotNull(pinned.PinnedAt);
        Assert.Equal(TimeSpan.Zero, pinned.PinnedAt.Value.Offset);

        ExecuteCliAndCapture(Parse(interactive, item.Id, "--next", "--NEXT"), kernel, workspace);
        Assert.Equal(pinned.PinnedAt, (await store.GetByExactIdAsync(item.Id))!.PinnedAt);
        ExecuteCliAndCapture(Parse(interactive, item.Id, "--no-next", "--NO-NEXT"), kernel, workspace);
        Assert.Null((await store.GetByExactIdAsync(item.Id))!.PinnedAt);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Pin_with_description_preserves_option_boundaries(bool interactive, bool pinFirst)
    {
        var (workspace, store, item) = await CreateFixture();
        string[] arguments = pinFirst
            ? ["--next", "--description", "alpha", "beta", "gamma", "--priority", "high"]
            : ["--description", "alpha", "beta", "gamma", "--next", "--priority", "high"];
        ExecuteCliAndCapture(Parse(interactive, item.Id, arguments), new AgentOrchestratorKernel(), workspace);

        var current = (await store.GetByExactIdAsync(item.Id))!;
        Assert.Equal("alpha beta gamma", current.Body);
        Assert.Equal("high", current.Priority);
        Assert.NotNull(current.PinnedAt);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Conflicting_switches_reject_entire_update(bool interactive, bool clearFirst)
    {
        var (workspace, store, item) = await CreateFixture();
        var arguments = clearFirst ? new[] { "--no-next", "--next" } : new[] { "--next", "--no-next" };
        var error = Assert.Throws<ArgumentException>(() => ExecuteCliAndCapture(
            Parse(interactive, item.Id, ["--title", "Replacement title", .. arguments]),
            new AgentOrchestratorKernel(), workspace));

        Assert.Contains("--next", error.Message);
        Assert.Contains("--no-next", error.Message);
        Assert.Contains("mutually exclusive", error.Message);
        Assert.Equal(JsonSerializer.Serialize(item), JsonSerializer.Serialize(await store.GetByExactIdAsync(item.Id)));
    }

    [Fact]
    public async Task List_marks_only_pinned_rows_in_text_and_json()
    {
        var (workspace, store, item) = await CreateFixture();
        var other = await store.AddAsync("Unpinned item");
        var kernel = new AgentOrchestratorKernel();
        ExecuteCliAndCapture(["backlog-update", item.Id[..8], "--next"], kernel, workspace);
        var pinnedAt = (await store.GetByExactIdAsync(item.Id))!.PinnedAt;
        Assert.NotNull(pinnedAt);

        var text = ExecuteCliAndCapture(["backlog-list"], kernel, workspace);
        Assert.Contains("[next]", Assert.Single(text.Split('\n').Where(line => line.Contains(item.Id, StringComparison.Ordinal))));
        Assert.DoesNotContain("[next]", Assert.Single(text.Split('\n').Where(line => line.Contains(other.Id, StringComparison.Ordinal))));

        using var json = JsonDocument.Parse(ExecuteCliAndCapture(["backlog-list", "--json"], kernel, workspace));
        var rows = json.RootElement.GetProperty("payload").GetProperty("items").EnumerateArray().ToArray();
        var pinned = Assert.Single(rows.Where(row => row.GetProperty("id").GetString() == item.Id));
        var unpinned = Assert.Single(rows.Where(row => row.GetProperty("id").GetString() == other.Id));
        Assert.Equal(JsonValueKind.String, pinned.GetProperty("pinnedAt").ValueKind);
        Assert.Equal(pinnedAt.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), pinned.GetProperty("pinnedAt").GetString());
        Assert.Equal(JsonValueKind.Null, unpinned.GetProperty("pinnedAt").ValueKind);
    }

    private async Task<(OrchestratorWorkspace, BacklogStore, BacklogItem)> CreateFixture()
    {
        var root = CreateTempDirectory();
        CliBacklogUpdateWorkspaceTemplate.CopyTo(root);
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var store = new BacklogStore(workspace.BacklogStorePath);
        var item = await store.AddAsync("Original title", "Original body");
        return (workspace, store, item);
    }

    private static IReadOnlyList<string> Parse(bool interactive, string id, params string[] arguments) =>
        interactive
            ? CliArgumentParser.SplitCommand($"backlog-update {id[..8]} {string.Join(' ', arguments)}")
            : CliArgumentParser.NormalizeArgs(["backlog-update", id[..8], .. arguments]);
}
