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
        Assert.StartsWith("t return  x stop waiting  c check", fixture.Tab.KeyHint);
        fixture.Press('s');
        fixture.Tab.HandleKey(Key.CursorUp);
        Assert.StartsWith("s sign in", fixture.Tab.KeyHint);
        fixture.Tab.HandleKey(Key.CursorDown);
        Assert.Equal(ProviderKind.XaiGrokCli, Assert.Single(DrainLaunches(fixture)).Target.Kind);

        session.Exit.SetResult(new((ProviderSignInOutcome)outcome, 0));
        await fixture.ApplyAsync();
        var check = await fixture.TakeCheckAsync();
        Assert.Equal("grok-cli", check.ProfileId);
        check.Result.SetResult(Fixture.Connection("grok-cli", ProviderConnectionResult.SignedOut));
        await fixture.ApplyAsync();
        Assert.Contains(expected, fixture.Tab.StatusText);
        Assert.Contains("  The provider reported no sign-in on the host.", fixture.Tab.Lines);
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
        check.Result.SetResult(Fixture.Connection("codex-cli", ProviderConnectionResult.SignedIn, at: fixture.Clock.GetUtcNow()));
        await fixture.ApplyAsync();
        Assert.StartsWith("OpenAI | signed in: yes", fixture.Tab.Lines[0]);
        Assert.Contains(expected, fixture.Tab.StatusText);
        fixture.Press('t');
        fixture.Press('x');
        Assert.Contains(expected, fixture.Tab.StatusText);
        Assert.DoesNotContain("s sign in", fixture.Tab.KeyHint);
        fixture.Press('s');
        Assert.Contains(expected, fixture.Tab.StatusText);
        fixture.Tab.HandleKey(Key.CursorDown);
        Assert.StartsWith("s sign in", fixture.Tab.KeyHint);

        var profiles = fixture.Initial;
        fixture.Initial = [profiles[1], profiles[2]];
        fixture.Press('r');
        await fixture.ApplyAsync();
        Assert.StartsWith("s sign in", fixture.Tab.KeyHint); // Reload preserved the selected Grok row.
        fixture.Tab.HandleKey(Key.CursorUp);
        Assert.DoesNotContain("s sign in", fixture.Tab.KeyHint);
        fixture.Press('s');
        Assert.DoesNotContain(fixture.Tab.Lines, line => line.Contains("Opening the provider's native sign-in"));
        Assert.Single(DrainLaunches(fixture));
        fixture.Initial = profiles;
        fixture.Press('r');
        await fixture.ApplyAsync();

        session.Exit.SetResult(new(ProviderSignInOutcome.Unknown, 0));
        await fixture.ApplyAsync();
        await TestHangGuard.WaitAsync(session.Disposed.Task, "native process observation released after exit");
        Assert.Contains("Native sign-in exited", fixture.Tab.StatusText);
        Assert.Contains("s sign in", fixture.Tab.KeyHint);
        fixture.Tab.HandleKey(Key.CursorDown);
        Assert.Contains("s sign in", fixture.Tab.KeyHint);
    }

    [Fact]
    public async Task ApiKeyBlocksSignInAndLaunchFailureKeepsReadiness()
    {
        using var fixture = new Fixture
        {
            Initial = [Fixture.Connection("codex-cli", mode: ProviderConnectionAuthMode.ApiKey),
                Fixture.Connection("codex-luna") with { SignIn = null },
                Fixture.Connection("grok-cli")]
        };
        await fixture.LoadAsync();
        Assert.DoesNotContain("s sign in", fixture.Tab.KeyHint);
        Assert.Contains("  Using API key from the host; sign-in would not change what workers use", fixture.Tab.Lines);
        fixture.Press('s');
        fixture.Tab.HandleKey(Key.CursorDown);
        fixture.Press('s');
        await fixture.ApplyAsync();
        Assert.Contains("Could not open native sign-in", fixture.Tab.StatusText);
        Assert.Contains("  The provider reported no sign-in on the host.", fixture.Tab.Lines);
        Assert.Single(DrainLaunches(fixture));
    }

    [Fact]
    public async Task AdvertisedDeviceCodeIsOfferedAndSelected()
    {
        var deviceCodeProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture
        {
            DeviceCodeProbe = deviceCodeProbe,
            Initial = [Fixture.Connection("grok-cli")]
        };
        await fixture.LoadAsync(initialChecks: 1);
        deviceCodeProbe.SetResult(true);
        await fixture.ApplyAsync();
        Assert.StartsWith("s sign in  d device code  c check", fixture.Tab.KeyHint);
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
