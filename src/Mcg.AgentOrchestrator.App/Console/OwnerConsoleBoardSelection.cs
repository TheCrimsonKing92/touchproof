using System.Data;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleBoardSelection(
    IOwnerConsoleKeySurface surface, OwnerConsoleScreenController controller, OwnerConsolePreviewPane previewPane,
    OwnerConsoleBoardPane board, DataTable boardTable, OwnerConsoleInputDiagnostics? diagnostics,
    Func<OwnerConsoleTraceState> traceState, Func<string?> selectedFieldKey)
{
    internal int Index { get; private set; } = -1;
    internal string? GoalId { get; private set; }
    internal string? Project { get; private set; }
    internal bool CopyValid { get; set; } = true;
    internal OwnerConsoleBoardRow? Row => controller.Model is { } model &&
        Index >= 0 && Index < model.Board.Length ? model.Board[Index] : null;
    internal OwnerConsoleFieldSnapshot? Fields
    {
        get
        {
            if (!CopyValid || controller.Model is not { } model || Index < 0 ||
                Index >= model.Board.Length || model.Board[Index].IsDiagnostic) return null;
            var row = model.Board[Index];
            var snapshot = row.Fields();
            return previewPane.Row?.Project == row.Project && previewPane.Preview.GoalId == row.GoalId && previewPane.Preview.Summary is { } summary
                ? snapshot with { Fields = [.. snapshot.Fields, .. summary.Fields().Select(item => item with { Key = "preview/" + item.Key })] }
                : snapshot;
        }
    }

    internal void Select(int index, bool refresh = false, int direction = 0, bool allowHeader = false)
    {
        if (!refresh) CopyValid = true;
        using var trace = diagnostics?.Begin("select-board", traceState);
        if (controller.Model is not { Board.Length: > 0 } model)
        {
            Index = -1; GoalId = null; Project = null;
            if (!previewPane.Select(null)) previewPane.Render();
            return;
        }
        var selectable = allowHeader && index >= 0 && index < boardTable.Rows.Count && model.Board[index].IsDiagnostic
            ? index : OwnerConsoleBoardSection.NearestGoal(model.Board, model.BoardScope, index, direction);
        Index = selectable < 0 ? 0 : selectable;
        var row = model.Board[Index];
        GoalId = row.IsDiagnostic ? null : row.GoalId;
        Project = selectable < 0 ? null : row.Project;
        if (!previewPane.Select(row.IsForeign || row.IsDiagnostic ? null : GoalId, refresh))
            previewPane.Render();
        if (selectable < 0 || boardTable.Rows.Count == 0) return;
        if (board.Value?.SelectedCell.Y != Index)
            board.SetSelection(Math.Max(0, OwnerConsoleBoardRenderer.FieldKeys.ToList().IndexOf(selectedFieldKey() ?? "")), Index, false);
        board.EnsureCursorIsVisible();
        surface.RenderOverflow();
    }

    internal bool Refuse()
    {
        if (surface.FocusedPane != OwnerConsolePane.Board || Row is not { } row ||
            !row.IsForeign && !row.IsDiagnostic) return false;
        surface.StartAction();
        surface.ShowNotice(row.IsDiagnostic ? row.Title : OwnerConsoleBoardSource.ForeignRowMessage(row),
            OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Action);
        return true;
    }
}
