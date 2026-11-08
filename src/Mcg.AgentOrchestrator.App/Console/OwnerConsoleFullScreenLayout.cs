using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleFullScreenLayout
{
    internal FrameView DecisionsFrame { get; }
    internal FrameView BoardFrame { get; }
    internal FrameView PreviewFrame { get; }
    internal FrameView ActivityFrame { get; }
    internal View BoardFooter { get; } = new() { Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1, Visible = false };
    internal IReadOnlyList<(string Text, bool Dimmed)> BoardFooterSegments { get; set; } = [];

    internal OwnerConsoleFullScreenLayout(View decisions, View decisionsEmpty, OwnerConsoleBoardPane board, View activity)
    {
        DecisionsFrame = new FrameView { Title = "DECISIONS", Y = 2, Width = Dim.Fill(), Height = Dim.Percent(20) };
        BoardFrame = new FrameView { Title = "BOARD", Y = Pos.Bottom(DecisionsFrame), Width = Dim.Fill(), Height = Dim.Percent(33) };
        PreviewFrame = new FrameView { Title = "SELECTED", Y = Pos.Bottom(BoardFrame), Width = Dim.Fill(), Height = 5 };
        ActivityFrame = new FrameView { Title = "ACTIVITY", Y = Pos.Bottom(PreviewFrame), Width = Dim.Fill(), Height = Dim.Fill(3) };
        DecisionsFrame.Add(decisions, decisionsEmpty);
        BoardFrame.Add(board, BoardFooter);
        BoardFooter.DrawingContent += (_, _) => OwnerConsoleBoardPane.DrawSegments(BoardFooter, 0, BoardFooterSegments);
        ActivityFrame.Add(activity);
        board.Style.AlwaysShowHeaders = true;
        board.Style.ShowHeaders = true;
        board.FullRowSelect = false;
    }

    internal void Compose(Window window, View status, params View[] trailing)
    {
        window.Add(new Label { Y = 1, Width = Dim.Fill(), Height = 1, Text = OwnerConsoleBindings.FieldsHint });
        window.Add(status, DecisionsFrame, BoardFrame, PreviewFrame, ActivityFrame);
        window.Add(trailing);
    }
}
