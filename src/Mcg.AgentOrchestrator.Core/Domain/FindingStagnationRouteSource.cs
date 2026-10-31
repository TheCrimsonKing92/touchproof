using System.Text.Json;

namespace Mcg.AgentOrchestrator.Core;

/// <summary>The report owning a proposed finding retry, independently of its feedback wording.</summary>
public sealed record FindingStagnationRouteSource(string StableId, string RoundKey, AgentRole Role, DateTimeOffset ResetAt)
{
    private const string Prefix = "finding-stagnation-source: ";

    public string ToTimelineMessage() => Prefix + JsonSerializer.Serialize(this);

    public static FindingStagnationRouteSource? Read(ProgressEvent evt)
    {
        if (evt.Kind != ProgressKind.TaskNote || !evt.Message.StartsWith(Prefix, StringComparison.Ordinal))
            return null;
        var source = JsonSerializer.Deserialize<FindingStagnationRouteSource>(evt.Message[Prefix.Length..]);
        if (source is null || string.IsNullOrWhiteSpace(source.StableId) || string.IsNullOrWhiteSpace(source.RoundKey) ||
            source.Role is not (AgentRole.Tester or AgentRole.Reviewer))
            throw new InvalidOperationException("Stored finding stagnation source is invalid.");
        return source;
    }
}
