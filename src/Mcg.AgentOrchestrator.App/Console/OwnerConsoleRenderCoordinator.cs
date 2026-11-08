using System.Data;
using Terminal.Gui.App;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleRenderCoordinator
{
    private readonly OwnerConsoleFullScreenLayout _layout;
    private readonly OwnerConsoleDecisionsPane _decisionsPane;
    private readonly ListView _decisions;
    private readonly OwnerConsoleBoardPane _board;
    private readonly OwnerConsoleActivityPane _activityPane;
    private readonly ListView _activity;
    private readonly OwnerConsoleScreenController _controller;
    private readonly Label _notice;
    private readonly Label _status;
    private readonly OwnerStatusPresenter _statusPresenter;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _refreshInterval;
    private readonly IApplication _app;
    private readonly OwnerConsoleNoticeExpiry _noticeExpiry;
    private readonly CancellationToken _token;
    private readonly Func<DateTimeOffset?> _refreshedAt;
    private readonly Func<IReadOnlyCollection<string>> _working;
    private DataTable BoardTable { get; }
    private OwnerConsoleNoticeStrip NoticeStrip { get; }
    private IReadOnlyList<string> ActivityLines => _activityPane.Lines;

    internal OwnerConsoleRenderCoordinator(OwnerConsoleFullScreenLayout layout,
        OwnerConsoleDecisionsPane decisionsPane, OwnerConsoleBoardPane board, OwnerConsoleActivityPane activityPane,
        DataTable boardTable, OwnerConsoleScreenController controller, OwnerConsoleNoticeStrip noticeStrip,
        Label notice, Label status, OwnerStatusPresenter statusPresenter, TimeProvider clock, TimeSpan refreshInterval,
        IApplication app, OwnerConsoleNoticeExpiry noticeExpiry, CancellationToken token,
        Func<DateTimeOffset?> refreshedAt, Func<IReadOnlyCollection<string>> working)
    {
        _layout = layout;
        _decisionsPane = decisionsPane;
        _decisions = decisionsPane.List;
        _board = board;
        _activityPane = activityPane;
        _activity = activityPane.List;
        BoardTable = boardTable;
        _controller = controller;
        NoticeStrip = noticeStrip;
        _notice = notice;
        _status = status;
        _statusPresenter = statusPresenter;
        _clock = clock;
        _refreshInterval = refreshInterval;
        _app = app;
        _noticeExpiry = noticeExpiry;
        _token = token;
        _refreshedAt = refreshedAt;
        _working = working;
    }

    internal int BoardBodyRows() => _board.Viewport.Height - 1 -
        (_board.Style.ShowHorizontalHeaderOverline ? 1 : 0) -
        (_board.Style.ShowHorizontalHeaderUnderline ? 1 : 0);

    internal void RenderOverflow()
    {
        var model = _controller.Model;
        SetTitle(_layout.DecisionsFrame, "DECISIONS" + (_decisionsPane.TitleStatus is { } status ? $" ({status})" : ""), OwnerConsoleOverflow.Of(
            _decisions.Source?.Count ?? 0, _decisions.Viewport.Y, _decisions.Viewport.Height));
        SetTitle(_layout.BoardFrame, "BOARD", OwnerConsoleOverflow.Of(
            BoardTable.Rows.Count, _board.RowOffset, BoardBodyRows()));
        var activityTitle = OwnerConsoleActivityTitle.Title(OwnerConsoleOverflow.Of(
            ActivityLines.Count, _activity.Viewport.Y, _activity.Viewport.Height),
            _activityPane.Following, _layout.ActivityFrame.Frame.Width - OwnerConsoleOverflow.TitleChrome,
            _activityPane.Filter, _activityPane.TitleStatus);
        if (_layout.ActivityFrame.Title != activityTitle) _layout.ActivityFrame.Title = activityTitle;

        static void SetTitle(FrameView frame, string baseTitle, OwnerConsoleOverflow overflow)
        {
            var title = OwnerConsoleOverflow.Title(baseTitle, overflow, frame.Frame.Width - OwnerConsoleOverflow.TitleChrome);
            if (frame.Title != title) frame.Title = title;
        }
    }

    internal void ShowNotice(string message, OwnerConsoleNoticeSeverity severity, OwnerConsoleNoticeSource source)
    {
        NoticeStrip.Raise(message, severity, source);
        RenderNotices();
    }

    internal void RenderNotices()
    {
        if (_token.IsCancellationRequested) return;
        _notice.Text = NoticeStrip.Format(_notice.Viewport.Width);
        // An uninitialized real application has no UI dispatcher. Injected clocks let
        // headless callers drive callbacks themselves rather than mutating UI on a timer thread.
        if (!_app.Initialized && ReferenceEquals(_clock, TimeProvider.System)) return;
        _noticeExpiry.Rearm(NoticeStrip.UntilNextSuccessExpiry);
    }

    internal void RenderStatus()
    {
        if (_controller.Model is not { } model) return;
        _status.Text = _statusPresenter.Compose(model.Status, model.Gate, _controller.BellStatusText,
            _refreshedAt(), _clock.GetUtcNow(), _clock.LocalTimeZone, _refreshInterval, _working(), _status.Viewport.Width);
    }

    internal void RefreshStatus() { RenderStatus(); RenderNotices(); }
}
