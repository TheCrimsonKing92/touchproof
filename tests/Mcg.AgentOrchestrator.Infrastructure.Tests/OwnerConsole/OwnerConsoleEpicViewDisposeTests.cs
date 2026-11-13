using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Fixture = OwnerConsoleEpicViewFixture;

// Parallel-safe: per-test kernels, in-memory sources and an uninitialized instance application.
public sealed class OwnerConsoleEpicViewDisposeTests
{
    [Fact]
    public async Task Dispose_HeldIgnoringRead_DrainsAndUnsubscribes()
    {
        using var fixture = new Fixture();
        using var dialog = new OwnerConsoleEpicDialog(fixture.Controller, action => action(), CancellationToken.None);
        var pending = fixture.Source.HoldNextLoad();
        var opening = dialog.OpenAsync(OwnerConsolePane.Board);
        await fixture.Source.LoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        dialog.Dispose();
        await opening.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(pending.Task.IsCompleted);
        var calls = fixture.Source.Calls;
        fixture.Controller.Apply(fixture.Model);
        Assert.Equal(calls, fixture.Source.Calls);
        fixture.Source.Release(pending, null);
        Assert.False(dialog.IsOpen);
    }

    [Fact]
    public async Task Dispose_QueuedPublication_CannotTouchWidgets()
    {
        using var fixture = new Fixture();
        var callbacks = new Queue<Action>();
        using var dialog = new OwnerConsoleEpicDialog(fixture.Controller, callbacks.Enqueue, CancellationToken.None);
        await dialog.OpenAsync(OwnerConsolePane.Board).WaitAsync(TestContext.Current.CancellationToken);
        var callback = Assert.Single(callbacks);
        var lines = dialog.Lines.ToArray();
        dialog.Dispose();
        callback();
        Assert.Equal(lines, dialog.Lines);
        Assert.False(dialog.IsOpen);
    }
}
