using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.Input;
using Harness = OwnerConsoleExecutorsSettingsTestContext;

// Parallel-safe: isolated workspace, files and SQLite inbox; no Terminal.Gui application initialization.
public sealed class OwnerConsoleExecutorsSettingsTabTests
{
    private const string Configuration = """
        {"executors":[
          {"id":"remote","enabled":true,"slots":3,"transport":"ssh","runnerAlias":"runner",
           "adminAlias":"admin","remoteRepository":"C:/repo","pollSeconds":12,"leaseSeconds":90,"occupancyProbe":true},
          {"id":"local","enabled":false,"slots":2,"leaseSeconds":75}
        ],"lanes":[]}
        """;

    [Fact]
    public void Lines_ConfiguredExecutors_PairRowsWithLatestControllerDecisions()
    {
        using var fixture = new Harness();
        fixture.Write(Configuration);
        WriteDecision(fixture, "remote", 4, false, "old");
        WriteDecision(fixture, "remote", 5, true, "pinned");
        WriteDecision(fixture, "local", 1, false, "deferred-busy");
        var tab = Tab(fixture);

        Assert.Equal("Executors", tab.Title);
        Assert.Equal(new[]
        {
            "remote: enabled slots=3 transport=ssh pollSeconds=12 leaseSeconds=90 occupancyProbe=true",
            "  controller: proposed slots=5 pinned application=pinned",
            "local: disabled slots=2 transport=local pollSeconds=10 leaseSeconds=75 occupancyProbe=false",
            "  controller: proposed slots=1 not pinned application=deferred-busy"
        }, tab.Lines);
        Assert.Equal(0, tab.SelectedLine);
        Assert.False(tab.Editing);
        Assert.Contains("Space", tab.KeyHint);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("whitespace")]
    [InlineData("invalid-json")]
    [InlineData("invalid-entry")]
    [InlineData("zero-executors")]
    [InlineData("unreadable")]
    public async Task Lines_UnavailableStore_ShowsOneLineAndHasNoSelectableRows(string state)
    {
        using var fixture = new Harness();
        switch (state)
        {
            case "missing": File.Delete(fixture.Path); break;
            case "empty": fixture.Write(""); break;
            case "whitespace": fixture.Write(" \r\n\t "); break;
            case "invalid-json": fixture.Write("{"); break;
            case "invalid-entry": fixture.Write("""{"executors":[{"id":"ok"},{"id":"bad","slots":0}],"lanes":[]}"""); break;
            case "zero-executors": fixture.Write("""{"executors":[],"lanes":[]}"""); break;
        }
        using var locked = state == "unreadable"
            ? new FileStream(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        var tab = Tab(fixture);

        Assert.StartsWith("Executors unavailable:", Assert.Single(tab.Lines));
        Assert.Equal(-1, tab.SelectedLine);
        tab.HandleKey(Key.CursorDown);
        tab.HandleKey(Key.CursorUp);
        tab.HandleKey(new Key(OwnerConsoleBindings.SettingsToggleKey));
        Assert.Contains("nothing queued", tab.StatusText);
        Assert.Equal(-1, tab.SelectedLine);
        Assert.Null(await fixture.Intents.ClaimNextPendingAsync(OperatorIntentScopes.Workspace, "test"));
        if (state == "missing") Assert.False(File.Exists(fixture.Path));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("other-executor")]
    [InlineData("invalid-json")]
    [InlineData("invalid-record")]
    [InlineData("unreadable")]
    public void Lines_UnavailableDecision_RendersNoDecisionYet(string state)
    {
        using var fixture = new Harness();
        fixture.Write(Configuration);
        if (state == "other-executor") WriteDecision(fixture, "other", 2, false, "no-change");
        else if (state == "invalid-json") File.WriteAllText(Ledger(fixture), "{\n");
        else if (state == "invalid-record") File.WriteAllText(Ledger(fixture), "{}\n");
        else if (state == "unreadable") WriteDecision(fixture, "remote", 2, false, "no-change");
        using var locked = state == "unreadable"
            ? new FileStream(Ledger(fixture), FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;

        var lines = Tab(fixture).Lines;
        Assert.Equal(4, lines.Count);
        Assert.Equal("  controller: no decision yet", lines[1]);
        Assert.Equal("  controller: no decision yet", lines[3]);
    }

    [Theory]
    [InlineData(false, "remote", false)]
    [InlineData(true, "local", true)]
    public async Task Toggle_HighlightedRow_QueuesOneIntentWithoutOptimisticChange(
        bool moveDown, string id, bool target)
    {
        using var fixture = new Harness();
        fixture.Write(Configuration);
        var tab = Tab(fixture);
        if (moveDown) tab.HandleKey(Key.CursorDown);
        var before = tab.Lines.ToArray();
        var original = fixture.FileBytes();

        tab.HandleKey(new Key(OwnerConsoleBindings.SettingsToggleKey));

        var intent = await fixture.Intents.ClaimNextPendingAsync(OperatorIntentScopes.Workspace, "test");
        Assert.NotNull(intent);
        Assert.Equal(OperatorIntentVerbs.ExecutorSetEnabled, intent.Verb);
        Assert.Equal(new ExecutorSetEnabledOperatorIntentPayload(id, target),
            JsonSerializer.Deserialize<ExecutorSetEnabledOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options));
        Assert.Equal("operator", intent.Actor);
        Assert.Equal("owner-console", intent.Channel);
        Assert.Equal(OperatorActorKind.Human, intent.ActorKind);
        Assert.Null(await fixture.Intents.ClaimNextPendingAsync(OperatorIntentScopes.Workspace, "test"));
        Assert.Equal(original, fixture.FileBytes());
        Assert.Equal(before, tab.Lines);
        Assert.Contains("Queued", tab.StatusText);
        Assert.Contains(id, tab.StatusText);
        Assert.Contains(target ? "enabled" : "disabled", tab.StatusText);
    }

    [Fact]
    public async Task Toggle_StaleRow_UsesFreshEnabledStateAndQueuesEachPress()
    {
        using var fixture = new Harness();
        fixture.Write(Configuration);
        var tab = Tab(fixture);
        fixture.Write(Configuration.Replace("\"enabled\":true", "\"enabled\":false"));
        var original = fixture.FileBytes();
        tab.HandleKey(new Key(OwnerConsoleBindings.SettingsToggleKey));
        tab.HandleKey(new Key(OwnerConsoleBindings.SettingsToggleKey));

        for (var index = 0; index < 2; index++)
        {
            var intent = await fixture.Intents.ClaimNextPendingAsync(OperatorIntentScopes.Workspace, "test");
            Assert.NotNull(intent);
            Assert.Equal(new ExecutorSetEnabledOperatorIntentPayload("remote", true),
                JsonSerializer.Deserialize<ExecutorSetEnabledOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options));
        }
        Assert.Null(await fixture.Intents.ClaimNextPendingAsync(OperatorIntentScopes.Workspace, "test"));
        Assert.Equal(original, fixture.FileBytes());
        Assert.StartsWith("remote: disabled", tab.Lines[0]);
        fixture.Write(Configuration);
        Assert.StartsWith("remote: enabled", tab.Lines[0]);
    }

