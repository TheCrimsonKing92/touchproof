using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.Input;

// Parallel-safe: each case owns its three files and view; no runtime workspace or global state.
public sealed class OwnerConsoleAgentsSettingsTabTests
{
    [Fact]
    public void SeatEdit_AliasAndEffort_ChangesExactlyTwoValues()
    {
        using var context = new Context();
        var expected = context.Agents.DeepClone();
        var fields = expected["Agents"]![0]!["Subscription"]!;
        fields["ModelAlias"] = "custom alias";
        fields["ReasoningEffort"] = "low";
        var before = context.Bytes();
        context.Open("Sol |");
        context.Type("custom alias");
        context.Down();
        context.Type("low");
        context.Save();

        Assert.False(context.Tab.Editing);
        AssertDocument(expected, context.Agents);
        Assert.Equal(before[1], File.ReadAllBytes(context.FunctionsPath));
        Assert.Equal(before[2], File.ReadAllBytes(context.WorkersPath));
        Assert.Contains("next relaunch", context.Tab.StatusText);
        context.AssertOnlyThreeFiles();
    }

    [Theory]
    [InlineData(0, "   ", "non-empty")]
    [InlineData(1, "ultra", "Reasoning effort")]
    [InlineData(2, "unknown-profile", "Worker profile")]
    public void InvalidEdit_RefusesWithMessage_LeavesEveryFileIdentical(int field, string value, string message)
    {
        using var context = new Context();
        var before = context.Bytes();
        context.Open("Sol |");
        for (var index = 0; index < field; index++) context.Down();
        context.Type(value);
        context.Save();

        Assert.True(context.Tab.Editing);
        Assert.Contains(message, context.Tab.StatusText);
        Assert.DoesNotContain("next relaunch", context.Tab.StatusText);
        context.AssertBytes(before);
    }

    [Fact]
    public void StatusEdit_OfflineAndAvailable_RoundTripsAndShowsRelaunchNotice()
    {
        using var context = new Context();
        var original = context.Agents;
        context.Open("Sol |");
        context.Down(3);
        context.View.HandleKey(Key.CursorRight);
        context.Save();
        var expected = original.DeepClone();
        expected["Agents"]![0]!["Status"] = "Offline";
        AssertDocument(expected, context.Agents);
        Assert.Contains("next relaunch", context.Tab.StatusText);

        context.Open("Sol |");
        context.Down(3);
        context.View.HandleKey(Key.CursorRight);
        context.Save();
        AssertDocument(original, context.Agents);
        Assert.Contains("next relaunch", context.Tab.StatusText);
    }

    [Fact]
    public void LocalBinding_ModelAndNullEffort_PreservesFallbackAndDisablesProfile()
    {
        using var context = new Context();
        var expected = context.Functions.DeepClone();
        expected["Bindings"]![0]!["Model"]!["ModelName"] = "local:new";
        expected["Bindings"]![0]!["Model"]!["ReasoningEffort"] = null;
        var before = context.Bytes();
        context.Open("acceptance-judge |");
        Assert.Contains("Worker profile disabled", context.Tab.StatusText);
        Assert.DoesNotContain(context.View.Lines, line => line.StartsWith("Worker profile:"));
        context.Type("local:new");
        context.Down();
        context.Type("");
        context.Save();

        Assert.False(context.Tab.Editing);
        AssertDocument(expected, context.Functions);
        Assert.Equal(before[0], File.ReadAllBytes(context.AgentsPath));
        Assert.Equal(before[2], File.ReadAllBytes(context.WorkersPath));
        Assert.Contains("next relaunch", context.Tab.StatusText);
    }

    [Fact]
    public void SubscriptionBinding_ProfileAndAlias_LeavesModelAndFallbackUntouched()
    {
        using var context = new Context();
        var expected = context.Functions.DeepClone();
        expected["Bindings"]![1]!["Subscription"]!["ModelAlias"] = "new-function";
        expected["Bindings"]![1]!["Subscription"]!["WorkerProfileName"] = "claude-cli";
        var before = context.Bytes();
        context.Open("conductor-author |");
        context.Type("new-function");
        context.Down(2);
        context.View.HandleKey(Key.CursorRight);
        context.Save();
        AssertDocument(expected, context.Functions);
        Assert.Equal(before[0], File.ReadAllBytes(context.AgentsPath));
        Assert.Equal(before[2], File.ReadAllBytes(context.WorkersPath));
        Assert.False(context.Tab.Editing);
    }

