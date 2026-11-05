namespace Mcg.AgentOrchestrator.Infrastructure;

// Suppress whole diagnostic excerpts: bounded captures can split an input value at either end,
// so replacing complete strings in already-truncated output cannot establish non-disclosure.
// Original command output and normalized test evidence remain available to the parser/gates.
internal sealed class JunitCommandEnvironmentDiagnostics(GoalAcceptanceVerifier.AcceptanceManifestCheck check)
{
    internal bool Enabled => check.Environment is { Count: > 0 };
    internal string Detail(string detail) => Enabled
        ? "Diagnostic details suppressed for check-local environment inputs." : detail;

    internal string Identity(string identity)
    {
        if (!Enabled) return identity;
        foreach (var value in check.Environment!.Values.Where(value => value.Length > 0)
                     .Distinct(StringComparer.Ordinal).OrderByDescending(value => value.Length))
            identity = identity.Replace(value, "[environment input]", StringComparison.Ordinal);
        return identity;
    }
}
