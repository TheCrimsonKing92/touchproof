using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class BuildReceiptSiteCacheStatus
{
    internal static string Resolve(DotnetTestBuildPhase phase)
    {
        if (phase.CachePlan is null) return "none";

        // Read the completed cache receipt; a plan alone does not prove reuse.
        var projects = Regex.Match(phase.Run?.Result.ResultSummary ?? "",
            @"\bbase-build-cache\s[^;]*?\bprojects=([^\s;]+)").Groups[1].Value;
        return projects.Length > 0 && projects.Split(',').All(project =>
            project.Length > "=hit".Length && project.EndsWith("=hit", StringComparison.Ordinal))
            ? "hit" : "miss";
    }
}