    [Fact]
    public void EffortEdit_UsesFileLocalChoices_RejectsValueFromOtherFile()
    {
        using var context = new Context();
        var before = context.Bytes();
        context.Open("Sol |");
        context.Down();
        Assert.Contains("choices: null, high, low", context.View.Lines[context.Tab.SelectedLine]);
        context.Type("medium"); // Present only in model-functions.json.
        context.Save();
        Assert.True(context.Tab.Editing);
        Assert.Contains("Reasoning effort", context.Tab.StatusText);
        context.AssertBytes(before);
    }

    [Fact]
    public void ExternalChange_RefusesStaleSave_KeepsPendingEditUntilDiscarded()
    {
        using var context = new Context();
        context.Open("Sol |");
        context.Type("pending");
        var external = context.Agents;
        external["Unknown"] = "external change";
        File.WriteAllText(context.AgentsPath, external.ToJsonString());
        var before = context.Bytes();
        context.Save();
        Assert.True(context.Tab.Editing);
        Assert.Contains("changed on disk", context.Tab.StatusText);
        Assert.Contains("pending", context.View.Lines[1]);
        Assert.DoesNotContain("next relaunch", context.Tab.StatusText);
        context.AssertBytes(before);
        Assert.False(context.View.HandleKey(Key.Esc));
        Assert.True(context.View.IsOpen);
        context.View.HandleKey(new Key('r'));
        context.Open("Sol |");
        context.Type("re-applied");
        context.Save();
        Assert.Equal("external change", context.Agents["Unknown"]!.GetValue<string>());
        Assert.Equal("re-applied", context.Agents["Agents"]![0]!["Subscription"]!["ModelAlias"]!.GetValue<string>());
    }

    [Fact]
    public void CtrlR_WhileEditing_DiscardsPendingEditAndReloadsRows()
    {
        using var context = new Context();
        context.Open("Sol |");
        context.Type("pending");
        var external = context.Agents;
        external["Agents"]![0]!["Name"] = "Reloaded seat";
        File.WriteAllText(context.AgentsPath, external.ToJsonString());
        var before = context.Bytes();
        Assert.True(context.Tab.Editing);
        Assert.Contains("pending", context.View.Lines[1]);

        Assert.False(context.View.HandleKey(Key.R.WithCtrl));

        Assert.False(context.Tab.Editing);
        Assert.True(context.View.IsOpen);
        Assert.Contains(context.View.Lines, line => line.StartsWith("Reloaded seat |"));
        Assert.DoesNotContain(context.View.Lines, line => line.StartsWith("Sol |"));
        Assert.Contains("discarded; reloaded", context.Tab.StatusText);
        context.AssertBytes(before);
        context.Open("Reloaded seat |");
        Assert.Equal("ModelAlias: old", context.View.Lines[1]);
    }

    [Fact]
    public void RemovedWorkerProfile_RevalidatesOnSave_RefusesWithoutWriting()
    {
        using var context = new Context();
        context.Open("Sol |");
        context.Type("new");
        File.WriteAllText(context.WorkersPath, "{\"Profiles\":[]}");
        var before = context.Bytes();
        context.Save();
        Assert.True(context.Tab.Editing);
        Assert.Contains("Worker profile", context.Tab.StatusText);
        context.AssertBytes(before);
    }

    [Fact]
    public void CancelEdit_BlocksTabAndCloseNavigation_UntilExplicitDiscard()
    {
        using var context = new Context();
        var before = context.Bytes();
        context.Open("Busy seat |");
        context.Type("q");
        Assert.True(context.View.IsOpen);
        context.View.HandleKey(Key.CursorRight);
        Assert.True(context.Tab.Editing);
        Assert.False(context.View.HandleKey(Key.Esc));
        Assert.False(context.Tab.Editing);
        Assert.True(context.View.IsOpen);
        context.AssertBytes(before);
        context.Open("Busy seat |");
        context.Type("busy-new");
        context.Save();
        Assert.Equal("Busy", context.Agents["Agents"]![1]!["Status"]!.GetValue<string>());
    }

