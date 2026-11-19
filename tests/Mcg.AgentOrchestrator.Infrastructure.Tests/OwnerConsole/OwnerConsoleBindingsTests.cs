using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.App;
using Terminal.Gui.Input;

// Parallel-safe: immutable registry and isolated Settings contexts.
public sealed class OwnerConsoleBindingsTests
{
    [Fact]
    public void RegistrySatisfiesBindingSafetyRules() => Assert.Empty(OwnerConsoleBindings.Validate(OwnerConsoleBindings.All));

    [Fact]
    public void SettingsResolvesByKeyInEveryPaneAndAsATypedCommand()
    {
        var row = Assert.Single(OwnerConsoleBindings.Commands, row => row.Command == "settings");
        Assert.Equal(OwnerConsoleScope.Global, row.Scope);
        Assert.Equal('s', row.Key);
        foreach (var pane in Enum.GetValues<OwnerConsolePane>())
        {
            Assert.Same(row, OwnerConsoleBindings.ForKey('s', pane));
            Assert.Same(row, OwnerConsoleBindings.ForKey('S', pane));
        }
        Assert.Same(row, OwnerConsoleBindings.ForLine("settings"));
        Assert.Same(row, OwnerConsoleBindings.ForLine(":settings"));
    }

    [Fact]
    public void DispatchVerbsHaveTopLevelBindingRows()
    {
        var verbs = OwnerConsoleDispatchVerbs.All;
        Assert.Contains("settings", verbs);
        Assert.Contains("goal", verbs);
        Assert.Equal(verbs.Count, verbs.Distinct(StringComparer.Ordinal).Count());
        Assert.All(verbs, verb => Assert.Equal(verb.ToLowerInvariant(), verb));
        Assert.Empty(MissingBindingCommands(verbs));
    }

    [Fact]
    public void BindingCoverageReportsMissingVerbByName() =>
        Assert.Equal(["frobnicate"], MissingBindingCommands(OwnerConsoleDispatchVerbs.All.Append("frobnicate")));

    private static string[] MissingBindingCommands(IEnumerable<string> verbs)
    {
        var commands = OwnerConsoleBindings.Commands.Select(row => row.Command).ToHashSet(StringComparer.Ordinal);
        return verbs.Where(verb => !commands.Contains(verb)).ToArray();
    }

    public static IEnumerable<object[]> DispatchVerbs => OwnerConsoleDispatchVerbs.All.Select(verb => new object[] { verb });

    [Theory]
    [MemberData(nameof(DispatchVerbs))]
    public async Task DispatchRecognizesEachVerbAndSettingsRequestsTheView(string verb)
    {
        using var context = new OwnerConsoleSettingsTestContext();
        var usage = OwnerConsoleBindings.Commands.First(row => row.Command == verb).Usage;
        var command = usage.Split('[', 2)[0].TrimEnd().Replace("<id-prefix>", "11111111", StringComparison.Ordinal);

        await context.Controller.RunCommandAsync(command);

        Assert.DoesNotContain(context.Output.Texts, text => text.Contains("unknown command", StringComparison.Ordinal));
        Assert.Equal(verb == "settings", context.Controller.TakeSettingsRequested());
        if (verb == "settings") Assert.Empty(context.Output.Texts);
    }

