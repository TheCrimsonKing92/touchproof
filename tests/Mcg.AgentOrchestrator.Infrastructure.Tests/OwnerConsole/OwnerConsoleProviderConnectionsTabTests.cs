using System.Threading.Channels;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.Input;

public sealed class OwnerConsoleProviderConnectionsTabTests
{
    [Fact]
    public async Task RendersOneRowPerProviderInUseAndCollapsesTheRest()
    {
        using var fixture = new Fixture
        {
            Initial = [Fixture.Connection("codex-cli", ProviderConnectionResult.SignedIn, ProviderConnectionAuthMode.ApiKey,
                    at: new(2026, 1, 1, 0, 5, 0, TimeSpan.Zero)),
                Fixture.Connection("codex-luna", ProviderConnectionResult.SignedIn), Fixture.Connection("grok-cli"),
                Fixture.Connection("claude-cli"), Fixture.Connection("unrouted")],
            Roster = [Fixture.Agent("codex-cli", AgentRole.Developer), Fixture.Agent("codex-spark", AgentRole.Tester, AgentStatus.Busy),
                Fixture.Agent("codex-luna", AgentRole.Developer), Fixture.Agent(null, AgentRole.Reviewer, provider: "xAI"),
                Fixture.Agent("claude-cli", AgentRole.Planner, AgentStatus.Offline)]
        };
        await fixture.LoadAsync();

        Assert.Equal(new[]
        {
            "OpenAI | signed in: yes | used by: Developer, Tester | confirmed: 2026-01-01 00:04 (1 min ago) | s sign in",
            "xAI | signed in: no | used by: Reviewer | confirmed: 2026-01-01 00:04 (1 min ago) | s sign in",
            "Not in use (2): claude-cli, unrouted"
        }, fixture.Tab.Lines.Where(line => !line.StartsWith(' ')));
        Assert.Contains("  Profiles: codex-cli, codex-luna", fixture.Tab.Lines);
        Assert.DoesNotContain(fixture.Tab.Lines.SkipLast(1),
            line => line.Contains("grok-cli") || line.Contains("claude-cli") || line.Contains("unrouted"));

        fixture.Press('c');
        var check = await fixture.TakeCheckAsync();
        Assert.Equal("codex-luna", check.ProfileId);
        Assert.Contains("Checking connection", fixture.Tab.StatusText);
        fixture.Tab.HandleKey(Key.CursorDown);
        Assert.Equal("xAI", fixture.Tab.Lines[fixture.Tab.SelectedLine].Split(" | ")[0]);
        Assert.DoesNotContain(fixture.Tab.Lines, line => line.Contains("codex-cli"));
        Assert.DoesNotContain("Checking connection", fixture.Tab.StatusText);
        check.Result.SetResult(Fixture.Connection("codex-luna", ProviderConnectionResult.SignedOut));
        await fixture.ApplyAsync();
        Assert.StartsWith("OpenAI | signed in: no", fixture.Tab.Lines[0]);
    }

    [Fact]
    public async Task TargetedOpen_PendingList_SelectsProviderIgnoringCase()
    {
        var list = new TaskCompletionSource<IReadOnlyList<OwnerConsoleProviderConnection>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture
        {
            Initial = [Fixture.Connection("codex-cli"), Fixture.Connection("grok-cli")],
            PendingList = list.Task
        };
        using var view = new OwnerConsoleSettingsView([fixture.Tab]);

        view.Open(OwnerConsolePane.Activity, "xai");
        await TestHangGuard.WaitAsync(fixture.ListStarted.Task, "targeted provider list request");
        Assert.True(view.IsOpen);
        Assert.Equal(["Reading configured profiles and readiness evidence…"], view.Lines);
        Assert.Equal(0, fixture.Tab.SelectedLine);
        Assert.Equal("Reading provider connections…", fixture.Tab.StatusText);

        list.SetResult(fixture.Initial);
        await fixture.ApplyAsync();

        Assert.Equal(1, fixture.Tab.SelectedLine);
        Assert.StartsWith("xAI |", fixture.Tab.Lines[fixture.Tab.SelectedLine]);
        Assert.Contains("  Profiles: grok-cli", view.Lines);
        Assert.DoesNotContain("No configured profile", fixture.Tab.StatusText);
        Assert.False(fixture.Launches.Reader.TryRead(out _));
    }

