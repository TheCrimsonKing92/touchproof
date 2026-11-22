using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.Input;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerConsoleProviderConnectionRows;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed partial class OwnerConsoleProviderConnectionsTab : IOwnerConsoleSettingsTab
{
    private readonly IProviderConnectionsConsoleGateway _connections;
    private readonly Func<IReadOnlyList<AgentDefinition>> _loadRoster;
    private readonly OwnerConsoleProviderSignInFlow _signIn;
    private readonly Action<Action> _invoke;
    private readonly Action<Func<Task>> _scheduleWork;
    private readonly TimeProvider _clock;
    private readonly CancellationToken _lifetime;
    private List<OwnerConsoleProviderConnectionState> _profiles = [];
    private IReadOnlyList<AgentDefinition> _roster = [];
    private OwnerConsoleProviderRowModel[] _rows = [];
    private string[] _notInUse = [];
    private int _selected;
    private bool _unusedExpanded;
    private long _revision;
    private bool _listed;
    private bool _listing;
    private bool _initialChecksStarted;
    private string? _loadError;
    private readonly OwnerConsoleSettingsView.ProviderTarget _target;

    internal OwnerConsoleProviderConnectionsTab(IProviderConnectionsConsoleGateway connections,
        IProviderSignInLauncher launcher, Func<IReadOnlyList<AgentDefinition>> loadRoster, Action<Action> invoke,
        TimeProvider clock, CancellationToken lifetime, Action<Func<Task>>? scheduleWork = null)
    {
        _connections = connections;
        _loadRoster = loadRoster;
        _invoke = invoke;
        _scheduleWork = scheduleWork ?? (work => { _ = Task.Run(work); });
        _clock = clock;
        _lifetime = lifetime;
        _signIn = new(connections, launcher, FindProfile, Check, apply => Post(apply), _scheduleWork, lifetime);
        _target = new(() => _rows, index => { _selected = index; ProbeSelectedDeviceCode(); });
    }

    internal Action? Invalidate { get; set; }
    private OwnerConsoleProviderRowModel? SelectedActiveRow => _selected < _rows.Length ? _rows[_selected] : null;
    private bool UnusedHeaderSelected => _notInUse.Length > 0 && _selected == _rows.Length;
    private string? SelectedUnusedId => _unusedExpanded && _selected > _rows.Length
        ? _notInUse[_selected - _rows.Length - 1] : null;
    private OwnerConsoleProviderConnectionState? SelectedUnusedProfile => SelectedUnusedId is { } id ? FindProfile(id) : null;
    private int SelectionCount => _rows.Length + (_notInUse.Length == 0 ? 0 : 1) +
        (_unusedExpanded ? _notInUse.Length : 0);
    internal const string TabTitle = "Provider connections";
    internal const string HelpKeys = BrowseKey + "  " + CheckKey + "  " + SignInKey + "  " + DeviceCodeKey + "  " +
        ReturnKey + "  " + StopWaitingKey + "  " + ReloadKey + "  " + ExpandUnusedKey + "  " + CollapseUnusedKey;
    public string Title => TabTitle;
    public bool Editing => false;

    internal void Target(string? provider) => _target.Set(provider, _listed && !_listing);

    public void Activate()
    {
        if (_unusedExpanded) CollapseUnused();
        if (!_listed && _loadError is null) Refresh();
    }

    private void CollapseUnused()
    {
        _unusedExpanded = false;
        _selected = Math.Min(_selected, _rows.Length);
    }

    public void Refresh()
    {
        if (_listing || _lifetime.IsCancellationRequested) return;
        _listing = true;
        _loadError = null;
        var revision = _revision;
        _scheduleWork(() => LoadAsync(revision));
    }

    public void HandleKey(Key key)
    {
        _target.Notice = null;
        if (_lifetime.IsCancellationRequested || key.IsAlt || key.IsCtrl) return;
        if (key == Key.CursorUp || key == Key.CursorDown)
        {
            _selected = Math.Clamp(_selected + (key == Key.CursorUp ? -1 : 1), 0, Math.Max(0, SelectionCount - 1));
            ProbeSelectedDeviceCode();
            return;
        }
        if (key == Key.Enter && (UnusedHeaderSelected || SelectedUnusedId is not null))
        {
            if (_unusedExpanded) CollapseUnused();
            else _unusedExpanded = true;
            return;
        }
        var action = char.ToLowerInvariant((char)key.AsRune.Value);
        if (action == 'r') { Refresh(); return; }
        var row = SelectedActiveRow;
        var profile = row is null ? SelectedUnusedProfile : Representative(row);
        if (profile is null) return;
        switch (action)
        {
            case 'c':
                Check(profile);
                if (row is null) _signIn.ProbeDeviceCode(profile);
                break;
            case 's': _signIn.BeginSignIn(profile, deviceCode: false, selectedFromUnused: row is null); break;
            case 'd' when _signIn.DeviceCodeAvailable(profile):
                _signIn.BeginSignIn(profile, deviceCode: true, selectedFromUnused: row is null);
                break;
            case 't': _signIn.ReportReturn(row is null ? profile : SignInProfile(row), stoppedWaiting: false); break;
            case 'x': _signIn.ReportReturn(row is null ? profile : SignInProfile(row), stoppedWaiting: true); break;
        }
    }

    private OwnerConsoleProviderConnectionState Representative(OwnerConsoleProviderRowModel row) =>
        FindProfile(row.Representative.Report.ProfileId)!;

    private OwnerConsoleProviderConnectionState SignInProfile(OwnerConsoleProviderRowModel row) =>
        row.Members.Select(member => FindProfile(member.Report.ProfileId)!)
            .FirstOrDefault(_signIn.HasActiveSignIn) ?? Representative(row);

    private void ProbeSelectedDeviceCode() => _signIn.ProbeDeviceCode(SelectedActiveRow is { } row ? Representative(row) : null);

    private async Task LoadAsync(long revision)
    {
        var error = "Could not list provider connections. Press r to retry.";
        try
        {
            _lifetime.ThrowIfCancellationRequested();
            var items = await _connections.ListAsync(_lifetime);
            _lifetime.ThrowIfCancellationRequested();
            error = "Could not read the agent roster. Press r to retry.";
            var roster = _loadRoster();
            Post(() => ApplyList(items, roster, revision), StartInitialChecks);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { Post(() => { _listing = false; _loadError = error; }); }
    }

    private void ApplyList(IReadOnlyList<OwnerConsoleProviderConnection> items, IReadOnlyList<AgentDefinition> roster, long revision)
    {
        var listRevision = ++_revision;
        var previous = _profiles.ToDictionary(profile => profile.Id, StringComparer.OrdinalIgnoreCase);
        var profiles = items.Select(item =>
        {
            if (!previous.TryGetValue(item.Report.ProfileId, out var existing)) return new OwnerConsoleProviderConnectionState(item);
            if (existing.LastRefresh <= revision)
            {
                existing.Connection = RetainHostObservation(existing.Connection, item);
                existing.OperationText = ProbeFailureText(item);
                existing.LastRefresh = listRevision;
            }
            return existing;
        }).ToList();
        _signIn.RevokeRemovedUnusedLaunches(id => profiles.Exists(profile => StringComparer.OrdinalIgnoreCase.Equals(profile.Id, id)));
        _profiles = profiles;
        _roster = roster;
        _listed = true;
        _listing = false;
        RebuildRows();
        _target.Apply();
    }

    private void RebuildRows()
    {
        var selected = SelectedActiveRow?.Key;
        var unusedId = SelectedUnusedId;
        var unusedSelected = UnusedHeaderSelected || unusedId is not null;
        (_rows, _notInUse) = OwnerConsoleProviderRowModel.Build(_profiles.Select(profile => profile.Connection).ToArray(), _roster);
        if (_notInUse.Length == 0) _unusedExpanded = false;
        if (unusedSelected && _notInUse.Length > 0)
        {
            var index = Array.FindIndex(_notInUse, id => StringComparer.OrdinalIgnoreCase.Equals(id, unusedId));
            _selected = _rows.Length + 1 + index; // A missing child selects the surviving header.
        }
        else
        {
            _selected = selected is { } key
                ? Math.Max(0, Array.FindIndex(_rows, row => row.Key.Kind == key.Kind &&
                    StringComparer.OrdinalIgnoreCase.Equals(row.Key.ProfileId, key.ProfileId)))
                : 0;
        }
        if (!unusedSelected) ProbeSelectedDeviceCode();
    }

    private void StartInitialChecks()
    {
        if (_initialChecksStarted || _lifetime.IsCancellationRequested) return;
        _initialChecksStarted = true;
        foreach (var row in _rows) Check(Representative(row));
        if (_rows.Length > 0) Invalidate?.Invoke();
    }

    private OwnerConsoleProviderConnectionState? FindProfile(string profileId) =>
        _profiles.Find(profile => StringComparer.OrdinalIgnoreCase.Equals(profile.Id, profileId));

    private void Check(OwnerConsoleProviderConnectionState profile)
    {
        var revision = ++_revision;
        profile.CheckRevision = revision;
        profile.Checking = true;
        _scheduleWork(() => CheckAsync(profile, revision));
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
            var current = profile.Connection;
            var reloaded = profile.LastRefresh > revision;
            if (reloaded && current.SignIn != result.SignIn) return;
            profile.OperationText = ProbeFailureText(result);
            var checkedConnection = RetainHostObservation(current, result);
            profile.Connection = reloaded ? MergeCheckAfterReload(current, checkedConnection) : checkedConnection;
            profile.LastRefresh = ++_revision;
            RebuildRows();
        });
    }

    private void Post(Action apply, Action? afterInvalidate = null)
    {
        if (_lifetime.IsCancellationRequested) return;
        _invoke(() =>
        {
            if (_lifetime.IsCancellationRequested) return;
            apply();
            Invalidate?.Invoke();
            afterInvalidate?.Invoke();
        });
    }
}
