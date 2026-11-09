namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleLoop(
    IOwnerConsoleSteps steps,
    IOwnerConsoleInput input,
    IConductEventSource events,
    IOwnerConsoleOutput output,
    TimeProvider clock,
    OwnerConsoleLoopOptions? options = null)
{
    private readonly OwnerConsoleLoopOptions _options = options ?? OwnerConsoleLoopOptions.Default;
    private readonly RefreshNoticePolicy _refreshNotices = new();
    internal bool EventSourceAbandoned { get; private set; }

    internal OwnerConsoleLoop(OwnerConsoleSession session, IOwnerConsoleInput input,
        IConductEventSource events, TimeProvider clock, OwnerConsoleLoopOptions? options = null)
        : this(new SessionSteps(session), input, events, new SystemConsoleOutput(), clock, options) { }

    internal async Task RunAsync(CancellationToken cancellationToken = default) =>
        await new OwnerConsoleLoopRun(this, steps, input, events, clock, _options, _refreshNotices)
            .RunAsync(cancellationToken);

    internal void MarkEventSourceAbandoned() => EventSourceAbandoned = true;

    internal void Announce(string? line)
    {
        if (line is not null) output.WriteLine(ConsoleAnnouncementFormatter.Format(clock, line));
    }

    internal void ReportStepError(Exception ex, bool isRefresh)
    {
        if (isRefresh) Announce(_refreshNotices.Failed(ErrorMessage(ex)));
        else ReportError(ex);
    }

    internal void ReportError(Exception ex) => Announce($"error: {ErrorMessage(ex)}");

    private static string ErrorMessage(Exception ex) =>
        ex.GetBaseException().Message.Replace('\r', ' ').Replace('\n', ' ');

    internal static bool PrintsBoard(string kind) =>
        kind is "watch-transition" or "acceptance" or "loop-relaunch" or "goal-escalation";

    private sealed class SessionSteps(OwnerConsoleSession session) : IOwnerConsoleSteps
    {
        public Task StartAsync(DateTimeOffset? lastActivity, CancellationToken cancellationToken) =>
            session.StartAsync(lastActivity, cancellationToken);
        public Task HandleEventAsync(OwnerConductEvent item, CancellationToken cancellationToken) =>
            session.HandleEventAsync(item, cancellationToken);
        public Task<bool> HandleCommandAsync(string line, CancellationToken cancellationToken) =>
            session.HandleCommandAsync(line, cancellationToken);
    }
}