    [Fact]
    public async Task MissingProviderOpen_ListLoaded_KeepsDefaultSelectionAndNamesProvider()
    {
        var list = new TaskCompletionSource<IReadOnlyList<OwnerConsoleProviderConnection>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture
        {
            Initial = [Fixture.Connection("codex-cli"), Fixture.Connection("grok-cli")],
            PendingList = list.Task
        };
        using var view = new OwnerConsoleSettingsView([fixture.Tab]);

        view.Open(OwnerConsolePane.Activity, "anthropic");
        await TestHangGuard.WaitAsync(fixture.ListStarted.Task, "missing provider list request");
        Assert.Equal("Reading provider connections…", fixture.Tab.StatusText);
        list.SetResult(fixture.Initial);
        await fixture.ApplyAsync();

        Assert.True(view.IsOpen);
        Assert.Equal(0, fixture.Tab.SelectedLine);
        Assert.StartsWith("OpenAI |", fixture.Tab.Lines[fixture.Tab.SelectedLine]);
        Assert.Contains("  Profiles: codex-cli", view.Lines);
        Assert.Equal("No configured profile found for provider anthropic.", fixture.Tab.StatusText);
    }

    [Fact]
    public async Task SelectedRowDetailIsPlainLanguageWithLocalTimesAndOneNextStep()
    {
        var connection = Fixture.Connection("codex-cli", at: new(2026, 1, 1, 0, 3, 0, TimeSpan.Zero));
        var otherResults = Enum.GetValues<ProviderConnectionResult>().SelectMany(result => new ProviderConnectionObservation[]
        {
            new(ProviderConnectionContext.Host, ProviderConnectionSource.CodexLoginStatus, result),
            new(ProviderConnectionContext.WorkerSandbox, ProviderConnectionSource.RecordedInvocation, result)
        });
        using var fixture = new Fixture
        {
            Initial = [connection with
            {
                Report = connection.Report with { Observations = connection.Report.Observations.Concat(otherResults).ToArray() }
            }]
        };
        fixture.Clock.Zone = TimeZoneInfo.CreateCustomTimeZone("fixture offset", TimeSpan.FromHours(2), "fixture offset", "fixture offset");
        await fixture.LoadAsync(initialChecks: 1);

        var enumNames = Enum.GetNames<ProviderConnectionResult>().Concat(Enum.GetNames<ProviderConnectionContext>())
            .Concat(Enum.GetNames<ProviderConnectionSource>()).Concat(Enum.GetNames<ProviderConnectionAuthMode>());
        foreach (var line in fixture.Tab.Lines.Append(fixture.Tab.StatusText).Append(fixture.Tab.KeyHint))
        foreach (var name in enumNames)
            Assert.DoesNotContain(name, line, StringComparison.Ordinal);
        Assert.Single(fixture.Tab.Lines, line => line.TrimStart().StartsWith("Next: ", StringComparison.Ordinal));
        Assert.Contains("  The provider reported no sign-in on the host.", fixture.Tab.Lines);
        Assert.Contains("  The provider call succeeded in a worker (previous run).", fixture.Tab.Lines);
        Assert.Contains("confirmed: 2026-01-01 02:03 (2 min ago)", fixture.Tab.Lines[0]);
        Assert.DoesNotMatch(@"\b\d+\s*s(?:ec(?:onds?)?)? ago\b", string.Join("\n", fixture.Tab.Lines));
    }

