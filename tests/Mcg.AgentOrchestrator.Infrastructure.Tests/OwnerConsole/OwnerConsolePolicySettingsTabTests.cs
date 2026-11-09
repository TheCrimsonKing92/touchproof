using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core.Conductor;
using Terminal.Gui.App;
using Terminal.Gui.Input;

// Parallel-safe: each test owns its policy file; no GUI application is initialized.
public sealed class OwnerConsolePolicySettingsTabTests
{
    private static readonly JsonDocumentOptions JsonOptions = new() { CommentHandling = JsonCommentHandling.Skip };

    [Fact]
    public void Composer_RegistersPolicyAfterAlertsAndEditsTheInjectedFile()
    {
        using var fixture = new Fixture();
        using var settings = new OwnerConsoleSettingsTestContext();
        var harness = new OwnerConsoleHarness();
        using var controller = OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, settings.Output,
            harness.State, harness.Tail, harness.Conductor, harness.DigestReport,
            harness.Digest, harness.Clock, sound: settings.Sound, conductorPolicyPath: fixture.Path);
        using IApplication app = Application.Create();
        using var console = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        var view = console.SettingsView;
        view.Open(OwnerConsolePane.Board);
        Assert.Contains(view.Lines, line => line.StartsWith("calm:", StringComparison.Ordinal));
        view.HandleKey(Key.CursorRight);
        Assert.Equal(1, view.ActiveTab);
        Edit(view, "acceptanceWidth", "3");
        Assert.Equal(3, ConductorAutonomyPolicy.ParseJson(File.ReadAllText(fixture.Path)).AcceptanceWidth);
        view.HandleKey(Key.CursorLeft);
        Assert.Contains(view.Lines, line => line.StartsWith("calm:", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoEdits_ChangeOnlyTheirTokensAndKeepUnknownKeysAndBom()
    {
        using var fixture = new Fixture();
        var before = File.ReadAllBytes(fixture.Path);
        var tab = new OwnerConsolePolicySettingsTab(fixture.Path);
        using var view = new OwnerConsoleSettingsView([tab]);
        view.Open(OwnerConsolePane.Board);
        Edit(view, "acceptanceWidth", "3");
        Assert.False(tab.Editing);
        Assert.Contains("next conductor relaunch", tab.StatusText);
        Edit(view, "boardFillMaxDraftsPerDay", "8");
        Assert.False(tab.Editing);
        Assert.Contains("next conductor relaunch", tab.StatusText);
        var expected = fixture.Original.Replace("\"acceptanceWidth\": 1", "\"acceptanceWidth\": 3", StringComparison.Ordinal)
            .Replace("\"boardFillMaxDraftsPerDay\": 6", "\"boardFillMaxDraftsPerDay\": 8", StringComparison.Ordinal);
        Assert.NotEqual(before, File.ReadAllBytes(fixture.Path));
        Assert.Equal(WithBom(expected), File.ReadAllBytes(fixture.Path));
        var original = Parse(fixture.Original);
        var saved = Parse(File.ReadAllText(fixture.Path));
        foreach (var pair in original)
            if (pair.Key is not ("acceptanceWidth" or "boardFillMaxDraftsPerDay"))
                Assert.True(JsonNode.DeepEquals(pair.Value, saved[pair.Key]), pair.Key);
        var loaded = ConductorAutonomyPolicy.ParseJson(File.ReadAllText(fixture.Path));
        Assert.Equal(3, loaded.AcceptanceWidth);
        Assert.Equal(8, loaded.BoardFillMaxDraftsPerDay);
        Assert.Single(Directory.GetFiles(fixture.Root));
    }

    [Theory]
    [InlineData("acceptanceWidth", "\"3\"", "integer")]
    [InlineData("acceptanceWidth", "1.5", "integer")]
    [InlineData("acceptanceWidth", "abc", "acceptanceWidth")]
    [InlineData("acceptanceWidth", "5", "acceptanceWidth must be between 1 and 4")]
    [InlineData("boardFillMaxDraftsPerDay", "21", "boardFillMaxDraftsPerDay must be between 0 and 20")]
    [InlineData("buildGcConserveMemoryLevel", "10", "buildGcConserveMemoryLevel must be between 0 and 9")]
    [InlineData("buildGcHeapCount", "65", "buildGcHeapCount must be between 0 and 64")]
    [InlineData("buildRootRetentionDays", "366", "buildRootRetentionDays must be between 0 and 365")]
    [InlineData("acceptanceAttemptBelowNormalPriority", "1", "boolean")]
    [InlineData("autoPromoteRiskThreshold", "Extreme", "valid values")]
    [InlineData("autoPromoteRiskThreshold", "7", "valid values")]
    [InlineData("autoPromoteRiskThreshold", "1", "valid values")]
    [InlineData("maxTotalBudget", "\"20\"", "decimal")]
    public void InvalidInput_ReportsRefusalAndLeavesBytesUntouched(string key, string input, string message)
    {
        using var fixture = new Fixture();
        var before = File.ReadAllBytes(fixture.Path);
        var tab = new OwnerConsolePolicySettingsTab(fixture.Path);
        using var view = new OwnerConsoleSettingsView([tab]);
        view.Open(OwnerConsolePane.Decisions);
        Edit(view, key, input);
        Assert.True(tab.Editing);
        Assert.Contains(message, tab.StatusText);
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
        Assert.Single(Directory.GetFiles(fixture.Root));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("adoptedBuildPushEnabled")]
    [InlineData("transitionMap")]
    [InlineData("perProviderBudgetCaps")]
    [InlineData("unknownExtra")]
    [InlineData("boardFillMode")]
    public void ReadOnlyKeys_HaveNoEditingAction(string key)
    {
        using var fixture = new Fixture();
        var before = File.ReadAllBytes(fixture.Path);
        var tab = new OwnerConsolePolicySettingsTab(fixture.Path);
        using var view = new OwnerConsoleSettingsView([tab]);
        view.Open(OwnerConsolePane.Activity);
        Highlight(view, key);
        Assert.Contains("changes only from the command line or by the owner", view.Lines[tab.SelectedLine]);
        foreach (var action in new[] { Key.Enter, new Key('x'), new Key('9'), Key.Backspace, Key.Delete, new Key(' '), Key.Enter })
        {
            view.HandleKey(action);
            Assert.False(tab.Editing);
            Assert.Equal(before, File.ReadAllBytes(fixture.Path));
        }
        Assert.Contains("Read-only", tab.StatusText);
    }

    [Fact]
    public void DisplayedDefaults_EqualLoaderWithEachOptionalKeyRemoved()
    {
        using var fixture = new Fixture();
        var tab = new OwnerConsolePolicySettingsTab(fixture.Path);
        var original = Parse(fixture.Original);
        using var known = JsonDocument.Parse(ConductorAutonomyPolicy.ParseJson(fixture.Original).ToJson());
        foreach (var member in known.RootElement.EnumerateObject())
        {
            var withoutKey = original.DeepClone().AsObject();
            withoutKey.Remove(member.Name);
            if (member.Name is "name" or "maxConcurrentPaidWorkers" or "transitionMap")
            {
                Assert.Throws<FormatException>(() => ConductorAutonomyPolicy.ParseJson(withoutKey.ToJsonString()));
                Assert.Equal("no loader default", tab.DefaultValue(member.Name));
                continue;
            }
            var loaded = ConductorAutonomyPolicy.ParseJson(withoutKey.ToJsonString());
            using var expected = JsonDocument.Parse(loaded.ToJson());
            var defaultValue = tab.DefaultValue(member.Name);
            using var displayed = JsonDocument.Parse(defaultValue);
            Assert.True(JsonElement.DeepEquals(expected.RootElement.GetProperty(member.Name), displayed.RootElement), member.Name);
            Assert.Contains(tab.Lines, line => line.StartsWith(member.Name + ":", StringComparison.Ordinal) &&
                line.Contains("default " + defaultValue, StringComparison.Ordinal));
        }
        foreach (var key in new[] { "maxTotalBudget", "perProviderBudgetCaps", "unknownExtra" })
        {
            Assert.Equal("no loader default", tab.DefaultValue(key));
            Assert.Contains(tab.Lines, line => line.StartsWith(key + ":", StringComparison.Ordinal) && line.Contains("no loader default"));
        }
    }

    [Theory]
    [InlineData("acceptanceChildrenOptOutExecutionThrottling", "true", "next use")]
    [InlineData("buildGcConserveMemory", "true", "next use")]
    [InlineData("buildGcConserveMemoryLevel", "9", "next use")]
    [InlineData("buildGcHeapCount", "4", "next use")]
    [InlineData("buildRootRetentionDays", "7", "next use")]
    [InlineData("acceptanceAttemptBelowNormalPriority", "false", "next conductor relaunch")]
    [InlineData("cascadeTesterCheapFirst", "false", "next conductor relaunch")]
    [InlineData("maxConcurrentPaidWorkers", "6", "next conductor relaunch")]
    [InlineData("maxCriterionRetries", "3", "next conductor relaunch")]
    [InlineData("plannerSampleCount", "3", "next conductor relaunch")]
    [InlineData("autoPromoteRiskThreshold", "Security", "next conductor relaunch")]
    [InlineData("autoPromoteRiskThreshold", "null", "next conductor relaunch")]
    [InlineData("maxTotalBudget", "21.00", "Not read by the conductor")]
    public void OtherEditableKeys_SaveOnlyTheirValueAndReportTiming(string key, string input, string timing)
    {
        using var fixture = new Fixture();
        var tab = new OwnerConsolePolicySettingsTab(fixture.Path);
        using var view = new OwnerConsoleSettingsView([tab]);
        view.Open(OwnerConsolePane.Board);
        Edit(view, key, input);
        Assert.False(tab.Editing);
        Assert.Contains(timing, tab.StatusText);
        var before = Parse(fixture.Original);
        var after = Parse(File.ReadAllText(fixture.Path));
        Assert.False(JsonNode.DeepEquals(before[key], after[key]));
        foreach (var pair in before)
            if (pair.Key != key) Assert.True(JsonNode.DeepEquals(pair.Value, after[pair.Key]), pair.Key);
        _ = ConductorAutonomyPolicy.ParseJson(File.ReadAllText(fixture.Path));
        if (key == "maxTotalBudget") Assert.Contains("21.00", File.ReadAllText(fixture.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingEditableKey_IsInsertedWithoutRewritingOtherBytes(bool inline)
    {
        using var fixture = new Fixture();
        var original = fixture.Original.Replace("  \"acceptanceWidth\": 1,\r\n", "", StringComparison.Ordinal);
        if (inline) original = Parse(original).ToJsonString();
        File.WriteAllBytes(fixture.Path, WithBom(original));
        var tab = new OwnerConsolePolicySettingsTab(fixture.Path);
        using var view = new OwnerConsoleSettingsView([tab]);
        view.Open(OwnerConsolePane.Board);
        Assert.Contains(view.Lines, line => line.StartsWith("acceptanceWidth: 2 (absent)", StringComparison.Ordinal));
        Edit(view, "acceptanceWidth", "3");
        Assert.False(tab.Editing);
        var insertion = inline ? ", \"acceptanceWidth\": 3" : ",\r\n  \"acceptanceWidth\": 3";
        var saved = File.ReadAllText(fixture.Path);
        Assert.Contains(insertion, saved);
        Assert.Equal(WithBom(original), WithBom(saved.Replace(insertion, "", StringComparison.Ordinal)));
        Assert.Equal(3, ConductorAutonomyPolicy.ParseJson(saved).AcceptanceWidth);
    }

    [Fact]
    public void StaleEdit_RefusesExternalChangesUntilCancelledAndReloaded()
    {
        using var fixture = new Fixture();
        var tab = new OwnerConsolePolicySettingsTab(fixture.Path);
        using var view = new OwnerConsoleSettingsView([tab]);
        view.Open(OwnerConsolePane.Board);
        Highlight(view, "acceptanceWidth");
        view.HandleKey(Key.Enter);
        view.HandleKey(new Key('3'));
        var changed = WithBom(fixture.Original + "\r\n// external writer");
        File.WriteAllBytes(fixture.Path, changed);
        view.HandleKey(Key.Enter);
        Assert.True(tab.Editing);
        Assert.Contains("changed on disk", tab.StatusText);
        Assert.Contains("3_", tab.StatusText);
        Assert.Equal(changed, File.ReadAllBytes(fixture.Path));
        view.HandleKey(Key.Esc);
        view.HandleKey(new Key('r'));
        Edit(view, "acceptanceWidth", "3");
        Assert.False(tab.Editing);
        Assert.Contains("// external writer", File.ReadAllText(fixture.Path));
    }

    [Theory]
    [InlineData("noop")]
    [InlineData("cancel")]
    [InlineData("duplicate")]
    [InlineData("locked")]
    public void UncommittedEdits_KeepBytesAndLeaveNoTemporaryFiles(string kind)
    {
        using var fixture = new Fixture();
        if (kind == "duplicate") File.WriteAllBytes(fixture.Path, WithBom(fixture.Original.Replace(
            "\"acceptanceWidth\": 1,", "\"acceptanceWidth\": 1, \"acceptanceWidth\": 1,", StringComparison.Ordinal)));
        var before = File.ReadAllBytes(fixture.Path);
        var tab = new OwnerConsolePolicySettingsTab(fixture.Path);
        using var view = new OwnerConsoleSettingsView([tab]);
        view.Open(OwnerConsolePane.Board);
        Highlight(view, "acceptanceWidth");
        view.HandleKey(Key.Enter);
        view.HandleKey(new Key(kind == "noop" ? '1' : '3'));
        // Readers are allowed; Windows replacement is refused while the destination denies delete sharing.
        using var held = kind == "locked" ? new FileStream(fixture.Path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        view.HandleKey(kind == "cancel" ? Key.Esc : Key.Enter);
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
        Assert.Single(Directory.GetFiles(fixture.Root));
        Assert.Equal(kind is "duplicate" or "locked", tab.Editing);
        Assert.Contains(kind switch
        {
            "noop" => "No changes", "cancel" => "cancelled", "duplicate" => "more than once", _ => "Save refused"
        }, tab.StatusText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrMalformedPolicy_ShowsErrorAndDisablesEdits(bool missing)
    {
        using var fixture = new Fixture();
        if (missing) File.Delete(fixture.Path);
        else File.WriteAllText(fixture.Path, "{broken");
        var tab = new OwnerConsolePolicySettingsTab(fixture.Path);
        using var view = new OwnerConsoleSettingsView([tab]);
        view.Open(OwnerConsolePane.Board);
        Highlight(view, "acceptanceWidth");
        view.HandleKey(Key.Enter);
        view.HandleKey(new Key('3'));
        view.HandleKey(Key.Enter);
        Assert.False(tab.Editing);
        Assert.Contains(missing ? "missing" : "not valid JSON", tab.StatusText);
        if (missing) Assert.False(File.Exists(fixture.Path));
        else Assert.Equal("{broken", File.ReadAllText(fixture.Path));
    }

    private static void Edit(OwnerConsoleSettingsView view, string key, string value)
    {
        Highlight(view, key);
        view.HandleKey(Key.Enter);
        foreach (var character in value) view.HandleKey(new Key(character));
        view.HandleKey(Key.Enter);
    }

    private static void Highlight(OwnerConsoleSettingsView view, string key)
    {
        view.HandleKey(Key.Home);
        var index = view.Lines.ToList().FindIndex(line => line.StartsWith(key + ":", StringComparison.Ordinal));
        Assert.True(index >= 0, $"Missing policy row: {key}");
        for (var row = 0; row < index; row++) view.HandleKey(Key.CursorDown);
    }

    private static JsonObject Parse(string text) => JsonNode.Parse(text, documentOptions: JsonOptions)!.AsObject();
    private static byte[] WithBom(string text) => Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "policy-tab-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(Root, "conductor-policy.json");
        internal string Original { get; } = ConductorAutonomyPolicy.Permissive.ToJson().ReplaceLineEndings("\r\n")
            .Replace("\"acceptanceWidth\": 2", "\"acceptanceWidth\": 1", StringComparison.Ordinal)
            .Replace("\"boardFillMaxDraftsPerDay\": 3", "\"boardFillMaxDraftsPerDay\": 6", StringComparison.Ordinal)
            .Insert(3, "  // owner comment\r\n  \"unknownExtra\": {\"keep\": [\"雪\", 2]},\r\n  \"maxTotalBudget\": 20.00,\r\n  \"perProviderBudgetCaps\": null,\r\n");

        internal Fixture() { Directory.CreateDirectory(Root); File.WriteAllBytes(Path, WithBom(Original)); }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
