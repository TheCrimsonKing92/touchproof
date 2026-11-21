using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.App.Orchestration;

public sealed record DecisionSnapshot(
    int SchemaVersion,
    DecisionSnapshotContext EvaluationContext,
    IReadOnlyList<DecisionSnapshotGoal> Goals)
{
    public const int CurrentSchemaVersion = 1;

    internal static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        RespectRequiredConstructorParameters = true,
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static DecisionSnapshot Read(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<DecisionSnapshot>(stream, JsonOptions) ??
            throw new InvalidDataException("The decision snapshot is null.");
    }
}
