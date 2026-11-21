using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.App;
using Terminal.Gui.Input;

// Per-test controller, workspace and dialog state.
public sealed class OwnerFleetSignInKeyTests
{
    private const string SignInRequired = "last confirmed: authentication-required";
    private const string UsageExhausted = "last confirmed: usage-exhausted";

    [Fact]
    public void OnlyFirstEligibleHoldShowsSignInHintAndItsFields()
    {
        var keys = new OwnerFleetViewKeys();
        var model = Fleet(Hold("anthropic", UsageExhausted), Hold("codex", null),
            Hold("xai", SignInRequired), Hold("openai", SignInRequired));

        var (text, fields) = keys.Render(model, OwnerFleetDiagram.DefaultWidth);
        var hint = Assert.Single(text.Split('\n').Select((line, index) => (Line: line, Index: index)),
            row => row.Line.Contains("Press s to open sign-in", StringComparison.Ordinal));
        Assert.Equal("Press s to open sign-in for xai", hint.Line.Trim('║', ' '));
        Assert.Equal("xai::<provider-default>", fields.At(hint.Index)?.Identity);
        Assert.Contains("Sign in (s)", fields.FooterHint);
        Assert.False(keys.Handle('s', ctrl: true));
        Assert.False(keys.SignInRequested);
    }

    [Theory]
    [InlineData(UsageExhausted)]
    [InlineData(null)]
    public void IneligibleSnapshotRemovesTheOfferFromBoxFooterAndKey(string? diagnosis)
    {
        var keys = new OwnerFleetViewKeys();
        var model = Fleet(Hold("xai", SignInRequired));
        Assert.Contains("Sign in (s)", keys.Render(model, OwnerFleetDiagram.DefaultWidth).Fields.FooterHint);

        var (text, fields) = keys.Render(Fleet(Hold("xai", diagnosis)), OwnerFleetDiagram.DefaultWidth);
        Assert.DoesNotContain("Press s to open sign-in", text);
        Assert.DoesNotContain("Sign in (s)", fields.FooterHint);
        Assert.False(keys.Handle('s'));
        Assert.False(keys.SignInRequested);
    }

    [Fact]
    public async Task FleetSignInOpensProviderConnectionsWithMatchingProfile()
    {
        using var workspace = new OwnerConsoleExecutorsSettingsTestContext();
        using var context = new OwnerConsoleSettingsTestContext();
        using var provider = new OwnerConsoleProviderConnectionsTabTests.Fixture
        {
            Initial = [OwnerConsoleProviderConnectionsTabTests.Fixture.Connection("codex-cli"),
                OwnerConsoleProviderConnectionsTabTests.Fixture.Connection("grok-cli")],
            Roster = [OwnerConsoleProviderConnectionsTabTests.Fixture.Agent("codex-cli", AgentRole.Developer),
                OwnerConsoleProviderConnectionsTabTests.Fixture.Agent("grok-cli", AgentRole.Reviewer)]
        };
        AgentCatalogStore.Save(workspace.Workspace.AgentCatalogPath, new AgentCatalog(provider.Roster));
        var dialogs = new FleetDialogs();
        var model = Fleet(Hold("xai", SignInRequired), Hold("openai", SignInRequired));
        var harness = new OwnerConsoleHarness();
        using var controller = OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, dialogs,
            harness.State, harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock,
            fleet: () => model, sound: context.Sound, workspace: workspace.Workspace,
            providerConnections: provider, providerSignIn: provider);
        using IApplication app = Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask, clock: context.Clock);
        view.Render(OwnerConsoleSettingsTestContext.Model());
        view.FocusPane(OwnerConsolePane.Board);
        var settings = view.SettingsView;
        var fleetBinding = Assert.IsType<OwnerConsoleBinding>(OwnerConsoleBindings.ForLine("fleet"));
        var fleet = view.RunBindingAsync(fleetBinding);
        var keys = await TestHangGuard.WaitAsync(dialogs.Opened.Task, "Fleet dialog to open");
        Assert.False(settings.IsOpen);

        Assert.True(keys.Handle('s'));
        Assert.True(keys.SignInRequested);
        dialogs.Close();
        await TestHangGuard.WaitAsync(fleet, "Fleet sign-in to return to Settings");

        Assert.False(keys.SignInRequested);
        Assert.True(settings.IsOpen);
        Assert.Equal(OwnerConsolePane.Board, settings.ReturnPane);
        Assert.Equal("xai", settings.TargetProvider);
        await provider.TakeCheckAsync();
        await provider.TakeCheckAsync();
        Assert.Contains("  Profiles: grok-cli", settings.Lines);

        await view.HandleKeyAsync(new Key('q'));
        model = Fleet(Hold("xai", UsageExhausted));
        dialogs.Reset();
        var reopened = view.RunBindingAsync(fleetBinding);
        keys = await TestHangGuard.WaitAsync(dialogs.Opened.Task, "ineligible Fleet dialog to open");
        Assert.False(keys.Handle('s'));
        Assert.False(keys.SignInRequested);
        Assert.False(reopened.IsCompleted);
        Assert.False(settings.IsOpen);
        dialogs.Close();
        await TestHangGuard.WaitAsync(reopened, "ineligible Fleet dialog to close");
        Assert.False(settings.IsOpen);
        Assert.Null(settings.TargetProvider);
    }

    private static OwnerFleetViewModel Fleet(params OwnerFleetProviderHoldNode[] holds) =>
        new(new(false, 0, 0, 0), [], holds);

    private static OwnerFleetProviderHoldNode Hold(string provider, string? diagnosis) =>
        new(provider + "::<provider-default>", provider, "fixture", DateTimeOffset.UnixEpoch, null, null, null, null)
        { Diagnosis = diagnosis };

    private sealed class FleetDialogs : IOwnerConsoleDialogs
    {
        internal TaskCompletionSource<OwnerFleetViewKeys> Opened { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ShowFleetAsync(OwnerFleetViewKeys keys, OwnerFleetViewModel model, int width)
        {
            keys.Render(model, width);
            Opened.SetResult(keys);
            return _closed.Task;
        }

        internal void Close() => _closed.SetResult();

        internal void Reset()
        {
            Opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException(title);
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException(title);
        public Task ShowTextAsync(string title, string text) => throw new InvalidOperationException(title);
    }
}
