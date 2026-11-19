using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.Input;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerConsoleProviderConnectionRows;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleProviderConnectionsTab : IOwnerConsoleSettingsTab
{
    private readonly IProviderConnectionsConsoleGateway _connections;
    private readonly OwnerConsoleProviderSignInFlow _signIn;
    private readonly Action<Action> _invoke;
    private readonly TimeProvider _clock;
    private readonly CancellationToken _lifetime;
    private List<OwnerConsoleProviderConnectionState> _profiles = [];
    private int _selected;
    private long _revision;
    private bool _listed;
    private bool _listing;
    private bool _listFailed;

    internal OwnerConsoleProviderConnectionsTab(IProviderConnectionsConsoleGateway connections,
        IProviderSignInLauncher launcher, Action<Action> invoke, TimeProvider clock, CancellationToken lifetime)
    {
        _connections = connections;
        _invoke = invoke;
        _clock = clock;
        _lifetime = lifetime;
        _signIn = new(connections, launcher, FindProfile, Check, Post, lifetime);
    }

    internal Action? Invalidate { get; set; }
    private OwnerConsoleProviderConnectionState? Selected => _profiles.Count == 0 ? null : _profiles[_selected];
    public string Title => "Provider connections";
    public bool Editing => false;
    public int SelectedLine => _profiles.Take(_selected).Sum(profile => LinesFor(profile).Count());
    public IReadOnlyList<string> Lines => _profiles.Count > 0
        ? _profiles.SelectMany(LinesFor).ToArray()
        : [_listing ? "Reading configured profiles and readiness evidence…" :
            _listed ? "No configured worker profiles." : "Provider connections have not been loaded."];
    public string StatusText => _listing ? "Reading provider connections…" :
        _listFailed ? "Could not list provider connections. Press r to retry." :
        Selected is { } profile ? CheckStatus(profile) ?? _signIn.StatusText(profile) ?? "" : "";

    public string KeyHint
    {
        get
        {
            var actions = Selected switch
            {
                { } profile when _signIn.CanReportReturn(profile) => "  t return  x stop waiting",
                { } profile when _signIn.CanSignIn(profile) =>
                    _signIn.DeviceCodeAvailable(profile) ? "  s sign in  d device code" : "  s sign in",
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
            _signIn.ProbeDeviceCode(Selected);
            return;
        }
        var action = char.ToLowerInvariant((char)key.AsRune.Value);
        if (action == 'r') { Refresh(); return; }
        if (Selected is not { } profile) return;
        switch (action)
        {
            case 'c': Check(profile); break;
            case 's': _signIn.BeginSignIn(profile, deviceCode: false); break;
            case 'd' when _signIn.DeviceCodeAvailable(profile): _signIn.BeginSignIn(profile, deviceCode: true); break;
            case 't': _signIn.ReportReturn(profile, stoppedWaiting: false); break;
            case 'x': _signIn.ReportReturn(profile, stoppedWaiting: true); break;
        }
    }

    private IEnumerable<string> LinesFor(OwnerConsoleProviderConnectionState profile)
    {
        yield return $"{ProfileId(profile.Id)} | {profile.Connection.Report.Provider}";
        foreach (var line in Details(profile.Connection, _profiles.Select(p => p.Connection), _clock.GetUtcNow()))
            yield return line;
        if (_signIn.StatusText(profile) is { } interaction) yield return $"  Sign-in: {interaction}";
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
                    if (!previous.TryGetValue(item.Report.ProfileId, out var existing)) return new OwnerConsoleProviderConnectionState(item);
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
                _signIn.ProbeDeviceCode(Selected);
            });
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { Post(() => { _listing = false; _listFailed = true; }); }
    }

    private OwnerConsoleProviderConnectionState? FindProfile(string profileId) =>
        _profiles.Find(profile => StringComparer.OrdinalIgnoreCase.Equals(profile.Id, profileId));

    private void Check(OwnerConsoleProviderConnectionState profile)
    {
        var revision = ++_revision;
        profile.CheckRevision = revision;
        profile.Checking = true;
        _ = Task.Run(() => CheckAsync(profile, revision));
    }

    private async Task CheckAsync(OwnerConsoleProviderConnectionState profile, long revision)
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
            _signIn.ProbeDeviceCode(Selected);
        });
    }

    private static ProviderConnectionObservation? ProbeFailure(OwnerConsoleProviderConnection connection) =>
        connection.Report.Observations.FirstOrDefault(o => o.ExecutionContext == ProviderConnectionContext.Host &&
            o.Result is ProviderConnectionResult.ToolMissing or ProviderConnectionResult.TimedOut or ProviderConnectionResult.OutputLimited);

    private static string? ProbeFailureText(OwnerConsoleProviderConnection connection) =>
        ProbeFailure(connection) is { } failure ? $"Check reported {failure.Result}. {failure.NextAction}" : null;

    private static string? CheckStatus(OwnerConsoleProviderConnectionState profile) =>
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
        _invoke(() =>
        {
            if (_lifetime.IsCancellationRequested) return;
            apply();
            Invalidate?.Invoke();
        });
    }
}
