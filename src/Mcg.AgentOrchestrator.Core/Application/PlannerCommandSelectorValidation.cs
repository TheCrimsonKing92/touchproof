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
        Func<Goal, TaskSpec, IReadOnlyList<DeclaredGateCheck>>? source,
        IReadOnlyList<RunnerTestIdentity>? listing = null)
    {
        var all = selectors.ToArray();
        var commands = all.Where(StartsWithRunner).ToArray();
        var unmatchedIdentities = listing is { Count: > 0 }
            ? all.Where(selector => !StartsWithRunner(selector) && !listing.Any(entry =>
                    string.Equals(selector, entry.Identity, StringComparison.Ordinal) ||
                    string.Equals(selector, entry.Name, StringComparison.Ordinal)))
                .Distinct(StringComparer.Ordinal).ToArray()
            : [];
        // Unavailable listings preserve identity-only rounds without resolving declared checks.
        if (commands.Length == 0 && unmatchedIdentities.Length == 0) return null;

        var checks = (source ?? Volatile.Read(ref defaultSource))?.Invoke(goal, task) ?? [];
        var unmatched = commands.Where(selector => !checks.Any(check => check.Matches(selector))).ToArray();
        var identityDiagnostic = unmatchedIdentities.Length == 0 ? null :
            $"Planner gate test obligation selectors match no runner-listed test identity: {string.Join(", ", unmatchedIdentities.Select(selector => $"'{selector}'"))}. " +
            "Use " + GateTestSelectorForm.ForChecks(checks) + ". Use an exact listed identity or test name, then replan.";
        if (unmatched.Length == 0) return identityDiagnostic;

        var declared = checks.Count == 0 ? "no declared checks are available"
            : string.Join(", ", checks.Select(check => $"'{check.Name}'"));
        return $"Planner gate test obligation selectors match no declared gate check: {string.Join(", ", unmatched.Select(selector => $"'{selector}'"))}. " +
            $"Declared check names: {declared}. " +
            "Use " + GateTestSelectorForm.ForChecks(checks) + ". " +
            "A whole-suite criterion such as \"existing checks pass\" is proven by the gate verdict and needs no test obligation. " +
            "Name a declared check or omit the gate test obligation for that criterion, then replan." +
            (identityDiagnostic is null ? "" : " " + identityDiagnostic);
    }

    internal static bool NeedsListing(IEnumerable<string> selectors) => selectors.Any(selector => !StartsWithRunner(selector));

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
