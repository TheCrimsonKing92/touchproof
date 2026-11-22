namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed partial class OwnerConsoleProviderConnectionsTab
{
    private OwnerConsoleProviderRowModel[] _rows = [];
    private string[] _notInUse = [];
    private int _selected;
    private bool _unusedExpanded;

    private OwnerConsoleProviderRowModel? SelectedActiveRow => _selected < _rows.Length ? _rows[_selected] : null;
    private bool UnusedHeaderSelected => _notInUse.Length > 0 && _selected == _rows.Length;
    private string? SelectedUnusedId => _unusedExpanded && _selected > _rows.Length
        ? _notInUse[_selected - _rows.Length - 1] : null;
    private OwnerConsoleProviderConnectionState? SelectedUnusedProfile => SelectedUnusedId is { } id ? FindProfile(id) : null;
    private int SelectionCount => _rows.Length + (_notInUse.Length == 0 ? 0 : 1) +
        (_unusedExpanded ? _notInUse.Length : 0);

    private void CollapseUnused()
    {
        _unusedExpanded = false;
        _selected = Math.Min(_selected, _rows.Length);
    }

    private void RebuildRows()
    {
        var selected = SelectedActiveRow?.Key;
        var unusedId = SelectedUnusedId;
        var unusedSelected = UnusedHeaderSelected || unusedId is not null;
        (_rows, _notInUse) = OwnerConsoleProviderRowModel.Build(_profiles.Select(profile => profile.Connection).ToArray(), _roster);
        if (_notInUse.Length == 0) _unusedExpanded = false;
        if (unusedSelected)
        {
            _selected = _notInUse.Length > 0 ? _rows.Length : 0;
            if (unusedId is null) return;

            var index = Array.FindIndex(_notInUse, id => StringComparer.OrdinalIgnoreCase.Equals(id, unusedId));
            if (index >= 0)
            {
                _selected = _rows.Length + 1 + index;
                return;
            }

            var activeIndex = Array.FindIndex(_rows, row => row.Members.Any(member =>
                StringComparer.OrdinalIgnoreCase.Equals(member.Report.ProfileId, unusedId)));
            if (activeIndex >= 0) _selected = activeIndex;
            return;
        }

        _selected = selected is { } key
            ? Math.Max(0, Array.FindIndex(_rows, row => row.Key.Kind == key.Kind &&
                StringComparer.OrdinalIgnoreCase.Equals(row.Key.ProfileId, key.ProfileId)))
            : 0;
        ProbeSelectedDeviceCode();
    }
}
