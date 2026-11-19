using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.Input;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerConsoleProviderConnectionRows;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleProviderConnectionsTab : IOwnerConsoleSettingsTab
{
    private readonly IProviderConnectionsConsoleGateway _connections;
    private readonly Func<IReadOnlyList<AgentDefinition>> _loadRoster;
    private readonly OwnerConsoleProviderSignInFlow _signIn;
    private readonly Action<Action> _invoke;
    private readonly TimeProvider _clock;
    private readonly CancellationToken _lifetime;
    private List<OwnerConsoleProviderConnectionState> _profiles = [];
    private IReadOnlyList<AgentDefinition> _roster = [];
    private OwnerConsoleProviderRowModel[] _rows = [];
    private string[] _notInUse = [];
    private int _selected;
    private long _revision;
    private bool _listed;
    private bool _listing;
    private bool _initialChecksStarted;
    private string? _loadError;

    internal OwnerConsoleProviderConnectionsTab(IProviderConnectionsConsoleGateway connections,
        IProviderSignInLauncher launcher, Func<IReadOnlyList<AgentDefinition>> loadRoster, Action<Action> invoke,
        TimeProvider clock, CancellationToken lifetime)
    {
        _connections = connections;
        _loadRoster = loadRoster;
        _invoke = invoke;
        _clock = clock;
        _lifetime = lifetime;
        _signIn = new(connections, launcher, FindProfile, Check, apply => Post(apply), lifetime);
    }

    internal Action? Invalidate { get; set; }
    private OwnerConsoleProviderRowModel? Selected => _rows.Length == 0 ? null : _rows[_selected];
    public string Title => "Provider connections";
    public bool Editing => false;
    public int SelectedLine => _selected;
    public IReadOnlyList<string> Lines => RenderLines().ToArray();
    public string StatusText => _listing ? "Reading provider connections…" :
        _loadError ?? (Selected is { } row ? RowStatus(row) ?? "" : "");

    public string KeyHint
    {
        get
        {
            if (Selected is not { } row) return "r reload  Left/Right tab  Esc/q back";
            var action = PrimaryAction(row);
            var related = action switch
            {
                "t return" => "  x stop waiting",
                "s sign in" when _signIn.DeviceCodeAvailable(Representative(row)) => "  d device code",
                _ => ""
            };
            var check = action == "c check" ? "" : "  c check";
            return $"{action}{related}{check}  r reload  Up/Down provider  Left/Right tab  Esc/q back";
        }
    }

    public void Activate()
    {
        if (!_listed && _loadError is null) Refresh();
    }

    public void Refresh()
    {
        if (_listing || _lifetime.IsCancellationRequested) return;
        _listing = true;
        _loadError = null;
        var revision = _revision;
        _ = Task.Run(() => LoadAsync(revision));
    }

    public void HandleKey(Key key)
    {
        if (_lifetime.IsCancellationRequested || key.IsAlt || key.IsCtrl) return;
        if (key == Key.CursorUp || key == Key.CursorDown)
        {
            _selected = Math.Clamp(_selected + (key == Key.CursorUp ? -1 : 1), 0, Math.Max(0, _rows.Length - 1));
            ProbeSelectedDeviceCode();
            return;
        }
        var action = char.ToLowerInvariant((char)key.AsRune.Value);
        if (action == 'r') { Refresh(); return; }
        if (Selected is not { } row) return;
        var profile = Representative(row);
        switch (action)
        {
            case 'c': Check(profile); break;
            case 's': _signIn.BeginSignIn(profile, deviceCode: false); break;
            case 'd' when _signIn.DeviceCodeAvailable(profile): _signIn.BeginSignIn(profile, deviceCode: true); break;
            case 't': _signIn.ReportReturn(SignInProfile(row), stoppedWaiting: false); break;
            case 'x': _signIn.ReportReturn(SignInProfile(row), stoppedWaiting: true); break;
        }
    }

    private IEnumerable<string> RenderLines()
    {
        if (_rows.Length == 0)
            yield return _notInUse.Length > 0 ? "No provider is used by an active agent." :
                _listing ? "Reading configured profiles and readiness evidence…" :
                _loadError ?? (_listed ? "No configured worker profiles." : "Provider connections have not been loaded.");

        for (var index = 0; index < _rows.Length; index++)
        {
            var row = _rows[index];
            var action = PrimaryAction(row);
            yield return Summary(row, _clock, action);
            if (index != _selected) continue;
            foreach (var line in Details(row, action == "s sign in", action == "t return"))
                yield return line;
            if (_signIn.StatusText(SignInProfile(row)) is { } interaction) yield return $"  Sign-in: {interaction}";
            if (CheckStatus(Representative(row)) is { } check) yield return $"  {check}";
        }
        if (_notInUse.Length > 0)
            yield return $"Not in use ({_notInUse.Length}): {string.Join(", ", _notInUse.Select(ProfileId))}";
    }

    private string PrimaryAction(OwnerConsoleProviderRowModel row) =>
        _signIn.CanReportReturn(SignInProfile(row)) ? "t return" :
        _signIn.CanSignIn(Representative(row)) ? "s sign in" : "c check";

    private OwnerConsoleProviderConnectionState Representative(OwnerConsoleProviderRowModel row) =>
        FindProfile(row.Representative.Report.ProfileId)!;

    private OwnerConsoleProviderConnectionState SignInProfile(OwnerConsoleProviderRowModel row) =>
        row.Members.Select(member => FindProfile(member.Report.ProfileId)!)
            .FirstOrDefault(_signIn.HasActiveSignIn) ?? Representative(row);

    private string? RowStatus(OwnerConsoleProviderRowModel row) =>
        CheckStatus(Representative(row)) ?? _signIn.StatusText(SignInProfile(row));

    private void ProbeSelectedDeviceCode() => _signIn.ProbeDeviceCode(Selected is { } row ? Representative(row) : null);

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
        _profiles = items.Select(item =>
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
        _roster = roster;
        _listed = true;
        _listing = false;
        RebuildRows();
    }

    private void RebuildRows()
    {
        var selected = Selected?.Key;
        (_rows, _notInUse) = OwnerConsoleProviderRowModel.Build(_profiles.Select(profile => profile.Connection).ToArray(), _roster);
        _selected = selected is { } key
            ? Math.Max(0, Array.FindIndex(_rows, row => row.Key.Kind == key.Kind &&
                StringComparer.OrdinalIgnoreCase.Equals(row.Key.ProfileId, key.ProfileId)))
            : 0;
        ProbeSelectedDeviceCode();
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

    private static string? CheckStatus(OwnerConsoleProviderConnectionState profile) =>
        profile.Checking ? "Checking connection…" : profile.OperationText;

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
