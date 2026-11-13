namespace Mcg.AgentOrchestrator.App.Orchestration;

internal interface ISelectedExecutionBackstopRunner
{
    Task<SelectedExecutionBackstopOutcome> RunAsync(SelectedExecutionBackstopRequest request, CancellationToken cancellationToken);
}
