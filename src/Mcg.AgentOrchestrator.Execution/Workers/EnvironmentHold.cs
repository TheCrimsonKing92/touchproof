using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Operator-owned prerequisite failure. It never creates worker work or an owner question.</summary>
public sealed record EnvironmentHold(string Project, string Tool, string Command, string Rule, int? ExitCode,
    string OutputTail, string Fingerprint, string Outcome)
{
    public const string State = "environment-hold";
    public string Identity => $"{State}:{Project}:{Tool}";
    public string Format() => JsonSerializer.Serialize(this);
    public static EnvironmentHold? TryRead(Goal goal)
    {
        if (goal.CurrentHold is not { State: State } hold) return null;
        try { return JsonSerializer.Deserialize<EnvironmentHold>(hold.Blocker); }
        catch (JsonException) { return null; }
    }
}
