namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record AcceptanceWholeProjectCheckInventory(string CheckName, string Project,
    IReadOnlyList<AcceptanceTestClassDescriptor> Classes);
