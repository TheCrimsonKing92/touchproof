using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.Input;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerConsoleProviderConnectionRows;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed partial class OwnerConsoleProviderConnectionsTab(IProviderConnectionsConsoleGateway connections,
    IProviderSignInLauncher launcher, Action<Action> invoke, TimeProvider clock, CancellationToken lifetime)
    : IOwnerConsoleSettingsTab
{
    private readonly IProviderConnectionsConsoleGateway _connections = connections;
    private readonly IProviderSignInLauncher _launcher = launcher;
    private readonly CancellationToken _lifetime = lifetime;
    private List<ProfileState> _profiles = [];
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
        Selected is { } profile ? CheckStatus(profile) ?? SignInText(profile) ?? "" : "";

    public string KeyHint
    {
        get
        {
            var actions = Selected switch
            {
                { } profile when AwaitingSignInReturn(profile) is not null => "  t return  x stop waiting",
                { } profile when CanSignIn(profile) =>
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
        if (_listing || _lifetime.IsCancellationRequested) return;
        _listing = true;
        _listFailed = false;
        var revision = _revision;
        _ = Task.Run(() => LoadAsync(revision));
    }

    public void HandleKey(Key key)
    {
        if (_lifetime.IsCancellationRequested || key.IsAlt || key.IsCtrl) return;
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
            case 't' when AwaitingSignInReturn(profile) is { } returned:
                ReportSignInReturn(profile, returned, "Returned from native sign-in. Authentication outcome unknown.");
                break;
            case 'x' when AwaitingSignInReturn(profile) is { } waiting:
                ReportSignInReturn(profile, waiting, "Stopped waiting for native sign-in. The native window may still be open.");
                break;
        }
    }

    private IEnumerable<string> LinesFor(ProfileState profile)
    {
        yield return $"{ProfileId(profile.Id)} | {profile.Connection.Report.Provider}";
        foreach (var line in Details(profile.Connection, _profiles.Select(p => p.Connection), clock.GetUtcNow()))
            yield return line;
        if (SignInText(profile) is { } interaction) yield return $"  Sign-in: {interaction}";
        if (CheckStatus(profile) is { } check) yield return $"  {check}";
    }

    private async Task LoadAsync(long revision)
    {
        try
        {
            _lifetime.ThrowIfCancellationRequested();
            var items = await _connections.ListAsync(_lifetime);
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
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
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
            _lifetime.ThrowIfCancellationRequested();
            result = await _connections.CheckAsync(profile.Id, _lifetime);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
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

    private void Post(Action apply)
    {
        if (_lifetime.IsCancellationRequested) return;
        invoke(() =>
        {
            if (_lifetime.IsCancellationRequested) return;
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
    }
}
