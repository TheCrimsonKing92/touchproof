using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class GateReplayEnvironmentAllowlist
{
    private static readonly (string Name, bool Required)[] Entries =
    [
        ("MCG_ORCHESTRATOR_REPOSITORY_ROOT", true),
        (GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable, false),
        (TempRootApparatusLossReceiptStore.GateInvocationIdVariable, false),
        (DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, false),
        (LaneSlotTimingReader.VariableName, false),
        (TempRootApparatusLossReceiptStore.ReceiptPathVariable, false),
        ("TEMP", true),
        ("TMP", true),
        ("MCG_ACCEPTANCE_CHANGE_SCOPED", false)
    ];

    private static readonly Regex CredentialShape = new(
        """
        (?:ghp_|gho_|ghs_|github_pat_|sk-)[a-z0-9_-]{20,}
        | AKIA[a-z0-9]{16}
        | xox[abprs]-
        | eyJ[a-z0-9_-]*\.[a-z0-9_-]+\.[a-z0-9_-]+
        | (?:Bearer|Basic)[ ]\S+
        | [a-z][a-z0-9+.-]*://[^/\s?#@]+@[^/\s?#@]+
        | -----BEGIN
        | [\w.-]*(?:password|passwd|secret|token|apikey|api_key|credential)[\w.-]*\s*=
        | \A[a-z0-9+]{32,}={0,2}\z
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace |
        RegexOptions.NonBacktracking);

    internal static bool Contains(string name) =>
        Entries.Any(entry => StringComparer.OrdinalIgnoreCase.Equals(entry.Name, name));

    internal static IEnumerable<string> Names => Entries.Select(entry => entry.Name);

    internal static IEnumerable<string> RequiredNames =>
        Entries.Where(entry => entry.Required).Select(entry => entry.Name);

    internal static bool IsSensitive(string value) => CredentialShape.IsMatch(value);

    internal static Projection Capture(IEnumerable<KeyValuePair<string, string?>> effectiveEnvironment)
    {
        var values = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        var droppedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in effectiveEnvironment)
        {
            var (name, _) = Array.Find(Entries, entry =>
                StringComparer.OrdinalIgnoreCase.Equals(entry.Name, pair.Key));
            if (name is null || pair.Value is not { } value) continue;

            if (IsSensitive(value))
            {
                droppedNames.Add(name);
                values.Remove(name);
            }
            else if (!droppedNames.Contains(name))
            {
                values[name] = value;
            }
        }
        return new(values.ToImmutable(), [.. droppedNames.Order(StringComparer.Ordinal)]);
    }

    internal sealed record Projection(
        ImmutableDictionary<string, string> Values,
        ImmutableArray<string> DroppedNames);
}
