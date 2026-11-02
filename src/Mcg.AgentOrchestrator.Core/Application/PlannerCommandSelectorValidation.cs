namespace Mcg.AgentOrchestrator.Core;

internal static class PlannerCommandSelectorValidation
{
    private static Func<Goal, TaskSpec, IReadOnlyList<DeclaredGateCheck>>? defaultSource;

    // One workspace is bound to an App process. Install its resolver once, before any kernels
    // are loaded; rehydrated kernels use the same resolver without storing host services.
    internal static void InstallDefaultSource(Func<Goal, TaskSpec, IReadOnlyList<DeclaredGateCheck>> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Interlocked.CompareExchange(ref defaultSource, source, null) is not null)
            throw new InvalidOperationException("The declared gate check source is already installed.");
    }

    internal static string? Refusal(IEnumerable<string> selectors, Goal goal, TaskSpec task,
        Func<Goal, TaskSpec, IReadOnlyList<DeclaredGateCheck>>? source)
    {
        // Declared-check matches are accepted either way; identities need no check-source lookup.
        var commands = selectors.Where(StartsWithRunner).ToArray();
        if (commands.Length == 0) return null;

        var checks = (source ?? Volatile.Read(ref defaultSource))?.Invoke(goal, task) ?? [];
        var unmatched = commands.Where(selector => !checks.Any(check => check.Matches(selector))).ToArray();
        if (unmatched.Length == 0) return null;

        var declared = checks.Count == 0 ? "no declared checks are available"
            : string.Join(", ", checks.Select(check => $"'{check.Name}'"));
        return $"Planner gate test obligation selectors match no declared gate check: {string.Join(", ", unmatched.Select(selector => $"'{selector}'"))}. " +
            $"Declared check names: {declared}. " +
            "On declared-command stacks, test identities use <test file>::<test name> or the bare test name; " +
            "on .NET, use the class or Namespace.Class.Method. " +
            "A whole-suite criterion such as \"existing checks pass\" is proven by the gate verdict and needs no test obligation. " +
            "Name a declared check or omit the gate test obligation for that criterion, then replan.";
    }

    private static bool StartsWithRunner(string selector)
    {
        var text = selector.Trim();
        var end = 0;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        return text[..end].ToLowerInvariant() is "npm" or "npx" or "pnpm" or "yarn" or "bunx" or
            "node" or "dotnet" or "go" or "cargo" or "pwsh" or "pytest" or "make" or "busted" or
            "tsc" or "vitest" or "jest" or "bun" or "deno" or "crosswalk-test" or "python";
    }
}
