using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Observations about one task's selection inputs, never authority or recovery inputs.
internal enum WorkerBuildApplicabilityReason
{
    NoInvocationRecorded, OnlyOtherDispatches, OtherDispatch, TreeMismatch,
    IncompleteProjectCoverage, UnresolvedScope, UnresolvedCandidate,
    PendingInvocation, TerminalNonSuccess
}

internal sealed record WorkerBuildApplicabilityComparison(
    string InvocationId, string DispatchId, string TreeDigest, string ScopeIdentity,
    IReadOnlyList<string> Projects, IReadOnlyList<string>? MissingProjects,
    bool DispatchMatches, bool? TreeMatches, bool? CoversProjects,
    WorkerBuildPhase Phase, WorkerBuildOutcome? Outcome, bool CustodyReleased,
    IReadOnlyList<WorkerBuildApplicabilityReason> Reasons, IReadOnlyList<string>? ChangedPaths);

internal sealed record WorkerBuildApplicabilityExplanation(
    string DispatchId, string? TreeDigest, string? ScopeIdentity, IReadOnlyList<string>? RequiredProjects,
    IReadOnlyList<WorkerBuildApplicabilityReason> Reasons,
    IReadOnlyList<WorkerBuildApplicabilityComparison> Invocations, int OmittedInvocations)
{
    // Bound collection and optional tree reads independently of the full comparison set.
    internal const int InvocationLimit = 8;

    internal static WorkerBuildApplicabilityExplanation Capture(string dispatchId, string? digest,
        WorkerBuildProjectScope.Result? scope, IReadOnlyList<WorkerBuildInvocation> invocations,
        Func<string, string, IReadOnlyList<string>?>? compareTrees = null)
    {
        var reasons = new HashSet<WorkerBuildApplicabilityReason>();
        if (scope is null) reasons.Add(WorkerBuildApplicabilityReason.UnresolvedScope);
        if (!IsTreeDigest(digest)) reasons.Add(WorkerBuildApplicabilityReason.UnresolvedCandidate);
        if (invocations.Count == 0) reasons.Add(WorkerBuildApplicabilityReason.NoInvocationRecorded);
        else if (invocations.All(i => i.DispatchId != dispatchId))
            reasons.Add(WorkerBuildApplicabilityReason.OnlyOtherDispatches);
        var comparisons = new List<WorkerBuildApplicabilityComparison>();
        var pathComparisons = new Dictionary<(string Recorded, string Current), IReadOnlyList<string>?>();
        foreach (var invocation in invocations.OrderBy(i => i.Id, StringComparer.Ordinal))
        {
            var failures = new HashSet<WorkerBuildApplicabilityReason>();
            var dispatchMatches = invocation.DispatchId == dispatchId;
            if (!dispatchMatches) failures.Add(WorkerBuildApplicabilityReason.OtherDispatch);
            bool? treeMatches = IsTreeDigest(digest) && IsTreeDigest(invocation.TreeDigest)
                ? invocation.TreeDigest == digest : null;
            if (treeMatches is false) failures.Add(WorkerBuildApplicabilityReason.TreeMismatch);
            if (treeMatches is null) failures.Add(WorkerBuildApplicabilityReason.UnresolvedCandidate);
            var coverageKnown = scope is not null && invocation.Projects.All(IsProjectIdentity);
            var missing = coverageKnown ? scope!.Projects.Except(invocation.Projects, StringComparer.OrdinalIgnoreCase).ToArray() : null;
            bool? covers = !coverageKnown ? null : invocation.Projects
                .Intersect(scope!.Projects, StringComparer.OrdinalIgnoreCase).Count() == scope.Projects.Count;
            if (covers is false) failures.Add(WorkerBuildApplicabilityReason.IncompleteProjectCoverage);
            if (covers is null) failures.Add(WorkerBuildApplicabilityReason.UnresolvedScope);
            if (!invocation.IsTerminal || !invocation.CustodyReleased)
                failures.Add(WorkerBuildApplicabilityReason.PendingInvocation);
            if (invocation.Outcome is not null and not WorkerBuildOutcome.Success)
                failures.Add(WorkerBuildApplicabilityReason.TerminalNonSuccess);
            reasons.UnionWith(failures);
            if (comparisons.Count == InvocationLimit) continue;
            IReadOnlyList<string>? paths = null;
            if (treeMatches is not null && compareTrees is not null)
            {
                var key = (Recorded: invocation.TreeDigest, Current: digest!);
                if (!pathComparisons.TryGetValue(key, out paths))
                {
                    try { paths = compareTrees(key.Recorded, key.Current); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { /* Diagnostic evidence remains unknown. */ }
                    pathComparisons[key] = paths;
                }
            }
            comparisons.Add(new(invocation.Id, invocation.DispatchId, invocation.TreeDigest, invocation.ScopeIdentity,
                invocation.Projects.ToArray(), missing, dispatchMatches, treeMatches, covers,
                invocation.Phase, invocation.Outcome, invocation.CustodyReleased, failures.Order().ToArray(), paths));
        }
        return new(dispatchId, digest, scope?.Identity, scope?.Projects.ToArray(), reasons.Order().ToArray(),
            comparisons.ToArray(), invocations.Count - comparisons.Count);
    }

    internal static bool IsTreeDigest(string? digest) => digest is not null &&
        digest.StartsWith(WorktreeTreeDigest.Algorithm + ":", StringComparison.Ordinal) &&
        digest[(WorktreeTreeDigest.Algorithm.Length + 1)..] is { Length: 40 or 64 } hash && hash.All(Uri.IsHexDigit);

    private static bool IsProjectIdentity(string project) => !string.IsNullOrWhiteSpace(project) &&
        !Path.IsPathRooted(project) && !project.Contains(':') && !project.Contains('\\') &&
        !project.Any(char.IsControl) && !project.Split('/').Any(part => part is "" or "." or "..") &&
        new[] { ".csproj", ".fsproj", ".vbproj" }.Contains(Path.GetExtension(project), StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyList<string>? CompareRecordedTrees(string root, string recorded, string current)
    {
        if (!IsTreeDigest(recorded) || !IsTreeDigest(current)) return null;
        var before = recorded[(WorktreeTreeDigest.Algorithm.Length + 1)..];
        var after = current[(WorktreeTreeDigest.Algorithm.Length + 1)..];
        foreach (var hash in new[] { before, after }.Distinct(StringComparer.Ordinal))
        {
            var type = GitCli.RunForRepository(root, "cat-file", "-t", hash);
            if (!type.Succeeded || type.DrainTimedOut || type.Output.Trim() != "tree") return null;
        }
        var diff = GitCli.RunForRepository(root, "diff-tree", "--no-ext-diff", "--no-textconv", "--no-renames",
            "--name-only", "-r", "-z", before, after, "--");
        return diff.Succeeded && !diff.DrainTimedOut ? diff.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries) : null;
    }
}