    [Fact]
    public async Task Toggle_RemovedHighlightedId_RefusesWithoutTogglingReplacement()
    {
        using var fixture = new Harness();
        fixture.Write(Configuration);
        var tab = Tab(fixture);
        fixture.Write("""{"executors":[{"id":"replacement"}],"lanes":[]}""");
        var original = fixture.FileBytes();
        tab.HandleKey(new Key(OwnerConsoleBindings.SettingsToggleKey));

        Assert.Contains("remote", tab.StatusText);
        Assert.Contains("not found", tab.StatusText);
        Assert.DoesNotContain("Queued", tab.StatusText);
        Assert.Null(await fixture.Intents.ClaimNextPendingAsync(OperatorIntentScopes.Workspace, "test"));
        Assert.Equal(original, fixture.FileBytes());
    }

    [Fact]
    public async Task OtherKeys_ReadOnlySettings_DoNotQueueOrWrite()
    {
        using var fixture = new Harness();
        fixture.Write(Configuration);
        var original = fixture.FileBytes();
        var tab = Tab(fixture);
        for (var character = '!'; character <= '~'; character++) tab.HandleKey(new Key(character));
        foreach (var key in new[] { Key.Enter, Key.Backspace, Key.CursorUp, Key.CursorDown,
            new Key(OwnerConsoleBindings.SettingsToggleKey).WithCtrl,
            new Key(OwnerConsoleBindings.SettingsToggleKey).WithAlt }) tab.HandleKey(key);

        Assert.False(tab.Editing);
        Assert.Null(await fixture.Intents.ClaimNextPendingAsync(OperatorIntentScopes.Workspace, "test"));
        Assert.Equal(original, fixture.FileBytes());
    }

