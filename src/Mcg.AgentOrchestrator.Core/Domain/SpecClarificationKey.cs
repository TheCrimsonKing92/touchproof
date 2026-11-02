using System.Diagnostics.CodeAnalysis;

namespace Mcg.AgentOrchestrator.Core;

public static class SpecClarificationKey
{
    public const string Prefix = "spec-clarification:";

    public static string Build(GoalId goalId, string topicKey) =>
        $"{Prefix}{goalId.Value}:{topicKey}";

    public static bool IsSpecClarification([NotNullWhen(true)] string? correlationKey) =>
        correlationKey?.StartsWith(Prefix, StringComparison.Ordinal) == true;
}
