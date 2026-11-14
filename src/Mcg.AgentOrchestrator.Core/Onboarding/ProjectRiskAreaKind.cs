using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Core;

[JsonConverter(typeof(JsonStringEnumConverter<ProjectRiskAreaKind>))]
public enum ProjectRiskAreaKind
{
    [JsonStringEnumMemberName("persistence")]
    Persistence,
    [JsonStringEnumMemberName("settings")]
    Settings,
    [JsonStringEnumMemberName("publicApi")]
    PublicApi
}
