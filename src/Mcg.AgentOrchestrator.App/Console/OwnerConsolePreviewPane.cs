using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsolePreviewPane : IDisposable
{
    private readonly FrameView _frame;
    private readonly TimeProvider _clock;
    private readonly Func<OwnerConsoleBoardRow?> _selectedRow;
    private readonly Func<string?, OwnerConsoleBoardRow?> _rowFor;
    private readonly OwnerConsoleSelectedGoalSummary _summary;
    private readonly OwnerConsoleLastGood<string, (OwnerConsoleSelectedGoalPreview Preview, OwnerConsoleBoardRow? Row)> _lastGood = new();
    private string? _status;

    internal Label Body { get; } = new() { Width = Dim.Fill(), Height = 3 };
    internal OwnerConsoleSelectedGoalPreview Preview { get; private set; } = new(null, null);
    internal OwnerConsoleBoardRow? Row { get; private set; }
    internal Task LastLoad => _summary.LastLoad;

    internal OwnerConsolePreviewPane(FrameView frame, TimeProvider clock,
        Func<string, CancellationToken, Task<OwnerGoalChangeSummary?>> load, Action<Action> invoke,
        Func<OwnerConsoleBoardRow?> selectedRow, Func<string?, OwnerConsoleBoardRow?> rowFor, CancellationToken token)
    {
        _frame = frame;
        _clock = clock;
        _selectedRow = selectedRow;
        _rowFor = rowFor;
        _summary = new(load, invoke, Apply, token);
        frame.Add(Body);
    }

    internal bool Select(string? id, bool refresh = false) => _summary.Select(id, refresh);

    internal void Apply(OwnerConsoleSelectedGoalPreview preview)
    {
        _status = null;
        Row = _rowFor(preview.GoalId);
        if (preview.GoalId is { } id)
        {
            if (preview.Unavailable is null && preview.Summary?.Preview is not null)
                _lastGood.Remember(id, (preview, Row));
            else if (_lastGood.TryGet(id, out var prior))
            {
                _status = preview.Unavailable is { } error ? "refresh failed: " + error :
                    preview.Summary is not null ? "refresh failed: could not load the goal" : "refreshing";
                preview = prior.Preview;
                Row = prior.Row;
            }
        }
        Preview = preview;
        Render();
    }

    internal void Render()
    {
        if (_selectedRow() is { } selected && (selected.IsForeign || selected.IsDiagnostic))
        {
            var foreignTitle = OwnerConsoleSelectedPreview.Title(selected.IsDiagnostic ? null : selected.GoalId);
            if (_frame.Title != foreignTitle) _frame.Title = foreignTitle;
            var foreignText = selected.IsDiagnostic ? selected.Title : OwnerConsoleBoardSource.ForeignRowMessage(selected);
            if (Body.Text != foreignText) Body.Text = foreignText;
            return;
        }
        var title = OwnerConsoleSelectedPreview.Title(Preview.GoalId) +
            (_status is null ? "" : $" ({_status})");
        if (_frame.Title != title) _frame.Title = title;
        var row = Row;
        var text = string.Join('\n', OwnerConsoleSelectedPreview.Lines(Preview, row?.Stage,
            _clock.LocalTimeZone, Body.Viewport.Width, row));
        if (Body.Text != text) Body.Text = text;
    }

    public void Dispose() => _summary.Dispose();
}
