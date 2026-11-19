using System.Threading.Channels;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.Input;

public sealed class OwnerConsoleProviderConnectionsTabTests
{
    [Fact]
    public async Task RendersContextsAndChecksOnlySelectedProfile()
    {
        using var fixture = new Fixture();
        await fixture.LoadAsync();

        Assert.Contains("codex-cli | OpenAI", fixture.Tab.Lines);
        Assert.Contains(fixture.Tab.Lines, l => l.Contains("Host / CodexLoginStatus | OAuth | SignedOut"));
        Assert.Contains(fixture.Tab.Lines, l => l.Contains("WorkerSandbox / RecordedInvocation | Unknown | WorkerSucceeded | historical"));
        Assert.Contains(fixture.Tab.Lines, l => l.Contains("2026-01-01 00:04:00Z (60s ago)"));
        Assert.Contains(fixture.Tab.Lines, l => l.Contains("Next: Review provider sign-in"));
        Assert.Contains("  Shared native sign-in: codex-cli, codex-luna", fixture.Tab.Lines);

        fixture.Tab.HandleKey(Key.CursorDown);
        fixture.Press('c');
        var check = await fixture.TakeCheckAsync();
        Assert.Equal("codex-luna", check.ProfileId);
        Assert.Contains("Checking connection", fixture.Tab.StatusText);
        fixture.Tab.HandleKey(Key.CursorUp);
        Assert.DoesNotContain("Checking connection", fixture.Tab.StatusText);

        check.Result.SetResult(Fixture.Connection("codex-luna", ProviderConnectionResult.SignedIn));
        await fixture.ApplyAsync();
        Assert.Contains("  Host / CodexLoginStatus | OAuth | SignedOut", fixture.Tab.Lines);
        Assert.Contains("  Host / CodexLoginStatus | OAuth | SignedIn", fixture.Tab.Lines);
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

        newer.Result.SetResult(Fixture.Connection("codex-cli", ProviderConnectionResult.SignedIn));
        await fixture.ApplyAsync();
        older.Result.SetResult(Fixture.Connection("codex-cli", ProviderConnectionResult.Expired));
        await fixture.ApplyAsync();

        Assert.Contains("  Host / CodexLoginStatus | OAuth | SignedIn", fixture.Tab.Lines);
        Assert.DoesNotContain(fixture.Tab.Lines, l => l.EndsWith("Expired", StringComparison.Ordinal));
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

        Assert.Contains(missingTool ? "ToolMissing" : "Check failed", fixture.Tab.StatusText);
        Assert.Contains("  Host / CodexLoginStatus | OAuth | SignedOut", fixture.Tab.Lines);
        Assert.Contains(fixture.Tab.Lines, l => l.Contains("2026-01-01 00:04:00Z"));
        Assert.DoesNotContain("sensitive diagnostic", string.Join("\n", fixture.Tab.Lines));
    }

    internal sealed class Fixture : IProviderConnectionsConsoleGateway, IProviderSignInLauncher, IDisposable
    {
        private readonly Channel<Action> _callbacks = Channel.CreateUnbounded<Action>();
        private readonly Channel<CheckRequest> _checks = Channel.CreateUnbounded<CheckRequest>();
        private readonly CancellationTokenSource _stop = new();
        internal OwnerConsoleTestClock Clock { get; } = new();
        internal OwnerConsoleProviderConnectionsTab Tab { get; }
        internal IReadOnlyList<OwnerConsoleProviderConnection> Initial { get; set; } =
            [Connection("codex-cli"), Connection("codex-luna")];
        internal Task<IReadOnlyList<OwnerConsoleProviderConnection>>? PendingList { get; set; }
        internal ProviderSignInLaunch Launch { get; set; } = new(null, ProviderSignInOutcome.LaunchFailed);
        internal Channel<(ProviderSignInTarget Target, bool DeviceCode)> Launches { get; } =
            Channel.CreateUnbounded<(ProviderSignInTarget, bool)>();
        internal bool DeviceCode { get; set; }

        internal Fixture() => Tab = new(this, this, action => _callbacks.Writer.TryWrite(action), Clock, _stop.Token);

        internal static OwnerConsoleProviderConnection Connection(string profile, ProviderConnectionResult result = ProviderConnectionResult.SignedOut,
            ProviderConnectionAuthMode mode = ProviderConnectionAuthMode.OAuth, DateTimeOffset? at = null)
        {
            var grok = profile == "grok-cli";
            var kind = profile == "codex-luna" ? ProviderKind.OpenAICodexLuna :
                grok ? ProviderKind.XaiGrokCli : ProviderKind.OpenAICodexCli;
            return new(new(profile, grok ? "xAI" : "OpenAI",
            [
                new(ProviderConnectionContext.Host, ProviderConnectionSource.CodexLoginStatus, result, mode,
                    at ?? new DateTimeOffset(2026, 1, 1, 0, 4, 0, TimeSpan.Zero)),
                new(ProviderConnectionContext.WorkerSandbox, ProviderConnectionSource.RecordedInvocation,
                    ProviderConnectionResult.WorkerSucceeded, IsHistorical: true)
            ]), new(kind, grok ? "grok" : "codex"));
        }

        internal void Press(char key) => Tab.HandleKey(new Key(key));

        internal async Task LoadAsync()
        {
            Tab.Activate();
            await ApplyAsync();
        }

        internal async Task ApplyAsync()
        {
            var callback = await TestHangGuard.WaitAsync(_callbacks.Reader.ReadAsync(_stop.Token).AsTask(), "provider console UI callback");
            callback();
        }

        internal Task<CheckRequest> TakeCheckAsync() =>
            TestHangGuard.WaitAsync(_checks.Reader.ReadAsync(_stop.Token).AsTask(), "provider connection check request");

        public Task<IReadOnlyList<OwnerConsoleProviderConnection>> ListAsync(CancellationToken token) =>
            PendingList?.WaitAsync(token) ?? Task.FromResult(Initial);

        public Task<OwnerConsoleProviderConnection> CheckAsync(string profileId, CancellationToken token)
        {
            var completion = new TaskCompletionSource<OwnerConsoleProviderConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
            _checks.Writer.TryWrite(new(profileId, completion));
            return completion.Task.WaitAsync(token);
        }

        public ProviderSignInTarget? GetSignInTarget(string profileId) => Initial.Single(p => p.Report.ProfileId == profileId).SignIn;

        public Task<bool> SupportsDeviceCodeAsync(ProviderSignInTarget target, CancellationToken token) => Task.FromResult(DeviceCode);

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
