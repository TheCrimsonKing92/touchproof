using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class ProjectStackResolver
{
    public static Toolchain Resolve(string workingDirectory, string? projectHomeDirectory)
        => Resolve(workingDirectory, projectHomeDirectory, CrosswalkShadowDetect.Shared);

    internal static Toolchain Resolve(string workingDirectory, string? projectHomeDirectory, CrosswalkShadowDetect? shadow)
    {
        if (ReadDeclaredStack(projectHomeDirectory) is { } stack)
            return stack switch
            {
                ProjectStack.Go => Toolchain.Go,
                ProjectStack.Node => Toolchain.Node,
                ProjectStack.Dotnet => Toolchain.Dotnet,
                ProjectStack.Lua => Toolchain.Unknown,
                ProjectStack.Declared => Toolchain.Unknown,
                ProjectStack.Crosswalk => Toolchain.Unknown,
                _ => throw new InvalidOperationException($"No toolchain mapping for declared stack {stack}.")
            };

        var detected = TargetToolchainDetector.Detect(workingDirectory);
        return shadow?.Observe(workingDirectory, projectHomeDirectory, detected) ?? detected;
    }

    private static ProjectStack? ReadDeclaredStack(string? projectHomeDirectory)
    {
        if (!string.IsNullOrWhiteSpace(projectHomeDirectory))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectHomeDirectory, "project-model.json")));
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("stack", out var declaration) &&
                    declaration.ValueKind == JsonValueKind.String &&
                    !int.TryParse(declaration.GetString(), out _))
                {
                    // Read only an explicit declaration: ProjectModel's default is not evidence.
                    var stack = declaration.Deserialize<ProjectStack>();
                    if (Enum.IsDefined(stack))
                        return stack;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                JsonException or ArgumentException or NotSupportedException)
            {
                // An unavailable or unusable declaration leaves marker detection authoritative.
            }
        }

        return null;
    }

    internal static ProjectStack? GuidanceStack(Toolchain toolchain, bool isOrchestratorHome) => toolchain switch
    {
        Toolchain.Dotnet => ProjectStack.Dotnet,
        Toolchain.Go => ProjectStack.Go,
        _ => isOrchestratorHome ? ProjectStack.Dotnet : null
    };

    internal static ProjectStack? GuidanceStackFor(string workingDirectory, WorkerTargetHome? targetHome)
    {
        var home = targetHome ?? WorkerTargetHome.Home;
        // Toolchain.Unknown deliberately includes declared projects; retain their identity for brief guidance.
        if (ReadDeclaredStack(home.ProjectHomeDirectory) == ProjectStack.Declared)
            return ProjectStack.Declared;
        return GuidanceStack(Resolve(workingDirectory, home.ProjectHomeDirectory), home.IsOrchestratorHome);
    }

    internal static IReadOnlyList<string?> TestCheckCommandsFor(string workingDirectory, WorkerTargetHome? targetHome,
        ProjectStack? stack = null)
    {
        stack ??= ReadDeclaredStack(targetHome?.ProjectHomeDirectory);
        if (stack != ProjectStack.Declared) return [];
        try
        {
            var path = WorkerManifestTestProjectReader.ResolvePath(workingDirectory, targetHome ?? WorkerTargetHome.Home);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("checks", out var checks) || checks.ValueKind != JsonValueKind.Array)
                return [];

            return checks.EnumerateArray()
                .Where(check => check.ValueKind == JsonValueKind.Object &&
                    check.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                    type.GetString() == "junit-command")
                .Select(check => check.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.String
                    ? command.GetString() : null)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            JsonException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }
}
