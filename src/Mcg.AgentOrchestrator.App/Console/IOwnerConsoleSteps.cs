namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal interface IOwnerConsoleSteps
{
    Task StartAsync(DateTimeOffset? lastActivity, CancellationToken cancellationToken);
    Task HandleEventAsync(OwnerConductEvent item, CancellationToken cancellationToken);
    Task<bool> HandleCommandAsync(string line, CancellationToken cancellationToken);
}