    [Fact]
    public void ReplaceFailure_PreservesDestinationAndPendingEdit_CleansTemporaryFile()
    {
        var replacementCalled = false;
        using var context = new Context((temporary, destination) =>
        {
            replacementCalled = true;
            Assert.True(File.Exists(temporary));
            Assert.Equal(Path.GetDirectoryName(destination), Path.GetDirectoryName(temporary));
            Assert.Equal("new", JsonNode.Parse(File.ReadAllText(temporary))!["Agents"]![0]!["Subscription"]!["ModelAlias"]!.GetValue<string>());
            throw new IOException("replacement refused");
        });
        var before = context.Bytes();
        context.Open("Sol |");
        context.Type("new");
        context.Save();
        Assert.True(replacementCalled);
        Assert.True(context.Tab.Editing);
        Assert.Contains("Save failed: replacement refused", context.Tab.StatusText);
        Assert.DoesNotContain("next relaunch", context.Tab.StatusText);
        context.AssertBytes(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Save_PreservesLineEndingsIndentationAndBom(bool crlfAndBom)
    {
        using var context = new Context();
        var newline = crlfAndBom ? "\r\n" : "\n";
        var text = context.Agents.ToJsonString(new JsonSerializerOptions { WriteIndented = true, NewLine = newline });
        File.WriteAllText(context.AgentsPath, text + newline, new UTF8Encoding(crlfAndBom));
        context.View.HandleKey(new Key('r'));
        context.Open("Sol |");
        context.Type("new");
        context.Save();
        Assert.False(context.Tab.Editing);
        var bytes = File.ReadAllBytes(context.AgentsPath);
        Assert.Equal(crlfAndBom, bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        var saved = File.ReadAllText(context.AgentsPath);
        Assert.Contains(newline + "  \"Agents\": [", saved);
        Assert.EndsWith(newline, saved);
        if (crlfAndBom) Assert.DoesNotContain("\n", saved.Replace("\r\n", ""));
        else Assert.DoesNotContain("\r", saved);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("array")]
    public void InvalidAgentsFile_ShowsActionableError_StillListsBindings(string failure)
    {
        using var context = new Context();
        if (failure == "missing") File.Delete(context.AgentsPath);
        else File.WriteAllText(context.AgentsPath, failure == "array" ? "{}" : "{");
        context.View.HandleKey(new Key('r'));
        Assert.Contains(context.View.Lines, line => line.Contains("Cannot load agents.json:") && line.Contains("reload"));
        Assert.Contains(context.View.Lines, line => line.StartsWith("acceptance-judge |"));
        Assert.Equal(failure != "missing", File.Exists(context.AgentsPath));
    }

    private static void AssertDocument(JsonNode expected, JsonNode actual)
    {
        Assert.True(JsonNode.DeepEquals(expected, actual), "Parsed configuration differs beyond the selected edits.");
        AssertKeyOrder(expected, actual);
    }

    private static void AssertKeyOrder(JsonNode? expected, JsonNode? actual)
    {
        if (expected is JsonObject obj)
        {
            var other = Assert.IsType<JsonObject>(actual);
            Assert.Equal(obj.Select(pair => pair.Key).ToArray(), other.Select(pair => pair.Key).ToArray());
            foreach (var (key, value) in obj) AssertKeyOrder(value, other[key]);
        }
        else if (expected is JsonArray array)
            for (var index = 0; index < array.Count; index++) AssertKeyOrder(array[index], actual![index]);
    }

    private sealed class Context : IDisposable
    {
        private readonly string _root = SharedTestSupport.CreateTempDirectory();
        internal string AgentsPath => Path.Combine(_root, "agents.json");
        internal string FunctionsPath => Path.Combine(_root, "model-functions.json");
        internal string WorkersPath => Path.Combine(_root, "workers.json");
        internal JsonNode Agents => JsonNode.Parse(File.ReadAllText(AgentsPath))!;
        internal JsonNode Functions => JsonNode.Parse(File.ReadAllText(FunctionsPath))!;
        internal OwnerConsoleAgentsSettingsTab Tab { get; }
        internal OwnerConsoleSettingsView View { get; }
        internal Context(Action<string, string>? replace = null)
        {
            File.WriteAllText(AgentsPath, """
                {
                  "Agents": [
                    {"Id":{"Value":"one"},"Name":"Sol","Role":"Developer","Status":"Available","ExecutionPolicy":"Preserve",
                     "Model":{"ProviderName":"OpenAI","ModelName":"unchanged-model","SubscriptionMode":"ApiKey","ReasoningEffort":null,"Capabilities":["Code"],"MaxOutputTokens":1024},
                     "Subscription":{"WorkerProfileName":"codex-cli","ModelAlias":"old","ReasoningEffort":"high"},
                     "ComplexModel":{"ProviderName":"Other","ModelName":"complex","ReasoningEffort":"low"},"Unknown":null},
                    {"Id":{"Value":"two"},"Name":"Busy seat","Role":"Tester","Status":"Busy",
                     "Model":{"ProviderName":"Other","ModelName":"keep","ReasoningEffort":null},
                     "Subscription":{"WorkerProfileName":"codex-cli","ModelAlias":"busy","ReasoningEffort":null}}
                  ],
                  "Unknown": {"keep":[null,1,true]}
                }
                """);
            File.WriteAllText(FunctionsPath, """
                {
                  "Bindings": [
                    {"Purpose":"acceptance-judge","Lane":"local","Name":null,
                     "Model":{"ProviderName":"Ollama","ModelName":"local:old","SubscriptionMode":"LocalBridge","ReasoningEffort":"medium"},
                     "Subscription":null,"SubscriptionFallback":{"WorkerProfileName":"claude-cli","ModelAlias":"fallback","ReasoningEffort":"high"}},
                    {"Purpose":"conductor-author","Lane":"subscription","Name":"author",
                     "Model":{"ProviderName":"OpenAI","ModelName":"keep","ReasoningEffort":null},
                     "Subscription":{"WorkerProfileName":"codex-cli","ModelAlias":"author-old","ReasoningEffort":"high"},
                     "SubscriptionFallback":{"WorkerProfileName":"claude-cli","ModelAlias":"backup","ReasoningEffort":"low"}}
                  ],
                  "Unknown":null
                }
                """);
            File.WriteAllText(WorkersPath, """
                {"Profiles":[{"Name":"codex-cli","CommandTemplate":"never execute"},{"Name":"claude-cli","CommandTemplate":"never execute"}]}
                """);
            Tab = new(AgentsPath, FunctionsPath, WorkersPath, replace);
            View = new([Tab]);
            View.Open(OwnerConsolePane.Decisions);
        }

        internal void Open(string prefix)
        {
            var index = View.Lines.ToList().FindIndex(line => line.StartsWith(prefix, StringComparison.Ordinal));
            Assert.True(index >= 0, $"Missing record: {prefix}");
            while (Tab.SelectedLine < index) View.HandleKey(Key.CursorDown);
            while (Tab.SelectedLine > index) View.HandleKey(Key.CursorUp);
            View.HandleKey(Key.Enter);
            Assert.True(Tab.Editing);
        }
        internal void Down(int count = 1) { for (var index = 0; index < count; index++) View.HandleKey(Key.CursorDown); }
        internal void Type(string value)
        {
            // Bounded by the displayed field's length; no time-based synchronization.
            var length = View.Lines[Tab.SelectedLine].Length;
            for (var index = 0; index < length; index++) View.HandleKey(Key.Backspace);
            foreach (var character in value) View.HandleKey(new Key(character));
        }
        internal void Save() => View.HandleKey(Key.Enter);
        internal byte[][] Bytes() => [File.ReadAllBytes(AgentsPath), File.ReadAllBytes(FunctionsPath), File.ReadAllBytes(WorkersPath)];
        internal void AssertBytes(byte[][] expected)
        {
            var actual = Bytes();
            for (var index = 0; index < actual.Length; index++) Assert.Equal(expected[index], actual[index]);
            AssertOnlyThreeFiles();
        }
        internal void AssertOnlyThreeFiles() => Assert.Equal(
            new[] { "agents.json", "model-functions.json", "workers.json" },
            Directory.GetFiles(_root).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
        public void Dispose() { View.Dispose(); Directory.Delete(_root, true); }
    }
}