    [Fact]
    public async Task HostApiKeyAndMissingNativeTargetExplainWhySignInIsNotOffered()
    {
        using var fixture = new Fixture
        {
            Initial = [Fixture.Connection("codex-cli", ProviderConnectionResult.SignedIn, ProviderConnectionAuthMode.ApiKey),
                Fixture.Connection("claude-cli") with { SignIn = null }],
            Roster = [Fixture.Agent("codex-cli", AgentRole.Developer), Fixture.Agent("claude-cli", AgentRole.Tester)]
        };
        await fixture.LoadAsync();

        Assert.Contains("  Using API key from the host; sign-in would not change what workers use", fixture.Tab.Lines);
        Assert.DoesNotContain("unsupported", string.Join("\n", fixture.Tab.Lines), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("s sign in", fixture.Tab.KeyHint);
        Assert.StartsWith("OpenAI | signed in: not checked", fixture.Tab.Lines[0]);
        Assert.Contains("confirmed: not checked", fixture.Tab.Lines[0]);
        fixture.Tab.HandleKey(Key.CursorDown);
        Assert.Contains("  The claude-cli profile does not launch the provider's own command line. Set its launch command to start with claude.",
            fixture.Tab.Lines);
        Assert.DoesNotContain("unsupported", string.Join("\n", fixture.Tab.Lines), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("s sign in", fixture.Tab.KeyHint);
    }

    [Fact]
    public async Task FirstOpenChecksEachProviderInUseOnceWithoutBlockingTheList()
    {
        using var fixture = new Fixture
        {
            Initial = [Fixture.Connection("codex-cli", ProviderConnectionResult.Unknown),
                Fixture.Connection("codex-luna", ProviderConnectionResult.Unknown),
                Fixture.Connection("grok-cli", ProviderConnectionResult.Unknown)]
        };
        IReadOnlyList<string>? firstRender = null;
        fixture.Tab.Invalidate = () => firstRender ??= fixture.Tab.Lines;
        await fixture.LoadAsync(initialChecks: 0);
        var first = await fixture.TakeCheckAsync();
        var second = await fixture.TakeCheckAsync();

        Assert.NotNull(firstRender);
        Assert.Equal(new[] { "OpenAI", "xAI" },
            firstRender.Where(line => !line.StartsWith(' ')).Select(line => line.Split(" | ")[0]));
        Assert.StartsWith("OpenAI | signed in: not checked", firstRender[0]);
        Assert.DoesNotContain(firstRender, line => line.Contains("Checking connection"));
        Assert.Equal("Checking connection…", fixture.Tab.StatusText);
        Assert.Equal(new[] { "codex-cli", "grok-cli" }, new[] { first.ProfileId, second.ProfileId }.Order(StringComparer.Ordinal));
        Assert.Equal(2, fixture.CheckCalls);

        first.Result.SetResult(Fixture.Connection(first.ProfileId, ProviderConnectionResult.SignedIn));
        await fixture.ApplyAsync();
        second.Result.SetResult(Fixture.Connection(second.ProfileId, ProviderConnectionResult.SignedIn));
        await fixture.ApplyAsync();
        Assert.StartsWith("OpenAI | signed in: yes", fixture.Tab.Lines[0]);
        fixture.Tab.Activate();
        Assert.DoesNotContain("Checking connection", fixture.Tab.StatusText);
        fixture.Press('r');
        await fixture.ApplyAsync();
        Assert.DoesNotContain(fixture.Tab.Lines, line => line.Contains("Checking connection"));
        fixture.Tab.HandleKey(Key.CursorDown);
        Assert.DoesNotContain("Checking connection", fixture.Tab.StatusText);
        Assert.Equal(2, fixture.CheckCalls);
    }

    [Fact]
    public async Task KeyHintLeadsWithSignInOnlyWhenTheSelectedRowOffersIt()
    {
        using var fixture = new Fixture
        {
            Initial = [Fixture.Connection("codex-cli"), Fixture.Connection("grok-cli", mode: ProviderConnectionAuthMode.ApiKey)]
        };
        await fixture.LoadAsync();

        Assert.Equal("s sign in  c check  r reload  Up/Down provider  Left/Right tab  Esc/q back", fixture.Tab.KeyHint);
        fixture.Tab.HandleKey(Key.CursorDown);
        Assert.Equal("c check  r reload  Up/Down provider  Left/Right tab  Esc/q back", fixture.Tab.KeyHint);
    }

    [Fact]
    public async Task RosterFailureStartsNoChecksAndReloadCanRecover()
    {
        using var fixture = new Fixture { RosterUnavailable = true };
        await fixture.LoadAsync(initialChecks: 0);
        Assert.Equal("Could not read the agent roster. Press r to retry.", fixture.Tab.StatusText);
        Assert.Equal(0, fixture.CheckCalls);

        fixture.RosterUnavailable = false;
        fixture.Press('r');
        await fixture.ApplyAsync();
        await fixture.FinishCheckAsync(await fixture.TakeCheckAsync());
        await fixture.FinishCheckAsync(await fixture.TakeCheckAsync());
        Assert.Equal(2, fixture.CheckCalls);
    }

    [Fact]
    public void OnlyCurrentHostSignInEvidenceDecidesTheRollup()
    {
        var signedIn = Fixture.Connection("codex-cli", ProviderConnectionResult.SignedIn).Report.Observations[0];
        var later = signedIn.ObservedAt!.Value.AddMinutes(2);
        var worker = signedIn with { ExecutionContext = ProviderConnectionContext.WorkerSandbox, ObservedAt = later };
        var historical = signedIn with { IsHistorical = true, ObservedAt = later };
        var keyPresent = signedIn with { AuthenticationMode = ProviderConnectionAuthMode.ApiKey,
            Result = ProviderConnectionResult.MaterialPresent, ObservedAt = later };
        var signedInWithKey = keyPresent with { Result = ProviderConnectionResult.SignedIn };
        var signedOut = signedIn with { Result = ProviderConnectionResult.SignedOut };
        var expired = signedIn with { Result = ProviderConnectionResult.Expired };
        var undated = expired with { ObservedAt = null };

        Assert.Null(OwnerConsoleProviderRowModel.SelectSignInEvidence([worker, historical, keyPresent]));
        Assert.Null(OwnerConsoleProviderRowModel.SelectSignInEvidence([signedInWithKey]));
        Assert.Equal(signedIn, OwnerConsoleProviderRowModel.SelectSignInEvidence([signedIn, signedInWithKey]));
        Assert.Equal(signedIn, OwnerConsoleProviderRowModel.SelectSignInEvidence([keyPresent, worker, signedIn, undated]));
        Assert.Equal(signedOut, OwnerConsoleProviderRowModel.SelectSignInEvidence([signedIn, signedOut]));
        Assert.Equal(expired, OwnerConsoleProviderRowModel.SelectSignInEvidence([signedIn, signedOut, expired]));
        Assert.Equal(signedIn with { ObservedAt = later },
            OwnerConsoleProviderRowModel.SelectSignInEvidence([expired, signedIn with { ObservedAt = later }]));
    }

    [Fact]
    public async Task OlderCompletionCannotReplaceNewerResult()
    {
        using var fixture = new Fixture();
        await fixture.LoadAsync();
        fixture.Press('c');
        var older = await fixture.TakeCheckAsync();
        fixture.Press('c');
        var newer = await fixture.TakeCheckAsync();

        newer.Result.SetResult(Fixture.Connection("codex-cli", ProviderConnectionResult.SignedIn,
            at: fixture.Clock.GetUtcNow()));
        await fixture.ApplyAsync();
        older.Result.SetResult(Fixture.Connection("codex-cli", ProviderConnectionResult.Expired,
            at: fixture.Clock.GetUtcNow()));
        await fixture.ApplyAsync();

        Assert.StartsWith("OpenAI | signed in: yes", fixture.Tab.Lines[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACheckStartedBeforeReloadKeepsNewerListedReadinessAndLaunchConfiguration(bool removeSignIn)
    {
        using var fixture = new Fixture { Initial = [Fixture.Connection("codex-cli")] };
        await fixture.LoadAsync(initialChecks: 1);
        fixture.Press('c');
        var check = await fixture.TakeCheckAsync();
        var listed = Fixture.Connection("codex-cli", ProviderConnectionResult.SignedIn, at: fixture.Clock.GetUtcNow());
        fixture.Initial = [removeSignIn ? listed with { SignIn = null } : listed];
        fixture.Press('r');
        await fixture.ApplyAsync();

        check.Result.SetResult(Fixture.Connection("codex-cli", ProviderConnectionResult.Expired));
        await fixture.ApplyAsync();
        Assert.StartsWith("OpenAI | signed in: yes", fixture.Tab.Lines[0]);
        Assert.StartsWith(removeSignIn ? "c check" : "s sign in", fixture.Tab.KeyHint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedRefreshRetainsLastObservation(bool missingTool)
    {
        using var fixture = new Fixture();
        await fixture.LoadAsync();
        fixture.Press('c');
        var check = await fixture.TakeCheckAsync();
        if (missingTool)
            check.Result.SetResult(Fixture.Connection("codex-cli", ProviderConnectionResult.ToolMissing,
                at: fixture.Clock.GetUtcNow()));
        else
            check.Result.SetException(new InvalidOperationException("sensitive diagnostic"));
        await fixture.ApplyAsync();

        Assert.Contains(missingTool ? "The provider command could not be found" : "Check failed", fixture.Tab.StatusText);
        Assert.Contains("  The provider reported no sign-in on the host.", fixture.Tab.Lines);
        Assert.Contains("confirmed: 2026-01-01 00:04 (1 min ago)", fixture.Tab.Lines[0]);
        Assert.DoesNotContain("sensitive diagnostic", string.Join("\n", fixture.Tab.Lines));
    }

    internal sealed class Fixture : IProviderConnectionsConsoleGateway, IProviderSignInLauncher, IDisposable
    {
        private readonly Channel<Action> _callbacks = Channel.CreateUnbounded<Action>();
        private readonly Channel<CheckRequest> _checks = Channel.CreateUnbounded<CheckRequest>();
        private readonly CancellationTokenSource _stop = new();
        private int _checkCalls;
        internal OwnerConsoleTestClock Clock { get; } = new();
        internal OwnerConsoleProviderConnectionsTab Tab { get; }
        internal IReadOnlyList<OwnerConsoleProviderConnection> Initial { get; set; } =
            [Connection("codex-cli"), Connection("codex-luna"), Connection("grok-cli")];
        internal IReadOnlyList<AgentDefinition> Roster { get; set; } =
            [Agent("codex-cli", AgentRole.Developer), Agent("codex-luna", AgentRole.Researcher), Agent("grok-cli", AgentRole.Reviewer)];
        internal bool RosterUnavailable { get; set; }
        internal int CheckCalls => Volatile.Read(ref _checkCalls);
        internal Task<IReadOnlyList<OwnerConsoleProviderConnection>>? PendingList { get; set; }
        internal TaskCompletionSource<bool> ListStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ProviderSignInLaunch Launch { get; set; } = new(null, ProviderSignInOutcome.LaunchFailed);
        internal Channel<(ProviderSignInTarget Target, bool DeviceCode)> Launches { get; } =
            Channel.CreateUnbounded<(ProviderSignInTarget, bool)>();
        internal TaskCompletionSource<bool>? DeviceCodeProbe { get; set; }

        internal Fixture() => Tab = new(this, this,
            () => RosterUnavailable ? throw new InvalidOperationException("fixture roster unavailable") : Roster,
            action => _callbacks.Writer.TryWrite(action), Clock, _stop.Token);

        internal static AgentDefinition Agent(string? profile, AgentRole role, AgentStatus status = AgentStatus.Available,
            string provider = "OpenAI") => new(new AgentId($"{role}-{profile}"), role.ToString(), role,
            new ModelProfile(provider, "fixture", default, default), status,
            profile is null ? AgentExecutionPolicy.ApiOnly : AgentExecutionPolicy.SubscriptionOnly,
            profile is null ? null : new SubscriptionLaunchProfile(profile));

        internal static OwnerConsoleProviderConnection Connection(string profile, ProviderConnectionResult result = ProviderConnectionResult.SignedOut,
            ProviderConnectionAuthMode mode = ProviderConnectionAuthMode.OAuth, DateTimeOffset? at = null)
        {
            var (kind, provider, command) = profile switch
            {
                "codex-cli" => (ProviderKind.OpenAICodexCli, "OpenAI", "codex"),
                "codex-luna" => (ProviderKind.OpenAICodexLuna, "OpenAI", "codex"),
                "grok-cli" => (ProviderKind.XaiGrokCli, "xAI", "grok"),
                "claude-cli" => (ProviderKind.AnthropicClaudeCli, "Anthropic", "claude"),
                _ => (ProviderKind.Unknown, "unknown", "")
            };
            return new(new(profile, provider,
            [
                new(ProviderConnectionContext.Host, ProviderConnectionSource.CodexLoginStatus, result, mode,
                    at ?? new DateTimeOffset(2026, 1, 1, 0, 4, 0, TimeSpan.Zero)),
                new(ProviderConnectionContext.WorkerSandbox, ProviderConnectionSource.RecordedInvocation,
                    ProviderConnectionResult.WorkerSucceeded, IsHistorical: true)
            ]), kind == ProviderKind.Unknown ? null : new(kind, command));
        }

        internal void Press(char key) => Tab.HandleKey(new Key(key));

        internal async Task LoadAsync(int initialChecks = 2)
        {
            Tab.Activate();
            await ApplyAsync();
            for (var index = 0; index < initialChecks; index++)
                await FinishCheckAsync(await TakeCheckAsync());
        }

        internal async Task FinishCheckAsync(CheckRequest check)
        {
            check.Result.SetResult(Initial.Single(item => item.Report.ProfileId == check.ProfileId));
            await ApplyAsync();
        }

        internal async Task ApplyAsync()
        {
            var callback = await TestHangGuard.WaitAsync(_callbacks.Reader.ReadAsync(_stop.Token).AsTask(), "provider console UI callback");
            callback();
        }

        internal Task<CheckRequest> TakeCheckAsync() =>
            TestHangGuard.WaitAsync(_checks.Reader.ReadAsync(_stop.Token).AsTask(), "provider connection check request");

        public Task<IReadOnlyList<OwnerConsoleProviderConnection>> ListAsync(CancellationToken token)
        {
            ListStarted.TrySetResult(true);
            return PendingList?.WaitAsync(token) ?? Task.FromResult(Initial);
        }

        public Task<OwnerConsoleProviderConnection> CheckAsync(string profileId, CancellationToken token)
        {
            Interlocked.Increment(ref _checkCalls);
            var completion = new TaskCompletionSource<OwnerConsoleProviderConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
            _checks.Writer.TryWrite(new(profileId, completion));
            return completion.Task.WaitAsync(token);
        }

        public ProviderSignInTarget? GetSignInTarget(string profileId) => Initial.Single(p => p.Report.ProfileId == profileId).SignIn;

        public Task<bool> SupportsDeviceCodeAsync(ProviderSignInTarget target, CancellationToken token) =>
            DeviceCodeProbe?.Task.WaitAsync(token) ?? Task.FromResult(false);

        public Task<ProviderSignInLaunch> LaunchAsync(ProviderSignInTarget target, bool deviceCode, CancellationToken token)
        {
            Launches.Writer.TryWrite((target, deviceCode));
            return Task.FromResult(Launch);
        }

        public void Dispose()
        {
            _stop.Cancel();
            _stop.Dispose();
        }

        internal sealed record CheckRequest(string ProfileId, TaskCompletionSource<OwnerConsoleProviderConnection> Result);
    }
}
