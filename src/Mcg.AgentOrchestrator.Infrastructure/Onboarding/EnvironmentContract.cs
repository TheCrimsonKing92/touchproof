using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Prerequisites stored alongside the checks they protect. Invalid declarations fail closed.</summary>
public sealed record EnvironmentContract(int Version, IReadOnlyList<EnvironmentToolRequirement> Tools)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public JsonNode ToJson() => JsonSerializer.SerializeToNode(this, JsonOptions)!;

    public static EnvironmentContract FromManifest(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("A manifest must be an object.");
        var contract = root.TryGetProperty("environmentContract", out var declared)
            ? declared.Deserialize<EnvironmentContract>(JsonOptions) ?? throw new JsonException("Null environment contract.")
            : root.TryGetProperty("checks", out var checks) && checks.ValueKind == JsonValueKind.Array
                ? EnvironmentContractDeriver.Derive(checks) : throw new JsonException("A manifest requires checks to derive an environment contract.");
        contract.Validate();
        return contract;
    }

    public static EnvironmentContract? Load(string repositoryRoot, string? projectHome = null)
    {
        var path = AcceptanceManifestLocator.Resolve(repositoryRoot, projectHome);
        return File.Exists(path) ? FromManifest(File.ReadAllText(path)) : null;
    }

    public void Validate()
    {
        if (Version != 1 || Tools is null) throw new JsonException("Environment contract requires version 1 and tools.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in Tools)
        {
            if (tool is null || string.IsNullOrWhiteSpace(tool.Name) || string.IsNullOrWhiteSpace(tool.Command) ||
                tool.Arguments is null || tool.Arguments.Any(argument => argument is null) || !names.Add(tool.Name) ||
                tool.Variables?.Any(string.IsNullOrWhiteSpace) == true)
                throw new JsonException("Environment tools require unique names, commands, arguments and valid variable names.");
            if (tool.OutputPattern is { } pattern)
                try { _ = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1)); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                { throw new JsonException($"Invalid environment output pattern for {tool.Name}.", ex); }
        }
    }
}
