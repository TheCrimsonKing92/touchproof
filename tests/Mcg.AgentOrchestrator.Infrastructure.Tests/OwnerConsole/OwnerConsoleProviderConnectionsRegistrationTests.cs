using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.App;
using Terminal.Gui.Input;

public sealed class OwnerConsoleProviderConnectionsRegistrationTests
{
    [Fact]
    public void ComposerAddsProviderConnectionsToSettings()
    {
        using var workspace = new OwnerConsoleExecutorsSettingsTestContext();
        using var settings = new OwnerConsoleSettingsTestContext();
        var list = new TaskCompletionSource<IReadOnlyList<OwnerConsoleProviderConnection>>();
        using var provider = new OwnerConsoleProviderConnectionsTabTests.Fixture { PendingList = list.Task };
        var harness = new OwnerConsoleHarness();
        using var controller = OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, settings.Output,
            harness.State, harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock,
            workspace: workspace.Workspace, providerConnections: provider, providerSignIn: provider);
        using IApplication app = Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        var frame = view.SettingsView;
        frame.Open(OwnerConsolePane.Board);
        Assert.StartsWith("mcg-exec-1: enabled", frame.Lines[0]);
        frame.HandleKey(Key.CursorRight);
        Assert.Equal(1, frame.ActiveTab);
        Assert.Equal("Reading configured profiles and readiness evidence…", frame.Lines[0]);
        frame.HandleKey(Key.CursorRight);
        Assert.Equal(0, frame.ActiveTab);
    }
}
