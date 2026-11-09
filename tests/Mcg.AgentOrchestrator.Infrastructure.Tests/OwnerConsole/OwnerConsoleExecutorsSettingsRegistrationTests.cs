using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Harness = OwnerConsoleExecutorsSettingsTestContext;

// Parallel-safe: isolated workspace/settings; application objects are never initialized.
public sealed class OwnerConsoleExecutorsSettingsRegistrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Composer_Workspace_RegistersExecutorsIndependentlyOfSound(bool hasSound)
    {
        using var fixture = new Harness();
        using var settings = new OwnerConsoleSettingsTestContext();
        var harness = new OwnerConsoleHarness();
        using var controller = new OwnerConsoleScreenController(harness.Questions, harness.Answers, settings.Output,
            new OwnerGoalDetailQuery(harness.State, harness.Tail), harness.Conductor, harness.DigestReport,
            harness.Digest, harness.Clock, sound: hasSound ? settings.Sound : null, workspace: fixture.Workspace);
        using IApplication app = Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        var frame = view.SettingsView;
        frame.Open(OwnerConsolePane.Board);
        Assert.Equal(0, frame.ActiveTab);

        if (hasSound)
        {
            Assert.Contains(frame.Lines, line => line.StartsWith("calm:", StringComparison.Ordinal));
            frame.HandleKey(Key.CursorRight);
            Assert.Equal(1, frame.ActiveTab);
        }
        Assert.StartsWith("mcg-exec-1: enabled", frame.Lines[0]);
        frame.HandleKey(Key.CursorRight);
        Assert.Equal(0, frame.ActiveTab);
        if (hasSound) Assert.Contains(frame.Lines, line => line.StartsWith("calm:", StringComparison.Ordinal));
        else Assert.StartsWith("mcg-exec-1: enabled", frame.Lines[0]);

        frame.HandleKey(Key.CursorLeft);
        Assert.Equal(hasSound ? 1 : 0, frame.ActiveTab);
        Assert.StartsWith("mcg-exec-1: enabled", frame.Lines[0]);
        frame.HandleKey(Key.CursorLeft);
        Assert.Equal(0, frame.ActiveTab);
        if (hasSound) Assert.Contains(frame.Lines, line => line.StartsWith("calm:", StringComparison.Ordinal));
    }
}
