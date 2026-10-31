namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>A literal, shell-free version probe and the environment inputs it depends on.</summary>
public sealed record EnvironmentToolRequirement(string Name, string Command, IReadOnlyList<string> Arguments,
    string? OutputPattern = null, IReadOnlyList<string>? Variables = null, string Source = "manifest")
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string ProbeCommand => string.Join(" ", new[] { Command }.Concat(Arguments));
    [System.Text.Json.Serialization.JsonIgnore]
    public string Rule => OutputPattern is null ? "exit=0" : $"exit=0 and output matches {OutputPattern}";
}