    [Fact]
    public void Toggle_UnusableInbox_ShowsFailureWithoutClaimingSuccess()
    {
        using var fixture = new Harness();
        var workspace = OrchestratorWorkspace.ForDirectory(Path.Combine(fixture.Workspace.RootDirectory, "broken-inbox"));
        var store = RemoteLaneExecutorConfiguration.ResolveStorePath(workspace.ExecutionDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);
        File.WriteAllText(store, Configuration);
        var original = File.ReadAllBytes(store);
        // A directory at the database path forces a real enqueue failure without timing or shared resources.
        var database = Path.Combine(workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName);
        Directory.CreateDirectory(database);
        Assert.True(Directory.Exists(database));
        var tab = new OwnerConsoleExecutorsSettingsTab(workspace, new(workspace));

        tab.HandleKey(new Key(OwnerConsoleBindings.SettingsToggleKey));

        Assert.StartsWith("Executor toggle refused:", tab.StatusText);
        Assert.DoesNotContain("Queued", tab.StatusText);
        Assert.StartsWith("remote: enabled", tab.Lines[0]);
        Assert.Equal(original, File.ReadAllBytes(store));
        Assert.True(Directory.Exists(database));
    }

    [Fact]
    public void Refresh_ReorderedOrRemovedRows_PreservesIdThenNearestRow()
    {
        using var fixture = new Harness();
        fixture.Write("""{"executors":[{"id":"a"},{"id":"b"},{"id":"c"}],"lanes":[]}""");
        var tab = Tab(fixture);
        tab.HandleKey(Key.CursorUp);
        Assert.Equal(4, tab.SelectedLine);
        tab.HandleKey(Key.CursorDown);
        Assert.Equal(0, tab.SelectedLine);
        tab.HandleKey(Key.CursorDown);
        Assert.Equal(2, tab.SelectedLine);

        fixture.Write("""{"executors":[{"id":"b"},{"id":"c"},{"id":"a"}],"lanes":[]}""");
        tab.Refresh();
        Assert.Equal(0, tab.SelectedLine);
        Assert.StartsWith("b:", tab.Lines[tab.SelectedLine]);
        fixture.Write("""{"executors":[{"id":"c"},{"id":"a"}],"lanes":[]}""");
        tab.Refresh();
        Assert.Equal(0, tab.SelectedLine);
        Assert.StartsWith("c:", tab.Lines[tab.SelectedLine]);
        fixture.Write("""{"executors":[],"lanes":[]}""");
        tab.Refresh();
        Assert.Equal(-1, tab.SelectedLine);
    }

    private static OwnerConsoleExecutorsSettingsTab Tab(Harness fixture) => new(fixture.Workspace, new(fixture.Workspace));
    private static string Ledger(Harness fixture) => Path.Combine(fixture.Folder, ExecutorSlotDecisionLedger.FileName);

    private static void WriteDecision(Harness fixture, string id, int proposed, bool pinned, string application)
    {
        var settings = new ExecutorSlotControllerSettings();
        var state = new ExecutorSlotControllerState(3, settings);
        var decision = new ExecutorSlotDecisionResult("hold", "hold", 3, proposed, pinned, "test", 3, 0,
            new Dictionary<string, int>(), [], null, null, null, 0, state);
        ExecutorSlotDecisionLedger.Append(Ledger(fixture), new(Guid.NewGuid().ToString("N"), Harness.At,
            id, settings, decision, state, application));
    }
}