    [Fact]
    public async Task DispatchReportsUnknownVerb()
    {
        using var context = new OwnerConsoleSettingsTestContext();
        await context.Controller.RunCommandAsync("frobnicate");
        Assert.Contains(context.Output.Texts, text => text.Contains("unknown command", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SettingsKeyOpensTheViewAndQuitReturnsToThePane()
    {
        using var context = new OwnerConsoleSettingsTestContext();
        using IApplication app = Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, context.Controller, () => Task.CompletedTask);
        view.Render(OwnerConsoleSettingsTestContext.Model());
        await view.HandleKeyAsync(Key.Tab);
        Assert.Equal(OwnerConsolePane.Board, view.FocusedPane);

        await view.HandleKeyAsync(new Key('s'));
        Assert.True(view.SettingsView.IsOpen);
        Assert.Equal(OwnerConsolePane.Board, view.SettingsView.ReturnPane);
        await view.HandleKeyAsync(new Key('s'));
        Assert.True(view.SettingsView.IsOpen);
        await view.HandleKeyAsync(new Key('q'));
        Assert.False(view.SettingsView.IsOpen);
        Assert.Equal(OwnerConsolePane.Board, view.FocusedPane);
        Assert.False(context.Controller.QuitRequested);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void OverlappingScopesRejectDuplicateKeys(int secondScope)
    {
        var violation = Assert.Single(OwnerConsoleBindings.Validate([Row("first", 'x'), Row("second", 'X', (OwnerConsoleScope)secondScope)]));
        Assert.Contains("'x'", violation);
        Assert.Contains("first", violation);
        Assert.Contains("second", violation);
    }

    [Fact]
    public void SeparateScopesRequireTheSameNoun()
    {
        var first = Row("first", 'x');
        var second = Row("second", 'x', OwnerConsoleScope.GoalDetail);
        var violation = Assert.Single(OwnerConsoleBindings.Validate([first, second]));
        Assert.Contains("'x'", violation);
        Assert.Contains("first", violation);
        Assert.Contains("second", violation);
        Assert.Empty(OwnerConsoleBindings.Validate([first, second with { Noun = first.Noun }]));
    }

    [Fact]
    public void KeyRequiresACommand()
    {
        var violation = Assert.Single(OwnerConsoleBindings.Validate([Row("", 'x') with { Usage = "missing" }]));
        Assert.Contains("'x'", violation);
        Assert.Contains("missing", violation);
        Assert.Contains("no command", violation);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ExternalKeysRequireSelectionScopeAndConfirmation(bool global, bool confirms)
    {
        var row = Row("external", 'x', global ? OwnerConsoleScope.Global : OwnerConsoleScope.Decisions)
            with { Effect = OwnerConsoleEffect.External, Confirms = confirms };
        var violation = Assert.Single(OwnerConsoleBindings.Validate([row]));
        Assert.Contains("'x'", violation);
        Assert.Contains("external", violation);
        Assert.Contains("unsafe", violation);
        Assert.Empty(OwnerConsoleBindings.Validate([row with { Scope = OwnerConsoleScope.Decisions, Confirms = true }]));
    }

    [Theory]
    [InlineData('j')]
    [InlineData('k')]
    [InlineData('/')]
    [InlineData(':')]
    [InlineData('?')]
    [InlineData('q')]
    public void ReservedKeysRejectUnrelatedCommands(char key)
    {
        var violation = Assert.Single(OwnerConsoleBindings.Validate([Row("unrelated", key)]));
        Assert.Contains($"'{key}'", violation);
        Assert.Contains("unrelated", violation);
        Assert.Contains("reserved", violation);
    }

    [Fact]
    public void LetterCapCountsDistinctTopLevelLetters()
    {
        var rows = "bcdefghilmn".Select((key, index) => Row("cmd" + index, key,
            index % 2 == 0 ? OwnerConsoleScope.Global : OwnerConsoleScope.Decisions)).ToArray();
        var violation = Assert.Single(OwnerConsoleBindings.Validate(rows));
        Assert.Contains("11", violation);
        foreach (var row in rows) Assert.Contains(row.Key!.Value.ToString(), violation);
        Assert.Empty(OwnerConsoleBindings.Validate(rows[..10]));
        Assert.Empty(OwnerConsoleBindings.Validate([.. rows[..10], Row("dialog", 'x', OwnerConsoleScope.GoalDetail)]));
    }

    [Fact]
    public void LookupUsesScopeCaseAndTheFirstCommandWord()
    {
        Assert.Equal(OwnerConsoleBindings.All.Single(row => row.Command == "landings"), OwnerConsoleBindings.ForKey('L', OwnerConsolePane.Activity));
        Assert.Null(OwnerConsoleBindings.ForKey('a', OwnerConsolePane.Board));
        Assert.NotNull(OwnerConsoleBindings.ForKey('A', OwnerConsolePane.Decisions));
        Assert.True(OwnerConsoleBindings.IsTopLevelKey('A'));
        Assert.False(OwnerConsoleBindings.IsTopLevelKey('x'));
        Assert.Equal(OwnerConsoleBindings.Gate, OwnerConsoleBindings.ForLine("  : GaTe trailing arguments  "));
        Assert.Null(OwnerConsoleBindings.ForLine(" : "));
        Assert.Null(OwnerConsoleBindings.ForLine("unknown"));
    }

    [Fact]
    public void PromptPreservesCommandOrderAndAdvertisesAliases()
    {
        const string expected = "conductor start | conductor stop | conductor status | digest | metrics | bell on | bell off | sound list | sound play <tier> [<n|name>] | sound set <tier> <n|name|system> | goal <id-prefix> | [e] epics | fleet | scope [<name>[,<name>...]|all] | follow | [/] filter <text> | [l] landings | [g] gate | [s] settings | news | [?] help | [q] quit | [a] accept | [r] answer";
        Assert.Equal(expected, OwnerConsoleBindings.CommandPrompt);
        Assert.Equal(expected.Split(" | "), OwnerConsoleBindings.PromptEntries);
        Assert.Equal(expected, OwnerConsoleKeyHints.CommandPrompt);
    }

    private static readonly string[] SettingsHelpLines =
    [
        "s: Open Settings (any pane).",
        ":settings: Open Settings. Use Left/Right to switch tabs (same as s).",
        "Settings Left/Right: Switch to the previous or next tab.",
        "Settings Esc or q: Close Settings and return to the previous pane.",
        "Settings tab Alerts & sounds: Up/Down highlight  p preview  Enter select/edit  s system sound  m mute",
        "Settings tab Agents & models: Up/Down highlight  Enter edit  r reload",
        "Settings tab Conductor policy: Up/Down highlight  Enter edit  r reload",
        "Settings tab Executors: Up/Down highlight  Space enable/disable",
        "Settings tab Projects: Up/Down highlight  Enter edit scope",
        "Settings tab Provider connections: Up/Down profile  c check  s sign in  d device code  t return  x stop waiting  r reload"
    ];

    [Fact]
    public void SettingsHelpListsNavigationAndEveryTabInOrder()
    {
        var lines = OwnerConsoleBindings.HelpText.Split('\n');
        var settingsKeyLine = Array.IndexOf(lines, SettingsHelpLines[0]);
        Assert.NotEqual(-1, settingsKeyLine);
        Assert.Equal(SettingsHelpLines, lines.Skip(settingsKeyLine).Take(SettingsHelpLines.Length));
    }

    [Fact]
    public void HelpPreservesNavigationAndPairsEveryAliasWithItsCommand()
    {
        string[] expected =
        [
            "Tab: Next pane (DECISIONS, BOARD, ACTIVITY).",
            "Shift+Tab: Previous pane (ACTIVITY, BOARD, DECISIONS).",
            "Up: Select the previous row in DECISIONS or BOARD; scroll ACTIVITY up.",
            "Down: Select the next row in DECISIONS or BOARD; scroll ACTIVITY down.",
            "Enter: Open detail in DECISIONS or BOARD, or What this means in ACTIVITY; submit a command or activate a dialog button.",
            ": (any pane): Open the command line.",
            "Esc: Cancel command entry or close a dialog.",
            "e: Open the epic view (any pane).",
            ":epics: Open the epic view (same as e).",
            "Epic list Up: Select the previous epic.",
            "Epic list Down: Select the next epic.",
            "Epic list Enter: Open the selected epic's detail.",
            "Epic list Esc: Close the epic view.",
            "Epic detail Up: Scroll up one line.",
            "Epic detail Down: Scroll down one line.",
            "Epic detail PgUp: Scroll up one page.",
            "Epic detail PgDn: Scroll down one page.",
            "Epic detail Esc: Back to the epic list.",
            "Epic view w (list and detail): Cycle the time window (day, week, all time).",
            "Epic view Ctrl+R (list and detail): Reload the epic view.",
            "/: Filter ACTIVITY by goal id prefix or text (any pane).",
            ":filter <text>: Show only ACTIVITY rows whose goal id starts with <text> or whose line contains <text>; filter with no text shows every row (same as /).",
            "l: Show delivered goals (any pane).",
            ":landings: Show delivered goals: landing commit, integration kind and acceptance (landings today|30) (same as l).",
            "g: Open the GATE view (any pane).",
            ":gate: Open the GATE view (gate <goal-id-prefix> opens the gate that goal is in) (same as g).",
            "GATE view Up/Down PgUp/PgDn: Scroll lanes; Esc: Close the GATE view.",
            "Goal detail d: Open the goal's open question.",
            "Goal detail f: Show the goal's last failure.",
            "Goal detail e: Open the goal's epic.",
            "Goal detail g: Open the GATE view on this goal's gate (same as :gate <goal-id-prefix>).",
            "Goal detail q: Back out of goal detail.",
            .. SettingsHelpLines,
            "?: Open this help dialog (any pane).",
            ":help: Open this help dialog (same as ?).",
            "q: Quit the console (any pane).",
            ":quit: Quit the console (same as q).",
            "a: Accept the selected default in DECISIONS after confirmation.",
            ":accept: Accept the selected default in DECISIONS after confirmation (same as a).",
            "r: Answer the selected decision in DECISIONS.",
            ":answer: Answer the selected decision in DECISIONS (same as r).",
            "Home/End: Select the first/last row in DECISIONS, BOARD or ACTIVITY.",
            "PgUp/PgDn: Move the selection one visible page up/down in DECISIONS, BOARD or ACTIVITY."
        ];
        string[] unkeyed =
        [
            ":conductor start: Start the conductor.",
            ":conductor stop: Stop the conductor.",
            ":conductor status: Show conductor status.",
            ":digest: Show the owner digest.",
            ":metrics: Show conductor metrics.",
            ":bell on: Enable all three bell tiers; use bell <calm|attention|alarm> on|off for one tier.",
            ":bell off: Disable all three bell tiers; use bell <calm|attention|alarm> on|off for one tier.",
            ":sound list: List selected sounds and numbered library clips for each tier.",
            ":sound play <tier> [<n|name>]: Preview the selected sound or a library clip once.",
            ":sound set <tier> <n|name|system>: Save a tier's sound selection and apply it to the next alert.",
            ":goal <id-prefix>: Open live goal detail, landing status and actions.",
            ":fleet: Show the fleet diagram: host and executor boxes.",
            ":scope [<name>[,<name>...]|all]: Show or change the BOARD project scope.",
            ":follow: Follow the newest ACTIVITY row; new events keep it selected until you move the selection or scroll (Home in ACTIVITY does the same).",
            ":news: Show and acknowledge unseen console changes.",
            "Fleet view x: Toggle between the top five lanes and all lanes for every executor."
        ];
        Assert.Equal(string.Join("\n", expected.Concat(unkeyed)), OwnerConsoleBindings.HelpText);
        var lines = OwnerConsoleBindings.HelpText.Split('\n');
        foreach (var row in OwnerConsoleBindings.Commands.Where(row => row.Key.HasValue))
        {
            var commandLine = Assert.Single(lines, line => line.StartsWith($":{row.Usage}: ", StringComparison.Ordinal));
            Assert.Equal(1, commandLine.Split($"(same as {row.Key})", StringSplitOptions.None).Length - 1);
            Assert.Equal(row.KeyHelp, lines[Array.IndexOf(lines, commandLine) - 1]);
        }
        Assert.Equal(OwnerConsoleBindings.HelpText, OwnerConsoleKeyHints.HelpText);
    }

    [Theory]
    [InlineData(0, true, "Enter detail  a accept default  r answer")]
    [InlineData(0, false, "Enter detail")]
    [InlineData(1, false, "Enter goal detail")]
    [InlineData(2, false, "Following latest  Up/Down scroll  Enter what this means")]
    public void PaneHintsCombineSelectionActionsAndGlobalViews(int pane, bool answerable, string prefix)
    {
        const string suffix = "  Home/End first/last  PgUp/PgDn page  Tab/Shift+Tab pane  e epics  / filter  l landings  g gate  s settings  : command  ? help  q quit";
        Assert.Equal(prefix + suffix, OwnerConsoleBindings.PaneHint((OwnerConsolePane)pane, answerable));
        Assert.Equal(prefix + suffix, OwnerConsoleKeyHints.Hint((OwnerConsolePane)pane, answerable));
    }

    private static OwnerConsoleBinding Row(string command, char key, OwnerConsoleScope scope = OwnerConsoleScope.Global) =>
        new(command, command, key, scope, OwnerConsoleEffect.View, OwnerConsoleCommandOwner.View, false,
            command, command, command, command);
}
