using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class LaunchLockSiteCapture
{
    // GitCli records synchronously inside Start. Keep metadata on that thread and restore
    // it before draining output or invoking observers, including for nested launches.
    [ThreadStatic] private static IReadOnlyList<string>? gitArguments;

    internal static IDisposable BeginGit(IReadOnlyList<string> arguments)
    {
        var scope = new GitScope(gitArguments);
        gitArguments = arguments;
        return scope;
    }

    internal static string GitSubcommand(IReadOnlyList<string> arguments)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (argument == "-c") { i++; continue; }
            if (!argument.StartsWith('-')) return argument;
        }
        return "-";
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static LaunchLockSite Capture(LaunchLockEntryPoint entry)
    {
        var git = entry == LaunchLockEntryPoint.GitCli && gitArguments is { } arguments
            ? GitSubcommand(arguments) : "-";
        try
        {
            foreach (var frame in new StackTrace(false).GetFrames())
            {
                if (frame.GetMethod() is not { DeclaringType: { } type } method || IsPlumbing(type)) continue;
                return new(CallerName(type, method), git);
            }
        }
        catch { } // Attribution must not change whether a process can launch.
        return new("Unknown.Unknown", git);
    }

    private static bool IsPlumbing(Type type)
    {
        for (Type? current = type; current is not null; current = current.DeclaringType)
            if (current == typeof(GitCli) || current == typeof(ProcessTreeGuiSuppression) ||
                current == typeof(HandleListProcessLauncher) || current == typeof(HandleListLaunchedProcess) ||
                current == typeof(OwnedProcessGroup) || current == typeof(LaunchLockTelemetry) ||
                current == typeof(LaunchLockAttribution) || current == typeof(LaunchLockSiteCapture) ||
                current == typeof(LaunchLockSiteFormatter)) return true;
        return false;
    }

    private static string CallerName(Type type, MethodBase method)
    {
        if (method.Name == "MoveNext" && type.DeclaringType is { } owner &&
            type.IsDefined(typeof(CompilerGeneratedAttribute), false) && type.Name.StartsWith('<'))
        {
            var end = type.Name.IndexOf(">d__", StringComparison.Ordinal);
            if (end > 1) return $"{owner.Name}.{type.Name[1..end]}";
        }
        return $"{type.Name}.{method.Name}";
    }

    private sealed class GitScope(IReadOnlyList<string>? previous) : IDisposable
    {
        public void Dispose() => gitArguments = previous;
    }
}
