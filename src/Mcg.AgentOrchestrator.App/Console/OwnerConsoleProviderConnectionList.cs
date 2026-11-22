using Mcg.AgentOrchestrator.Core;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerConsoleProviderConnectionRows;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleProviderConnectionList(
    Func<string, OwnerConsoleProviderConnectionState?> findProfile, OwnerConsoleProviderSignInFlow signIn, TimeProvider clock)
{
    private string[] _notInUse = [];
    private int _selected;
    private bool _unusedExpanded;

    internal OwnerConsoleProviderRowModel[] ActiveRows { get; private set; } = [];
    internal OwnerConsoleProviderRowModel? SelectedActiveRow => _selected < ActiveRows.Length ? ActiveRows[_selected] : null;
    internal OwnerConsoleProviderConnectionState? SelectedUnusedProfile => SelectedUnusedId is { } id ? findProfile(id) : null;
    internal bool HasUnusedSelection => UnusedHeaderSelected || SelectedUnusedId is not null;
    private bool UnusedHeaderSelected => _notInUse.Length > 0 && _selected == ActiveRows.Length;
    private string? SelectedUnusedId => _unusedExpanded && _selected > ActiveRows.Length
        ? _notInUse[_selected - ActiveRows.Length - 1] : null;
    private int SelectionCount => ActiveRows.Length + (_notInUse.Length == 0 ? 0 : 1) +
        (_unusedExpanded ? _notInUse.Length : 0);

    internal void SelectActiveRow(int index) => _selected = index;

    internal void Move(int step) => _selected = Math.Clamp(_selected + step, 0, Math.Max(0, SelectionCount - 1));

    internal bool ToggleUnused()
    {
        if (!HasUnusedSelection) return false;
        if (_unusedExpanded) CollapseUnused();
        else _unusedExpanded = true;
        return true;
    }

    internal void CollapseUnused()
    {
        if (!_unusedExpanded) return;
        _unusedExpanded = false;
        _selected = Math.Min(_selected, ActiveRows.Length);
    }

    internal void Rebuild(IReadOnlyList<OwnerConsoleProviderConnection> profiles, IReadOnlyList<AgentDefinition> roster)
    {
        var selected = SelectedActiveRow?.Key;
        var unusedId = SelectedUnusedId;
        var unusedSelected = UnusedHeaderSelected || unusedId is not null;
        (ActiveRows, _notInUse) = OwnerConsoleProviderRowModel.Build(profiles, roster);
        if (_notInUse.Length == 0) _unusedExpanded = false;
        if (unusedSelected)
        {
            _selected = _notInUse.Length > 0 ? ActiveRows.Length : 0;
            if (unusedId is null) return;

            var index = Array.FindIndex(_notInUse, id => StringComparer.OrdinalIgnoreCase.Equals(id, unusedId));
            if (index >= 0)
            {
                _selected = ActiveRows.Length + 1 + index;
                return;
            }

            var activeIndex = Array.FindIndex(ActiveRows, row => row.Members.Any(member =>
                StringComparer.OrdinalIgnoreCase.Equals(member.Report.ProfileId, unusedId)));
            if (activeIndex >= 0) _selected = activeIndex;
            return;
        }

        _selected = selected is { } key
            ? Math.Max(0, Array.FindIndex(ActiveRows, row => row.Key.Kind == key.Kind &&
                StringComparer.OrdinalIgnoreCase.Equals(row.Key.ProfileId, key.ProfileId)))
            : 0;
    }

    internal OwnerConsoleProviderConnectionState Representative(OwnerConsoleProviderRowModel row) =>
        findProfile(row.Representative.Report.ProfileId)!;

    internal OwnerConsoleProviderConnectionState SignInProfile(OwnerConsoleProviderRowModel row) =>
        row.Members.Select(member => findProfile(member.Report.ProfileId)!)
            .FirstOrDefault(signIn.HasActiveSignIn) ?? Representative(row);

    internal int SelectedLine => _selected + (ActiveRows.Length == 0 && _notInUse.Length > 0 ? 1 : 0);
    internal string? SelectionStatus => SelectedActiveRow is { } row
        ? CheckStatus(Representative(row)) ?? signIn.StatusText(SignInProfile(row))
        : SelectedUnusedProfile is { } profile ? CheckStatus(profile) ?? signIn.StatusText(profile) : null;

    internal string KeyHint
    {
        get
        {
            if (UnusedHeaderSelected) return FormatUnusedHeaderHint(_unusedExpanded);
            if (SelectedUnusedProfile is { } unused)
                return FormatKeyHint(PrimaryAction(unused, unused), signIn.DeviceCodeAvailable(unused), unused: true);
            if (SelectedActiveRow is not { } row) return FormatKeyHint(null, deviceCodeAvailable: false);
            return FormatKeyHint(PrimaryAction(Representative(row), SignInProfile(row)),
                signIn.DeviceCodeAvailable(Representative(row)));
        }
    }

    internal IEnumerable<string> RenderLines(string emptyMessage)
    {
        if (ActiveRows.Length == 0)
            yield return _notInUse.Length > 0 ? "No provider is used by an active agent." : emptyMessage;

        for (var index = 0; index < ActiveRows.Length; index++)
        {
            var row = ActiveRows[index];
            var profile = Representative(row);
            var signInProfile = SignInProfile(row);
            var action = PrimaryAction(profile, signInProfile);
            yield return Summary(row, clock, action);
            if (index != _selected) continue;
            foreach (var line in Details(row, action == SignInKey, action == ReturnKey))
                yield return line;
            foreach (var line in OperationDetails(profile, signInProfile)) yield return line;
        }
        if (_notInUse.Length == 0) yield break;
        yield return $"Not in use ({_notInUse.Length}) {(_unusedExpanded ? "[-]" : "[+]")}";
        if (!_unusedExpanded) yield break;
        for (var index = 0; index < _notInUse.Length; index++)
        {
            var profile = findProfile(_notInUse[index])!;
            var action = PrimaryAction(profile, profile);
            var evidence = OwnerConsoleProviderRowModel.SelectSignInEvidence(profile.Connection.Report.Observations);
            yield return UnusedSummary(profile.Connection, evidence, clock, action);
            if (ActiveRows.Length + 1 + index != _selected) continue;
            foreach (var line in UnusedDetails(profile.Connection, evidence, action == SignInKey, action == ReturnKey))
                yield return line;
            foreach (var line in OperationDetails(profile, profile)) yield return line;
        }
    }

    private IEnumerable<string> OperationDetails(OwnerConsoleProviderConnectionState profile, OwnerConsoleProviderConnectionState signInProfile)
    {
        if (signIn.StatusText(signInProfile) is { } interaction) yield return $"  Sign-in: {interaction}";
        if (CheckStatus(profile) is { } check) yield return $"  {check}";
    }

    private string PrimaryAction(OwnerConsoleProviderConnectionState profile, OwnerConsoleProviderConnectionState signInProfile) =>
        signIn.CanReportReturn(signInProfile) ? ReturnKey : signIn.CanSignIn(profile) ? SignInKey : CheckKey;

    private static string? CheckStatus(OwnerConsoleProviderConnectionState profile) =>
        profile.Checking ? "Checking connection…" : profile.OperationText;
}
