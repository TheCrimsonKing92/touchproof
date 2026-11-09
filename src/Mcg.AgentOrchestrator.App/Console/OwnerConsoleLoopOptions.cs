namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerConsoleLoopOptions(TimeSpan PollBound, TimeSpan OperationBound, TimeSpan ShutdownBound)
{
    internal TimeSpan BusyNoticeAfter { get; init; } = TimeSpan.FromSeconds(2);
    internal TimeSpan RefreshInterval { get; init; } = TimeSpan.FromSeconds(30);
    internal static OwnerConsoleLoopOptions Default { get; } = new(
        TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2));
}
