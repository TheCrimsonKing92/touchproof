using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// An immutable observation of this process's executable lookup, before git shim handling.
internal sealed class ConductorGitResolution
{
    internal enum Classification { NotChecked, Missing, Cmd, RawMinGw, Unrecognized }

    private static readonly Lazy<ConductorGitResolution> ProcessResolution = new(() =>
        Resolve(OperatingSystem.IsWindows(), HandleListProcessLauncher.ResolveApplication));
    private static readonly AsyncLocal<ConductorGitResolution?> ScopedResolution = new();

    private ConductorGitResolution(string? resolvedPath, Classification kind, string? expectedCmdPath = null)
    {
        ResolvedPath = resolvedPath;
        Kind = kind;
        ExpectedCmdPath = expectedCmdPath;
    }

    internal string? ResolvedPath { get; }
    internal Classification Kind { get; }
    internal string? ExpectedCmdPath { get; }
    internal bool RefusesStart => Kind == Classification.RawMinGw;
    internal string LogValue => ResolvedPath ?? "none";
    internal static ConductorGitResolution NotChecked { get; } = new(null, Classification.NotChecked);
    internal static ConductorGitResolution Current => ScopedResolution.Value ?? ProcessResolution.Value;

    internal static ConductorGitResolution Resolve(bool isWindows, Func<string, string?> resolveApplication)
    {
        if (!isWindows) return NotChecked;
        try { return Classify(resolveApplication("git")); }
        catch (Exception) { return new(null, Classification.Missing); }
    }

    internal static ConductorGitResolution Classify(string? resolvedPath)
    {
        // The launcher returns the bare name when lookup misses; it is not a resolved path.
        if (string.IsNullOrWhiteSpace(resolvedPath) || !Path.IsPathFullyQualified(resolvedPath))
            return new(null, Classification.Missing);

        var normalized = Path.GetFullPath(resolvedPath);
        var directory = Path.GetDirectoryName(normalized);
        if (string.Equals(Path.GetFileName(normalized), "git.exe", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(Path.GetFileName(directory), "cmd", StringComparison.OrdinalIgnoreCase))
                return new(resolvedPath, Classification.Cmd);
            if (string.Equals(Path.GetFileName(directory), "bin", StringComparison.OrdinalIgnoreCase))
            {
                var platformDirectory = Path.GetDirectoryName(directory);
                var platform = Path.GetFileName(platformDirectory);
                if (string.Equals(platform, "mingw32", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(platform, "mingw64", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(platform, "clangarm64", StringComparison.OrdinalIgnoreCase))
                {
                    var root = Path.GetDirectoryName(platformDirectory)!;
                    return new(resolvedPath, Classification.RawMinGw, Path.Combine(root, "cmd", "git.exe"));
                }
            }
        }
        return new(resolvedPath, Classification.Unrecognized);
    }

    internal static IDisposable Use(ConductorGitResolution resolution)
    {
        var previous = ScopedResolution.Value;
        ScopedResolution.Value = resolution;
        return new RestoreResolution(previous);
    }

    private sealed class RestoreResolution(ConductorGitResolution? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            ScopedResolution.Value = previous;
            _disposed = true;
        }
    }
}
