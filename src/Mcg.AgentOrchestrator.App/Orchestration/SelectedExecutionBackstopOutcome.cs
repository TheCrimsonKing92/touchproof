namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record SelectedExecutionBackstopOutcome(string Result, IReadOnlyList<string> FailingTests,
    IReadOnlyList<string> FailedChecks, string? InfrastructureReason = null)
{
    internal static SelectedExecutionBackstopOutcome Green { get; } = new("green", [], []);
    internal static SelectedExecutionBackstopOutcome Infrastructure(string reason) => new("infrastructure-failure", [], [], reason);
}
