using System.Collections.Immutable;
using System.Diagnostics;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class GateToolResolutionPreflight
{
    internal const string MissingToolsReasonCode = "gate-tool-resolution-missing";
    private static readonly string[] RequiredTools = ["powershell", "cmd", "chcp", "git", "dotnet"];
    private static readonly AsyncLocal<AttemptContext?> Attempt = new();

    internal static GateToolResolutionResult? Current => Attempt.Value?.Result;

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
            throw new AcceptanceInfrastructureDeferredException(MissingToolsReasonCode, null,
                $"Missing gate tools: {string.Join(", ", result.MissingTools)}. PATH length: {result.PathLength}.");
        }

        return result;
    }

    internal static IDisposable? EnterHermeticAttempt(
        string workingDirectory, Func<IDictionary<string, string?>>? createEnvironment)
    {
        if (!OperatingSystem.IsWindows()) return null;

        workingDirectory = Path.GetFullPath(workingDirectory);
        var environment = createEnvironment?.Invoke() ?? new ProcessStartInfo().Environment;
        AcceptanceHermeticEnvironment.ConfigureHermeticVerificationEnvironment(environment, workingDirectory);
        return new AttemptScope(new(createEnvironment, workingDirectory, EnsureToolsResolve(environment, workingDirectory)));
    }

    // A focused arm supplies its working directory once its checkout is ready.
    internal static IDisposable? BeginFocusedAttempt(Func<IDictionary<string, string?>>? createEnvironment) =>
        OperatingSystem.IsWindows() ? new AttemptScope(new(createEnvironment, null, null)) : null;

    internal static IDisposable? EnterFocusedArm(string workingDirectory)
    {
        if (Attempt.Value is not { } attempt) return null;
        if (string.Equals(attempt.WorkingDirectory, Path.GetFullPath(workingDirectory), StringComparison.OrdinalIgnoreCase))
            return null;
        return EnterHermeticAttempt(workingDirectory, attempt.CreateEnvironment);
    }

    internal static bool IsMissingToolsFailure(Exception exception) =>
        exception is AcceptanceInfrastructureDeferredException { ReasonCode: MissingToolsReasonCode };

    internal static IDisposable EnterAttempt(GateToolResolutionResult? result) => new AttemptScope(new(null, null, result));

    private static string? ReadVariable(IEnumerable<KeyValuePair<string, string?>> environment, string name) =>
        environment.LastOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private static string NormalizeDirectory(string directory)
    {
        if (directory.Length >= 2 && directory[0] == '"' && directory[^1] == '"')
            directory = directory[1..^1];
        var rootLength = Path.GetPathRoot(directory.AsSpan()).Length;
        return directory[..Math.Max(rootLength, directory.TrimEnd('\\', '/').Length)];
    }

    private sealed record AttemptContext(
        Func<IDictionary<string, string?>>? CreateEnvironment,
        string? WorkingDirectory,
        GateToolResolutionResult? Result);

    private sealed class AttemptScope : IDisposable
    {
        private readonly AttemptContext? _previous = Attempt.Value;

        internal AttemptScope(AttemptContext context)
        {
            Attempt.Value = context;
        }

        public void Dispose() => Attempt.Value = _previous;
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
