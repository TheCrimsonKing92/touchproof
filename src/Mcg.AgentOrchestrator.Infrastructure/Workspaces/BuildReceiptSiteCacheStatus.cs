using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class BuildReceiptSiteCacheStatus
{
    internal static (string Cache, string? RefusalReason) Resolve(DotnetTestBuildPhase phase)
    {
        if (phase.TreeOutputRestore is { } reused)
            return reused.Hit ? ("hit", null) : ("miss", reused.Refusal?.ToReceiptReason());
        if (phase.CachePlan is null) return ("none", null);

        // Read the completed cache receipt; a plan alone does not prove reuse.
        var projects = Regex.Match(phase.Run?.Result.ResultSummary ?? "",
            @"\bbase-build-cache\s[^;]*?\bprojects=([^\s;]+)").Groups[1].Value;
        return projects.Length > 0 && projects.Split(',').All(project =>
            project.Length > "=hit".Length && project.EndsWith("=hit", StringComparison.Ordinal))
            ? ("hit", null) : ("miss", null);
    }
}
