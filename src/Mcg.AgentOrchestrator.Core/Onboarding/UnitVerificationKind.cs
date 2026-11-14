using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Core;

[JsonConverter(typeof(JsonStringEnumConverter<UnitVerificationKind>))]
public enum UnitVerificationKind
{
    [JsonStringEnumMemberName("unitTests")]
    UnitTests,
    [JsonStringEnumMemberName("desktopUi")]
    DesktopUi,
    [JsonStringEnumMemberName("serviceHealth")]
    ServiceHealth
}
