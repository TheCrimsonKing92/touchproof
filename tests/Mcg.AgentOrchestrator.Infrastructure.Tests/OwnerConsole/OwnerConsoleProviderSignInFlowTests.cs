using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnusedProfileKeysKeepExactIdWithSharedNativeTarget(bool siblingInUse)
    {
        var sibling = Fixture.Connection("codex-cli");
        var selected = sibling with { Report = sibling.Report with { ProfileId = "codex-secondary" } };
        var session = new Session();
        using var fixture = new Fixture
        {
            Initial = [sibling, selected],
            Roster = siblingInUse ? [Fixture.Agent("codex-cli", AgentRole.Developer)] : [],
            Launch = new(session)
        };
        await fixture.LoadAsync(initialChecks: siblingInUse ? 1 : 0);
        if (siblingInUse) fixture.Tab.HandleKey(Key.CursorDown);
        fixture.Tab.HandleKey(Key.Enter);
        fixture.Tab.HandleKey(Key.CursorDown);
        if (!siblingInUse) fixture.Tab.HandleKey(Key.CursorDown);
        Assert.StartsWith("  codex-secondary | signed in: no", fixture.Tab.Lines[fixture.Tab.SelectedLine]);

        fixture.Press('s');
        await fixture.ApplyAsync();
        Assert.Equal("codex-secondary", Assert.Single(fixture.SignInLookups));
        Assert.Equal(sibling.SignIn, Assert.Single(DrainLaunches(fixture)).Target);
        Assert.StartsWith("t return  x stop waiting  c check", fixture.Tab.KeyHint);
        fixture.Press('t');
        var returnCheck = await fixture.TakeCheckAsync();
        Assert.Equal("codex-secondary", returnCheck.ProfileId);
        var signedIn = Fixture.Connection("codex-cli", ProviderConnectionResult.SignedIn, at: fixture.Clock.GetUtcNow());
        returnCheck.Result.SetResult(signedIn with { Report = signedIn.Report with { ProfileId = "codex-secondary" } });
        await fixture.ApplyAsync();
        Assert.StartsWith("  codex-secondary | signed in: yes", fixture.Tab.Lines[fixture.Tab.SelectedLine]);

        fixture.Press('c');
        var explicitCheck = await fixture.TakeCheckAsync();
        Assert.Equal("codex-secondary", explicitCheck.ProfileId);
        await fixture.FinishCheckAsync(explicitCheck);
        session.Exit.SetResult(new(ProviderSignInOutcome.Unknown, 0));
        await fixture.ApplyAsync();
        Assert.Equal((siblingInUse ? 1 : 0) + 2, fixture.CheckCalls);
        Assert.Equal("codex-secondary", Assert.Single(fixture.SignInLookups));
        Assert.Empty(DrainLaunches(fixture));
    }

    [Theory]
    [InlineData(true, "Using API key from the host")]
    [InlineData(false, "has no native sign-in command")]
    public async Task UnsupportedUnusedProfileOffersCheckAndNeverLaunches(bool apiKey, string explanation)
    {
        var unsupported = apiKey
            ? Fixture.Connection("grok-cli", mode: ProviderConnectionAuthMode.ApiKey)
            : Fixture.Connection("grok-cli") with { SignIn = null };
        using var fixture = new Fixture { Initial = [unsupported], Roster = [] };
        await fixture.LoadAsync(initialChecks: 0);
        fixture.Tab.HandleKey(Key.Enter);
        fixture.Tab.HandleKey(Key.CursorDown);
        Assert.StartsWith("  grok-cli |", fixture.Tab.Lines[fixture.Tab.SelectedLine]);
        Assert.StartsWith("c check  Enter collapse Not in use", fixture.Tab.KeyHint);
        Assert.DoesNotContain("s sign in", fixture.Tab.KeyHint);
        Assert.DoesNotContain("d device code", fixture.Tab.KeyHint);
        Assert.Contains(fixture.Tab.Lines, line => line.Contains(explanation, StringComparison.Ordinal));
        Assert.Equal(1, fixture.ScheduledProviderWork); // Only the initial list.
        fixture.Press('s');
        fixture.Press('d');
        Assert.Equal(1, fixture.ScheduledProviderWork);
        fixture.Press('c');
        var check = await fixture.TakeCheckAsync();
        Assert.Equal("grok-cli", check.ProfileId);
        await fixture.FinishCheckAsync(check);
        Assert.Equal(2, fixture.ScheduledProviderWork);
        Assert.Empty(fixture.SignInLookups);
        Assert.Empty(DrainLaunches(fixture));
        Assert.Equal(0, fixture.DeviceCodeProbes);
    }

    [Fact]
    public async Task RemovingUnusedProfileDoesNotRedirectPendingLoginOrCheck()
    {
        var selected = Fixture.Connection("grok-cli");
        var sibling = selected with { Report = selected.Report with { ProfileId = "grok-secondary" } };
        var session = new Session();
        using var fixture = new Fixture
        {
            Initial = [sibling, selected], Roster = [], Launch = new(session)
        };
        await fixture.LoadAsync(initialChecks: 0);
        fixture.Tab.HandleKey(Key.Enter);
        fixture.Tab.HandleKey(Key.CursorDown);
        fixture.Tab.HandleKey(Key.CursorDown);
        Assert.StartsWith("  grok-cli |", fixture.Tab.Lines[fixture.Tab.SelectedLine]);
        fixture.Press('s');
        await fixture.ApplyAsync();
        Assert.Equal("grok-cli", Assert.Single(fixture.SignInLookups));
        Assert.Single(DrainLaunches(fixture));
        fixture.Press('c');
        var pendingCheck = await fixture.TakeCheckAsync();
        Assert.Equal("grok-cli", pendingCheck.ProfileId);

        fixture.Initial = [sibling];
        fixture.Press('r');
        await fixture.ApplyAsync();
        Assert.Equal("Not in use (1) [-]", fixture.Tab.Lines[fixture.Tab.SelectedLine]);
        foreach (var action in "csdtx") fixture.Press(action);
        fixture.Tab.HandleKey(Key.CursorDown);
        Assert.StartsWith("  grok-secondary | signed in: no", fixture.Tab.Lines[fixture.Tab.SelectedLine]);
        Assert.StartsWith("c check", fixture.Tab.KeyHint); // The old process still holds the shared credentials.

        pendingCheck.Result.SetResult(Fixture.Connection("grok-cli", ProviderConnectionResult.SignedIn,
            at: fixture.Clock.GetUtcNow()));
        await fixture.ApplyAsync();
        session.Exit.SetResult(new(ProviderSignInOutcome.Succeeded, 0));
        await fixture.ApplyAsync();
        Assert.StartsWith("  grok-secondary | signed in: no", fixture.Tab.Lines[fixture.Tab.SelectedLine]);
        Assert.StartsWith("s sign in", fixture.Tab.KeyHint);
        Assert.Equal("", fixture.Tab.StatusText);
        Assert.Equal(1, fixture.CheckCalls);
        Assert.Equal("grok-cli", Assert.Single(fixture.SignInLookups));
        Assert.Empty(DrainLaunches(fixture));
    }

    [Fact]
    public async Task RemovingUnusedProfileDuringExecutableResolutionRevokesNativeLaunch()
    {
        if (!OperatingSystem.IsWindows()) return;

        var selected = Fixture.Connection("grok-cli");
        var sibling = selected with { Report = selected.Report with { ProfileId = "grok-secondary" } };
        var resolving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolution = new TaskCompletionSource<WorkerProcessRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolved = new WorkerProcessRunResult(0, @"C:\tools\grok.exe" + "\r\n", "");
        var starts = 0;
        var session = new Session();
        var launcher = new ProviderSignInLauncher(@"C:\workspace", (_, token) =>
        {
            if (!resolving.TrySetResult()) return Task.FromResult(resolved);
            return resolution.Task.WaitAsync(token);
        }, _ =>
        {
            Interlocked.Increment(ref starts);
            return session;
        });
        using var fixture = new Fixture(launcher) { Initial = [selected, sibling], Roster = [] };
        await fixture.LoadAsync(initialChecks: 0);
        fixture.Tab.HandleKey(Key.Enter);
        fixture.Tab.HandleKey(Key.CursorDown);
        Assert.StartsWith("  grok-cli |", fixture.Tab.Lines[fixture.Tab.SelectedLine]);

        fixture.Press('s');
        await TestHangGuard.WaitAsync(resolving.Task, "native executable resolution started");
        Assert.Equal("grok-cli", Assert.Single(fixture.SignInLookups));
        fixture.Initial = [sibling];
        fixture.Press('r');
        await fixture.ApplyAsync();
        Assert.Equal("Not in use (1) [-]", fixture.Tab.Lines[fixture.Tab.SelectedLine]);
        foreach (var action in "csdtx") fixture.Press(action);
        fixture.Tab.HandleKey(Key.CursorDown);
        Assert.StartsWith("  grok-secondary |", fixture.Tab.Lines[fixture.Tab.SelectedLine]);
        Assert.StartsWith("s sign in", fixture.Tab.KeyHint);
        Assert.Equal(3, fixture.ScheduledProviderWork); // Initial list, original action, and reload.

        resolution.SetResult(resolved);
        await fixture.ApplyAsync(); // The launch result settles after the resolver is released.
        Assert.Equal(0, Volatile.Read(ref starts));
        Assert.Equal("grok-cli", Assert.Single(fixture.SignInLookups));

        fixture.Press('s');
        await fixture.ApplyAsync();
        Assert.Equal(1, Volatile.Read(ref starts));
        Assert.Equal(new[] { "grok-cli", "grok-secondary" }, fixture.SignInLookups);
        session.Exit.SetResult(new(ProviderSignInOutcome.Unknown, 0));
        await fixture.ApplyAsync();
        var recheck = await fixture.TakeCheckAsync();
        Assert.Equal("grok-secondary", recheck.ProfileId);
        await fixture.FinishCheckAsync(recheck);
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
