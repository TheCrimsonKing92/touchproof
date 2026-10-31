using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class FindingStagnationRetryInstruction
{
    internal static string Append(string message, IEnumerable<ReviewFinding> findings)
    {
        var ids = findings.Where(finding => finding.State == ReviewFindingState.Open &&
                finding.Severity == FindingSeverity.Blocking)
            .Select(finding => finding.StableId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return ids.Length == 0 ? message : message + Environment.NewLine +
            "If a blocking finding cannot be resolved by a code change, report blockers: exact-blocker - " +
            "and name that finding's stable id verbatim. Blocking finding stable ids: " + string.Join(", ", ids) + ".";
    }
}
