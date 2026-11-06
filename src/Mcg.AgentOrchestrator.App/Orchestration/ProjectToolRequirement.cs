using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.App.Orchestration;

public sealed record ProjectToolRequirement(
    string Tool,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EnvironmentVariable = null)
{
    internal static IReadOnlyList<ProjectToolRequirement> Validate(IReadOnlyList<ProjectToolRequirement> requirements)
    {
        var variables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var requirement in requirements)
        {
            if (requirement is null || string.IsNullOrWhiteSpace(requirement.Tool) ||
                requirement.Tool.IndexOfAny(['\r', '\n', '\0']) >= 0)
                throw new ArgumentException("A tool requirement must name a tool.");
            if (requirement.EnvironmentVariable is { } variable &&
                (string.IsNullOrWhiteSpace(variable) || variable.IndexOfAny(['=', '\0', '\r', '\n']) >= 0 ||
                 !variables.Add(variable)))
                throw new ArgumentException("Tool environment variables must be valid, nonblank and unique.");
        }
        return requirements.ToArray();
    }
}
