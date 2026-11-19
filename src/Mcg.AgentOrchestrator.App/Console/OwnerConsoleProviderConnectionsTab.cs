using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.Input;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerConsoleProviderConnectionRows;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleProviderConnectionsTab(IProviderConnectionsConsoleGateway connections,
    IProviderSignInLauncher launcher, Action<Action> invoke, TimeProvider clock, CancellationToken lifetime)
    : IOwnerConsoleSettingsTab
{
    private List<ProfileState> _profiles = [];
    private readonly Dictionary<ProviderSignInTarget, bool> _deviceCode = [];
    private int _selected;
    private long _revision;
    private bool _listed;
    private bool _listing;
    private bool _listFailed;

    internal Action? Invalidate { get; set; }
    private ProfileState? Selected => _profiles.Count == 0 ? null : _profiles[_selected];
    public string Title => "Provider connections";
    public bool Editing => false;
    public int SelectedLine => _profiles.Take(_selected).Sum(profile => LinesFor(profile).Count());
    public IReadOnlyList<string> Lines => _profiles.Count > 0
        ? _profiles.SelectMany(LinesFor).ToArray()
        : [_listing ? "Reading configured profiles and readiness evidence…" :
            _listed ? "No configured worker profiles." : "Provider connections have not been loaded."];
    public string StatusText => _listing ? "Reading provider connections…" :
        _listFailed ? "Could not list provider connections. Press r to retry." :
        Selected is { } profile ? CheckStatus(profile) ?? profile.InteractionText ?? "" : "";

    public string KeyHint
    {
        get
        {
            var actions = Selected switch
            {
                { SignIn: { Started: true } } => "  t return  x stop waiting",
                { SignIn: null } profile when CanSignIn(profile) =>
                    DeviceCodeAvailable(profile) ? "  s sign in  d device code" : "  s sign in",
                _ => ""
            };
            return $"Up/Down profile  c check{actions}  r reload  Left/Right tab  Esc/q back";
        }
    }

    public void Activate()
    {
        if (!_listed && !_listFailed) Refresh();
    }

    public void Refresh()
    {
        if (_listing || lifetime.IsCancellationRequested) return;
        _listing = true;
        _listFailed = false;
        var revision = _revision;
        _ = Task.Run(() => LoadAsync(revision));
    }

    public void HandleKey(Key key)
    {
        if (lifetime.IsCancellationRequested || key.IsAlt || key.IsCtrl) return;
        if (key == Key.CursorUp || key == Key.CursorDown)
        {
            _selected = Math.Clamp(_selected + (key == Key.CursorUp ? -1 : 1), 0, Math.Max(0, _profiles.Count - 1));
            ProbeDeviceCode();
            return;
        }
        var action = char.ToLowerInvariant((char)key.AsRune.Value);
        if (action == 'r') { Refresh(); return; }
        if (Selected is not { } profile) return;
        switch (action)
        {
            case 'c': Check(profile); break;
            case 's': BeginSignIn(profile, false); break;
            case 'd' when DeviceCodeAvailable(profile): BeginSignIn(profile, true); break;
            case 't' when profile.SignIn is { Started: true } returned:
                FinishSignIn(profile, returned, "Returned from native sign-in. Authentication outcome unknown.");
                break;
            case 'x' when profile.SignIn is { Started: true } waiting:
                FinishSignIn(profile, waiting, "Stopped waiting for native sign-in. The native window may still be open.");
                break;
        }
    }

    private IEnumerable<string> LinesFor(ProfileState profile)
    {
        yield return $"{ProfileId(profile.Id)} | {profile.Connection.Report.Provider}";
        foreach (var line in Details(profile.Connection, _profiles.Select(p => p.Connection), clock.GetUtcNow()))
            yield return line;
        if (profile.InteractionText is { } interaction) yield return $"  Sign-in: {interaction}";
        if (CheckStatus(profile) is { } check) yield return $"  {check}";
    }

    private async Task LoadAsync(long revision)
    {
        try
        {
            lifetime.ThrowIfCancellationRequested();
            var items = await connections.ListAsync(lifetime);
            Post(() =>
            {
                var selected = Selected?.Id;
                var previous = _profiles.ToDictionary(profile => profile.Id, StringComparer.OrdinalIgnoreCase);
                _profiles = items.Select(item =>
                {
                    if (!previous.TryGetValue(item.Report.ProfileId, out var existing)) return new ProfileState(item);
                    if (existing.LastRefresh <= revision)
                    {
                        existing.Connection = RetainHostObservation(existing.Connection, item);
                        if (ProbeFailureText(item) is { } failure) existing.OperationText = failure;
                    }
                    return existing;
                }).ToList();
                _selected = Math.Max(0, _profiles.FindIndex(profile => StringComparer.OrdinalIgnoreCase.Equals(profile.Id, selected)));
                _listed = true;
                _listing = false;
                ProbeDeviceCode();
            });
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { Post(() => { _listing = false; _listFailed = true; }); }
    }

    private void Check(ProfileState profile)
    {
        var revision = ++_revision;
        profile.CheckRevision = revision;
        profile.Checking = true;
        _ = Task.Run(() => CheckAsync(profile, revision));
    }

    private async Task CheckAsync(ProfileState profile, long revision)
    {
        OwnerConsoleProviderConnection? result = null;
        try
        {
            lifetime.ThrowIfCancellationRequested();
            result = await connections.CheckAsync(profile.Id, lifetime);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        catch (Exception) { /* The selected row retains its last observation. */ }
        Post(() =>
        {
            if (profile.CheckRevision != revision || !_profiles.Contains(profile)) return;
            profile.Checking = false;
            if (result is null || !StringComparer.OrdinalIgnoreCase.Equals(result.Report.ProfileId, profile.Id))
            {
                profile.OperationText = "Check failed. Showing the last readiness observation.";
                return;
            }
            profile.Connection = RetainHostObservation(profile.Connection, result);
            profile.OperationText = ProbeFailureText(result);
            profile.LastRefresh = ++_revision;
            ProbeDeviceCode();
        });
    }

    private static ProviderConnectionObservation? ProbeFailure(OwnerConsoleProviderConnection connection) =>
        connection.Report.Observations.FirstOrDefault(o => o.ExecutionContext == ProviderConnectionContext.Host &&
            o.Result is ProviderConnectionResult.ToolMissing or ProviderConnectionResult.TimedOut or ProviderConnectionResult.OutputLimited);

    private static string? ProbeFailureText(OwnerConsoleProviderConnection connection) =>
        ProbeFailure(connection) is { } failure ? $"Check reported {failure.Result}. {failure.NextAction}" : null;

    private static string? CheckStatus(ProfileState profile) =>
        profile.Checking ? "Checking connection…" : profile.OperationText;

    private static OwnerConsoleProviderConnection RetainHostObservation(
        OwnerConsoleProviderConnection current, OwnerConsoleProviderConnection result)
    {
        if (ProbeFailure(result) is not { } failure || ProbeFailure(current) is not null) return result;
        var previous = current.Report.Observations.FirstOrDefault(o =>
            o.ExecutionContext == ProviderConnectionContext.Host && o.ObservedAt is not null &&
            o.Result != ProviderConnectionResult.Unknown);
        return previous is null ? result : result with
        {
            Report = result.Report with { Observations = result.Report.Observations.Select(o => o == failure ? previous : o).ToArray() }
        };
    }

    private bool CanSignIn(ProfileState profile) => profile.Connection.SignIn is { } target &&
        !HasHostApiKey(profile.Connection.Report) &&
        !_profiles.Any(p => p.SignIn is { } operation && SharesCredentialSource(target, operation.Target));

    private bool DeviceCodeAvailable(ProfileState profile) => profile.Connection.SignIn is { } target &&
        _deviceCode.GetValueOrDefault(target);

    private void ProbeDeviceCode()
    {
        if (Selected is not { } profile || HasHostApiKey(profile.Connection.Report) ||
            profile.Connection.SignIn is not { CanProbeDeviceCode: true } target ||
            !_deviceCode.TryAdd(target, false)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (await launcher.SupportsDeviceCodeAsync(target, lifetime)) Post(() => _deviceCode[target] = true);
            }
            catch (Exception) { /* Inconclusive capability probes leave the device-code option hidden. */ }
        });
    }

    private void BeginSignIn(ProfileState profile, bool deviceCode)
    {
        if (!CanSignIn(profile)) return;
        var operation = new SignInOperation(profile.Connection.SignIn!);
        profile.SignIn = operation;
        profile.InteractionText = "Opening the provider's native sign-in…";
        _ = Task.Run(() => RunSignInAsync(profile, operation, deviceCode));
    }

    private async Task RunSignInAsync(ProfileState profile, SignInOperation operation, bool deviceCode)
    {
        ProviderSignInLaunch launch;
        try
        {
            lifetime.ThrowIfCancellationRequested();
            var target = connections.GetSignInTarget(profile.Id);
            launch = target is not null && target == operation.Target
                ? await launcher.LaunchAsync(target, deviceCode, lifetime)
                : new(null, ProviderSignInOutcome.Unsupported);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        catch (Exception) { launch = new(null, ProviderSignInOutcome.LaunchFailed); }

        using var session = launch.Session;
        if (session is null)
        {
            Post(() =>
            {
                if (profile.SignIn != operation) return;
                profile.SignIn = null;
                profile.InteractionText = launch.Failure switch
                {
                    ProviderSignInOutcome.ToolMissing => "The provider CLI could not be found.",
                    ProviderSignInOutcome.Unsupported => "This native sign-in option is unavailable.",
                    _ => "Could not open native sign-in."
                };
            });
            return;
        }
        Post(() =>
        {
            operation.Started = true;
            profile.InteractionText = "Native sign-in is open. Press t after returning, or x to stop waiting.";
        });
        using var observe = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        try
        {
            var exitTask = session.WaitAsync(observe.Token);
            if (await Task.WhenAny(exitTask, operation.Returned.Task) != exitTask) observe.Cancel();
            var exit = await exitTask;
            Post(() => FinishSignIn(profile, operation, exit.Outcome switch
            {
                ProviderSignInOutcome.Cancelled => "Native sign-in was cancelled.",
                ProviderSignInOutcome.Succeeded => "The provider reported successful native sign-in.",
                _ => $"Native sign-in exited ({exit.ExitCode}). Authentication outcome unknown."
            }));
        }
        catch (OperationCanceledException) when (observe.IsCancellationRequested) { }
        catch (Exception)
        {
            Post(() =>
            {
                if (profile.SignIn == operation)
                    profile.InteractionText = "Could not observe native sign-in. Press t after returning, or x to stop waiting.";
            });
        }
    }

    private void FinishSignIn(ProfileState profile, SignInOperation operation, string text)
    {
        if (profile.SignIn != operation) return;
        operation.Returned.TrySetResult();
        profile.SignIn = null;
        profile.InteractionText = text;
        if (_profiles.Contains(profile)) Check(profile);
    }

    private void Post(Action apply)
    {
        if (lifetime.IsCancellationRequested) return;
        invoke(() =>
        {
            if (lifetime.IsCancellationRequested) return;
            apply();
            Invalidate?.Invoke();
        });
    }

    private sealed class ProfileState(OwnerConsoleProviderConnection connection)
    {
        internal string Id { get; } = connection.Report.ProfileId;
        internal OwnerConsoleProviderConnection Connection { get; set; } = connection;
        internal long CheckRevision { get; set; }
        internal long LastRefresh { get; set; }
        internal bool Checking { get; set; }
        internal string? InteractionText { get; set; }
        internal string? OperationText { get; set; }
        internal SignInOperation? SignIn { get; set; }
    }

    private sealed class SignInOperation(ProviderSignInTarget target)
    {
        internal ProviderSignInTarget Target { get; } = target;
        internal bool Started { get; set; }
        internal TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
