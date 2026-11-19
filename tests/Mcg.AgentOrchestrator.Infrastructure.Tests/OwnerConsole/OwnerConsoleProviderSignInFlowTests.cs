using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.Input;
using Fixture = OwnerConsoleProviderConnectionsTabTests.Fixture;

public sealed class OwnerConsoleProviderSignInFlowTests
{
    [Theory]
    [InlineData((int)ProviderSignInOutcome.Unknown, "Authentication outcome unknown")]
    [InlineData((int)ProviderSignInOutcome.Cancelled, "Native sign-in was cancelled")]
    [InlineData((int)ProviderSignInOutcome.Succeeded, "reported successful native sign-in")]
    public async Task NativeExitRechecksSelectedProfile(int outcome, string expected)
    {
        using var fixture = new Fixture();
        var session = new Session();
        fixture.Launch = new(session);
        await fixture.LoadAsync();
        fixture.Tab.HandleKey(Key.CursorDown);
        fixture.Press('s');
        await fixture.ApplyAsync(); // Native window started.
        Assert.Contains("t return", fixture.Tab.KeyHint);
        fixture.Press('s');
        fixture.Tab.HandleKey(Key.CursorUp);
        Assert.DoesNotContain("s sign in", fixture.Tab.KeyHint);
        fixture.Press('s'); // Shares the active Codex credential source.
        fixture.Tab.HandleKey(Key.CursorDown);
        Assert.Single(DrainLaunches(fixture));

        session.Exit.SetResult(new((ProviderSignInOutcome)outcome, 0));
        await fixture.ApplyAsync();
        var check = await fixture.TakeCheckAsync();
        Assert.Equal("codex-luna", check.ProfileId);
        check.Result.SetResult(Fixture.Connection("codex-luna", ProviderConnectionResult.SignedOut));
        await fixture.ApplyAsync();
        Assert.Contains(expected, fixture.Tab.StatusText);
        Assert.Contains("  Host / CodexLoginStatus | OAuth | SignedOut", fixture.Tab.Lines);
        Assert.False(fixture.Launches.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData('t', "Authentication outcome unknown")]
    [InlineData('x', "The native window may still be open")]
    public async Task ManualReturnRechecksOnceAndBlocksSharedSignInUntilNativeExit(char action, string expected)
    {
        using var fixture = new Fixture();
        var session = new Session();
        fixture.Launch = new(session);
        await fixture.LoadAsync();
        fixture.Press('s');
        await fixture.ApplyAsync();
        fixture.Press(action);
        var check = await fixture.TakeCheckAsync();
        Assert.Equal("codex-cli", check.ProfileId);
        check.Result.SetResult(Fixture.Connection("codex-cli", ProviderConnectionResult.SignedIn));
        await fixture.ApplyAsync();
        Assert.Contains("  Host / CodexLoginStatus | OAuth | SignedIn", fixture.Tab.Lines);
        Assert.Contains(expected, fixture.Tab.StatusText);
        fixture.Press('t');
        fixture.Press('x');
        Assert.Contains(expected, fixture.Tab.StatusText);
        Assert.DoesNotContain("s sign in", fixture.Tab.KeyHint);
        fixture.Press('s');
        Assert.Contains(expected, fixture.Tab.StatusText);
        fixture.Tab.HandleKey(Key.CursorDown);
        Assert.DoesNotContain("s sign in", fixture.Tab.KeyHint);
        fixture.Press('s');
        Assert.DoesNotContain("Opening the provider's native sign-in", fixture.Tab.StatusText);

        var profiles = fixture.Initial;
        fixture.Initial = [profiles[1]];
        fixture.Press('r');
        await fixture.ApplyAsync();
        Assert.DoesNotContain("s sign in", fixture.Tab.KeyHint);
        fixture.Press('s');
        Assert.DoesNotContain(fixture.Tab.Lines, line => line.Contains("Opening the provider's native sign-in"));
        Assert.Single(DrainLaunches(fixture));
        fixture.Initial = profiles;
        fixture.Press('r');
        await fixture.ApplyAsync();
        fixture.Tab.HandleKey(Key.CursorUp);

        session.Exit.SetResult(new(ProviderSignInOutcome.Unknown, 0));
        await fixture.ApplyAsync();
        await TestHangGuard.WaitAsync(session.Disposed.Task, "native process observation released after exit");
        Assert.Contains("Native sign-in exited", fixture.Tab.StatusText);
        Assert.Contains("s sign in", fixture.Tab.KeyHint);
        fixture.Tab.HandleKey(Key.CursorDown);
        Assert.Contains("s sign in", fixture.Tab.KeyHint);
    }

    [Fact]
    public async Task ApiKeyUnsupportedAndLaunchFailureKeepReadiness()
    {
        using var fixture = new Fixture
        {
            Initial = [Fixture.Connection("codex-cli", mode: ProviderConnectionAuthMode.ApiKey),
                Fixture.Connection("codex-luna") with { SignIn = null },
                Fixture.Connection("grok-cli")]
        };
        await fixture.LoadAsync();
        Assert.DoesNotContain("s sign in", fixture.Tab.KeyHint);
        Assert.Contains(fixture.Tab.Lines, l => l.Contains("Manage the API key"));
        fixture.Press('s');
        fixture.Tab.HandleKey(Key.CursorDown);
        Assert.DoesNotContain("s sign in", fixture.Tab.KeyHint);
        fixture.Press('s');
        fixture.Tab.HandleKey(Key.CursorDown);
        fixture.Press('s');
        await fixture.ApplyAsync();
        Assert.Contains("Could not open native sign-in", fixture.Tab.StatusText);
        Assert.Contains("  Host / CodexLoginStatus | ApiKey | SignedOut", fixture.Tab.Lines);
        Assert.Single(DrainLaunches(fixture));
    }

    [Fact]
    public async Task AdvertisedDeviceCodeIsOfferedAndSelected()
    {
        using var fixture = new Fixture { DeviceCode = true, Initial = [Fixture.Connection("grok-cli")] };
        await fixture.LoadAsync();
        await fixture.ApplyAsync();
        Assert.Contains("d device code", fixture.Tab.KeyHint);
        fixture.Press('d');
        await fixture.ApplyAsync();
        Assert.True(Assert.Single(DrainLaunches(fixture)).DeviceCode);
    }

    private static List<(ProviderSignInTarget Target, bool DeviceCode)> DrainLaunches(Fixture fixture)
    {
        var launches = new List<(ProviderSignInTarget, bool)>();
        while (fixture.Launches.Reader.TryRead(out var launch)) launches.Add(launch);
        return launches;
    }

    private sealed class Session : IProviderSignInSession
    {
        internal TaskCompletionSource<ProviderSignInExit> Exit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ProviderSignInExit> WaitAsync(CancellationToken token) => Exit.Task.WaitAsync(token);
        public void Dispose() => Disposed.TrySetResult();
    }
}
