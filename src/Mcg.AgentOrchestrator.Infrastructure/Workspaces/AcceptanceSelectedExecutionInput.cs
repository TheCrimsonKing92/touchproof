using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record AcceptanceSelectedExecutionInput(ReverseDependencyTestSelection Selection,
    IReadOnlyList<AcceptanceTestClassDescriptor> Classes, IReadOnlyList<string> GuardClasses);
