using Mcg.AgentOrchestrator.App.OwnerConsole;

// Per-controller fakes and pure snapshots; no terminal initialization or shared state.
public sealed class OwnerFleetExpandKeyTests
{
    [Fact]
    public async Task FleetKeyTogglesBothWaysAndPreservesSessionStateAndFields()
    {
        var harness = new OwnerConsoleHarness();
        var first = OwnerFleetDiagramTests.EightLanes();
        var model = first with { Host = first.Host with { ExecutorCount = 2 },
            Executors = [first.Executors[0], first.Executors[0] with { Id = "beta", Lanes = [] }] };
        var dialogs = new Dialogs();
        var loads = 0;
        using var controller = OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, dialogs,
            harness.State, harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock,
            fleet: () => { loads++; return model; });

        await controller.RunCommandAsync(":fleet");
        await controller.RunCommandAsync(":help");
        await controller.RunCommandAsync(":fleet");
        await controller.RunCommandAsync(":fleet");

        Assert.Equal(3, loads); // Each opening refreshes once; toggles render the existing snapshot.
        Assert.Equal(new[] { false, true, true, false, false }, dialogs.States);
        Assert.Equal(5, dialogs.Renders.Count);
        var collapsed = dialogs.Renders[0];
        var expanded = dialogs.Renders[1];
        Assert.Equal(5, LaneRows(collapsed.Text));
        Assert.Contains("+3 more lanes", collapsed.Text);
        Assert.Equal(8, LaneRows(expanded.Text));
        Assert.DoesNotContain("more lanes", expanded.Text);
        Assert.Equal(2, BetaStart(expanded.Text) - BetaStart(collapsed.Text)); // Three added lanes replace the hidden-count row.
        Assert.Equal(collapsed.Text, dialogs.Renders[3].Text);
        Assert.Equal(collapsed.Text, dialogs.Renders[4].Text);
        Assert.All(dialogs.Renders, rendered => AssertMapping(rendered.Text, rendered.Fields));
        Assert.Contains("(x)", collapsed.Fields.FooterHint);
        Assert.Contains("F2 fields", expanded.Fields.FooterHint);
    }

    private static int LaneRows(string text) => text.Split('\n').Count(row => row.StartsWith("│ lane-", StringComparison.Ordinal));
    private static int BetaStart(string text) => Array.FindIndex(text.Split('\n'), row => row.Contains("Executor beta", StringComparison.Ordinal));

    private static void AssertMapping(string text, OwnerConsoleStructuredText fields)
    {
        var rows = text.Split('\n');
        foreach (var id in new[] { "alpha", "beta" })
        {
            var title = Array.FindIndex(rows, row => row.Contains("Executor " + id, StringComparison.Ordinal));
            var end = Array.FindIndex(rows, title, row => row.StartsWith('└'));
            Assert.True(title > 0 && end > title);
            for (var line = title - 1; line <= end; line++)
            {
                var snapshot = fields.At(line);
                Assert.NotNull(snapshot);
                Assert.Equal(id, snapshot.Identity);
                Assert.Contains(snapshot.Fields, field => field.Key == "executor" && field.Value == id);
            }
            Assert.Null(fields.At(title - 2));
        }
        Assert.Equal("host", fields.At(0)?.Identity);
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        internal List<(string Text, OwnerConsoleStructuredText Fields)> Renders { get; } = [];
        internal List<bool> States { get; } = [];
        private int _opens;
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException("unexpected confirmation");
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException("unexpected prompt");
        public Task ShowTextAsync(string title, string text) => Task.CompletedTask;
        public Task ShowFleetAsync(OwnerFleetViewKeys keys, OwnerFleetViewModel model, int width)
        {
            Assert.False(keys.Handle('z'));
            Assert.False(keys.Handle('x', ctrl: true));
            Capture();
            if (++_opens <= 2)
            {
                Assert.True(keys.Handle(_opens == 1 ? 'X' : 'x'));
                Capture();
            }
            return Task.CompletedTask;

            void Capture() { States.Add(keys.Expanded); Renders.Add(keys.Render(model, width)); }
        }
    }
}
