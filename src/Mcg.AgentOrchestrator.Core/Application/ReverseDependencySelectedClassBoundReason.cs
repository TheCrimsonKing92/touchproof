namespace Mcg.AgentOrchestrator.Core;

internal static class ReverseDependencySelectedClassBoundReason
{
    internal static string Render(string subject, int count, int cap) =>
        $"{subject} selection of {count} test classes exceeded the {cap}-test-class bound.";
}
