using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleEpicDialogRenderer
{
    internal static (IReadOnlyList<string> Lines, IReadOnlyDictionary<int, OwnerConsoleFieldSnapshot> FieldRows,
        int Scroll, string Title) Format(Label header, Label hint, ListView body, OwnerConsoleListRenderer source,
        string headerText, string hintText, OwnerConsoleEpicViewModel? epicModel, OwnerConsoleEpicLoadState loadState,
        bool showingDetail, string? loadingDetailTitle, OwnerConsoleEpicWindow timeWindow, string? failure,
        string? selectedEpicId, int selectedIndex, int scroll, bool select, bool refreshing, int frameWidth)
    {
        var width = OwnerConsoleEpicFormatter.Width(body.Viewport.Width);
        var headerLines = OwnerConsoleEpicFormatter.Wrap(headerText, width).ToArray();
        header.Text = string.Join("\n", headerLines);
        header.Height = headerLines.Length;
        body.Y = Pos.Bottom(header);
        var hintLines = OwnerConsoleEpicFormatter.Wrap(hintText, width).ToArray();
        hint.Text = string.Join("\n", hintLines);
        hint.Y = Pos.AnchorEnd(hintLines.Length + 1);
        hint.Height = hintLines.Length;
        body.Height = Dim.Fill(hintLines.Length + 1);
        var fieldRows = new Dictionary<int, OwnerConsoleFieldSnapshot>();
        IReadOnlyList<string> lines = epicModel is not null ? (epicModel.SelectedDetail is { } retainedDetail
            ? OwnerConsoleEpicFormatter.DetailLines(retainedDetail, width, fieldRows)
            : OwnerConsoleEpicFormatter.ListLines(epicModel, width, selectedEpicId, fieldRows))
            : loadState switch
        {
            OwnerConsoleEpicLoadState.Loading => OwnerConsoleEpicFormatter.Wrap(
                showingDetail && loadingDetailTitle is { } title ? OwnerConsoleEpicFormatter.LoadingDetailLine(title) :
                    OwnerConsoleEpicFormatter.LoadingLine(timeWindow), width).ToArray(),
            OwnerConsoleEpicLoadState.Failed => OwnerConsoleEpicFormatter.Wrap(OwnerConsoleEpicFormatter.FailedLine(failure!), width).ToArray(),
            OwnerConsoleEpicLoadState.Unavailable => OwnerConsoleEpicFormatter.Wrap(OwnerConsoleEpicFormatter.UnavailableText, width).ToArray(),
            _ => showingDetail && epicModel!.SelectedDetail is { } detail
                ? OwnerConsoleEpicFormatter.DetailLines(detail, width)
                : OwnerConsoleEpicFormatter.ListLines(epicModel!, width, selectedEpicId)
        };
        scroll = Math.Clamp(scroll, 0, Math.Max(0, lines.Count - 1));
        var changed = source.Apply(body, lines);
        var row = showingDetail ? scroll : epicModel?.Epics.Take(selectedIndex)
            .Sum(epic => 2 + OwnerConsoleEpicFormatter.Wrap(OwnerConsoleEpicFormatter.Summary(epic), width).Count() +
                (epic.Plan is null ? 0 : OwnerConsoleEpicFormatter.Wrap(epic.Outcome.Render(), width).Count())) ?? 0;
        if (!showingDetail && epicModel is { } model && OwnerConsoleEpicFormatter.HasNoActivity(model))
            row += OwnerConsoleEpicFormatter.Wrap(OwnerConsoleEpicFormatter.NoActivityLine(timeWindow), width).Count();
        if (changed || select)
        {
            var selected = lines.Count == 0 ? (int?)null : Math.Clamp(row, 0, lines.Count - 1);
            if (changed || body.SelectedItem != selected) body.SelectedItem = selected;
            // Scrolling can raise ViewportChanged; suppress reentrant formatting here.
            body.EnsureSelectedItemVisible();
        }
        var renderedTitle = RenderTitle(epicModel, loadState, failure, refreshing, lines, body, frameWidth);
        return (lines, fieldRows, scroll, renderedTitle);
    }

    private static string RenderTitle(OwnerConsoleEpicViewModel? epicModel, OwnerConsoleEpicLoadState loadState,
        string? failure, bool refreshing, IReadOnlyList<string> lines, ListView body, int frameWidth)
    {
        var status = epicModel is null ? null : loadState == OwnerConsoleEpicLoadState.Failed
            ? "refresh failed: " + failure : loadState == OwnerConsoleEpicLoadState.Unavailable
            ? "refresh failed: " + OwnerConsoleEpicFormatter.UnavailableText
            : loadState == OwnerConsoleEpicLoadState.Loading || refreshing ? "refreshing" : null;
        var title = OwnerConsoleOverflow.Title("Epics" + (status is null ? "" : $" ({status})"),
            OwnerConsoleOverflow.Of(lines.Count, body.Viewport.Y, body.Viewport.Height),
            frameWidth - OwnerConsoleOverflow.TitleChrome);
        return title;
    }
}
