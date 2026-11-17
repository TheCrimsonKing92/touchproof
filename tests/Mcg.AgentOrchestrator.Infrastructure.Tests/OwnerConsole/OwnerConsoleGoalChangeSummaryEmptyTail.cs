using Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleGoalChangeSummaryEmptyTail : IGoalEventTail
{ public IReadOnlyList<string> ReadLast(string id, int count) => []; }
