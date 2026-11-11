using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class WorkerManifestTestCheckReader
{
    internal static ManifestDeclaredTestCheckSet? Read(string? manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
            return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var declared = new List<ManifestDeclaredTestCheck>();
            var otherKinds = new List<string>();
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("checks", out var checks) &&
                checks.ValueKind == JsonValueKind.Array)
            {
                foreach (var check in checks.EnumerateArray())
                {
                    if (check.ValueKind != JsonValueKind.Object ||
                        !check.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                        continue;
                    var kind = ManifestTestCheckKinds.Canonicalize(type.GetString());
                    if (kind is null)
                    {
                        otherKinds.Add(type.GetString()!);
                        continue;
                    }

                    string? project = null;
                    if (check.TryGetProperty("project", out var projectElement))
                    {
                        if (projectElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                            continue;
                        project = projectElement.GetString();
                    }
                    if (kind == ManifestTestCheckKinds.Dotnet && string.IsNullOrWhiteSpace(project))
                        continue;

                    var arguments = new List<string>();
                    if (check.TryGetProperty("arguments", out var argumentElement) &&
                        argumentElement.ValueKind != JsonValueKind.Null)
                    {
                        if (argumentElement.ValueKind != JsonValueKind.Array ||
                            argumentElement.EnumerateArray().Any(argument => argument.ValueKind != JsonValueKind.String))
                            continue;
                        arguments.AddRange(argumentElement.EnumerateArray().Select(argument => argument.GetString()!));
                    }

                    var name = check.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                        ? nameElement.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name))
                        name = kind == ManifestTestCheckKinds.Dotnet ? project! :
                            $"{kind} {(string.IsNullOrWhiteSpace(project) ? "." : project)}";
                    declared.Add(new(name, kind, project, arguments));
                }
            }
            return new(declared, otherKinds);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
