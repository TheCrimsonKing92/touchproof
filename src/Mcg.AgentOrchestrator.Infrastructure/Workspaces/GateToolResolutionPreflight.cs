using System.Collections.Immutable;
using System.Diagnostics;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class GateToolResolutionPreflight
{
    private static readonly string[] RequiredTools = ["powershell", "cmd", "chcp", "git", "dotnet"];
    private static readonly AsyncLocal<GateToolResolutionResult?> AttemptResult = new();

    internal static GateToolResolutionResult? Current => AttemptResult.Value;

    internal static GateToolResolutionResult? Evaluate(
        IEnumerable<KeyValuePair<string, string?>> environment, string workingDirectory)
    {
        if (!OperatingSystem.IsWindows()) return null;

        var resolved = RequiredTools.ToDictionary(
            name => name, name => AcceptanceBareCommandResolver.Resolve(name, environment, workingDirectory));
        var path = ReadVariable(environment, "PATH") ?? string.Empty;
        var entries = path.Split(';').Select(NormalizeDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var systemRoot = ReadVariable(environment, "SystemRoot");
        if (string.IsNullOrEmpty(systemRoot)) systemRoot = ReadVariable(environment, "WINDIR");
        var systemDirectory = !string.IsNullOrWhiteSpace(systemRoot) && Path.IsPathFullyQualified(systemRoot) &&
            systemRoot.IndexOfAny(Path.GetInvalidPathChars()) < 0
            ? Path.Combine(systemRoot, "System32") : null;
        var powershellDirectory = systemDirectory is null
            ? null : Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0");

        return new(
            [.. RequiredTools.Where(name => resolved[name] is null)],
            AcceptanceBareCommandResolver.Resolve("pwsh", environment, workingDirectory) is not null,
            path.Length,
            IsOnPath(systemDirectory),
            IsOnPath(powershellDirectory),
            IsOnPath(Path.GetDirectoryName(resolved["git"])),
            IsOnPath(Path.GetDirectoryName(resolved["dotnet"])));

        bool? IsOnPath(string? directory) =>
            directory is null ? null : entries.Contains(NormalizeDirectory(directory));
    }

    internal static GateToolResolutionResult? EnsureToolsResolve(
        IEnumerable<KeyValuePair<string, string?>> environment, string workingDirectory)
    {
        var result = Evaluate(environment, workingDirectory);
        if (result is { MissingTools.IsEmpty: false })
        {
            throw new AcceptanceInfrastructureDeferredException("gate-tool-resolution-missing", null,
                $"Missing gate tools: {string.Join(", ", result.MissingTools)}. PATH length: {result.PathLength}.");
        }

        return result;
    }

    internal static IDisposable? EnterHermeticAttempt(
        string workingDirectory, Func<IDictionary<string, string?>>? createEnvironment)
    {
        if (!OperatingSystem.IsWindows()) return null;

        var environment = createEnvironment?.Invoke() ?? new ProcessStartInfo().Environment;
        AcceptanceHermeticEnvironment.ConfigureHermeticVerificationEnvironment(environment, workingDirectory);
        return EnterAttempt(EnsureToolsResolve(environment, Path.GetFullPath(workingDirectory)));
    }

    internal static IDisposable EnterAttempt(GateToolResolutionResult? result) => new AttemptScope(result);

    // A dedicated owned child runs a single attempt on this execution flow and exits.
    internal static void AdoptOwnedAttempt(GateToolResolutionResult result) => AttemptResult.Value = result;

    private static string? ReadVariable(IEnumerable<KeyValuePair<string, string?>> environment, string name) =>
        environment.LastOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private static string NormalizeDirectory(string directory)
    {
        if (directory.Length >= 2 && directory[0] == '"' && directory[^1] == '"')
            directory = directory[1..^1];
        var rootLength = Path.GetPathRoot(directory.AsSpan()).Length;
        return directory[..Math.Max(rootLength, directory.TrimEnd('\\', '/').Length)];
    }

    private sealed class AttemptScope : IDisposable
    {
        private readonly GateToolResolutionResult? _previous = Current;

        internal AttemptScope(GateToolResolutionResult? result)
        {
            AttemptResult.Value = result;
        }

        public void Dispose() => AttemptResult.Value = _previous;
    }
}

internal sealed record GateToolResolutionResult(
    ImmutableArray<string> MissingTools,
    bool PwshResolved,
    int PathLength,
    bool? SystemDirectoryOnPath,
    bool? WindowsPowerShellDirectoryOnPath,
    bool? GitDirectoryOnPath,
    bool? DotnetDirectoryOnPath);
