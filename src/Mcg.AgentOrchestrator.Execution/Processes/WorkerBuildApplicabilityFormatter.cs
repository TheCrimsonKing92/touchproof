using System.Text;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class WorkerBuildApplicabilityFormatter
{
    internal const int RenderLimit = 8192;
    internal const int ListLimit = 16;

    internal static string Format(WorkerBuildApplicabilityExplanation explanation)
    {
        var text = new StringBuilder();
        foreach (var reason in explanation.Reasons)
            text.AppendLine($"{DispatchFailureDiagnosticMarker.WorkerBuildApplicability}{Name(reason)}; next={Guidance(reason)}");
        text.AppendLine($"applicability_current: dispatch={Identity(explanation.DispatchId)}; digest={Digest(explanation.TreeDigest)}; " +
            $"scope={Identity(explanation.ScopeIdentity)}; required_projects={List(explanation.RequiredProjects)}");
        var rendered = 0;
        foreach (var invocation in explanation.Invocations)
        {
            var line = $"applicability_invocation: id={Identity(invocation.InvocationId)}; dispatch={Identity(invocation.DispatchId)}; " +
                $"digest={Digest(invocation.TreeDigest)}; scope={Identity(invocation.ScopeIdentity)}; " +
                $"dispatch_matches={Known(invocation.DispatchMatches)}; tree_matches={Known(invocation.TreeMatches)}; " +
                $"covers_projects={Known(invocation.CoversProjects)}; projects={List(invocation.Projects)}; " +
                $"missing_projects={List(invocation.MissingProjects)}; phase={invocation.Phase}; recorded_status={invocation.Outcome?.ToString() ?? "pending"}; " +
                $"custody_released={Known(invocation.CustodyReleased)}; reasons=[{string.Join(',', invocation.Reasons.Select(Name))}]; " +
                $"changed_paths={List(invocation.ChangedPaths)}";
            // Omit whole comparisons; never clip a digest/identity into a seemingly exact value.
            if (text.Length + line.Length + 160 > RenderLimit) break;
            text.AppendLine(line);
            rendered++;
        }
        text.Append($"applicability_omitted_invocations={explanation.OmittedInvocations + explanation.Invocations.Count - rendered}; " +
            $"truncated={(explanation.OmittedInvocations > 0 || rendered < explanation.Invocations.Count ? "true" : "false")}");
        return text.ToString();
    }

    private static string Known(bool? value) => value is null ? "unknown" : value.Value ? "true" : "false";
    private static string Identity(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':') ? value : "unknown";
    private static string Digest(string? value) => WorkerBuildApplicabilityExplanation.IsTreeDigest(value) ? value! : "unknown";
    private static bool RelativePath(string value) => value.Length is > 0 and <= 240 &&
        !Path.IsPathRooted(value) && !value.Contains(':') && !value.Contains('\\') &&
        !value.Split('/').Any(part => part is ".." or "." or "") &&
        !value.Any(c => char.IsControl(c) || c is ';' or '[' or ']' or ',');

    private static string List(IReadOnlyList<string>? values)
    {
        if (values is null) return "unknown";
        var ordered = values.Order(StringComparer.Ordinal).ToArray();
        var entries = new List<string>();
        var used = 0;
        foreach (var entry in ordered)
        {
            if (entries.Count == ListLimit) break;
            if (!RelativePath(entry)) continue;
            if (used + entry.Length > 512) break;
            entries.Add(entry);
            used += entry.Length;
        }
        return $"[{string.Join(',', entries)}]; omitted={values.Count - entries.Count}; truncated={(values.Count > entries.Count ? "true" : "false")}";
    }

    internal static string Name(WorkerBuildApplicabilityReason reason) => reason switch
    {
        WorkerBuildApplicabilityReason.NoInvocationRecorded => "no-invocation-recorded",
        WorkerBuildApplicabilityReason.OnlyOtherDispatches => "only-other-dispatches",
        WorkerBuildApplicabilityReason.OtherDispatch => "other-dispatch",
        WorkerBuildApplicabilityReason.TreeMismatch => "tree-mismatch",
        WorkerBuildApplicabilityReason.IncompleteProjectCoverage => "incomplete-project-coverage",
        WorkerBuildApplicabilityReason.UnresolvedScope => "unresolved-scope",
        WorkerBuildApplicabilityReason.UnresolvedCandidate => "unresolved-candidate",
        WorkerBuildApplicabilityReason.PendingInvocation => "pending-invocation",
        WorkerBuildApplicabilityReason.TerminalNonSuccess => "terminal-non-success",
        _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };

    private static string Guidance(WorkerBuildApplicabilityReason reason) => reason switch
    {
        WorkerBuildApplicabilityReason.NoInvocationRecorded or WorkerBuildApplicabilityReason.OnlyOtherDispatches or
            WorkerBuildApplicabilityReason.OtherDispatch => "supply authorized current-dispatch build evidence",
        WorkerBuildApplicabilityReason.TreeMismatch => "supply evidence matching the current candidate",
        WorkerBuildApplicabilityReason.IncompleteProjectCoverage => "supply one invocation covering all required projects",
        WorkerBuildApplicabilityReason.UnresolvedScope => "resolve current project scope evidence",
        WorkerBuildApplicabilityReason.UnresolvedCandidate => "resolve current and recorded candidate evidence",
        WorkerBuildApplicabilityReason.PendingInvocation => "reconcile existing invocation completion and custody",
        WorkerBuildApplicabilityReason.TerminalNonSuccess => "inspect the recorded terminal build outcome",
        _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };
}
