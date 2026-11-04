using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

// Facts read by the landing adapter from the owning dispatch and actual Git commits.
public sealed record DeveloperBuildCandidateEvidence(string DispatchId, string TreeDigest, bool CandidateProven);

// A report request, not an implementation verdict. Only this explicit structured grammar grants eligibility.
internal static class DeveloperBuildReportEvidence
{
    internal const string InfrastructureProject = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
    private const string ProjectGrammar = @"\ASanctioned build receipt for (?<projects>`[^`\r\n]+\.csproj`(?:, `[^`\r\n]+\.csproj`)*) and exact affected class identities\z";

    internal static bool IsBuildReportRequest(HumanInputRequest request) => request.PlannerEvidence?.Needed is { } needed &&
        (needed == "Sanctioned Infrastructure test-project build receipt and exact affected class identities" || Regex.IsMatch(needed, ProjectGrammar));

    internal static string[] Projects(Goal goal, HumanInputRequest request)
    {
        if (request.GoalId != goal.Id || request.Kind != HumanWaitKind.ProspectiveAcceptanceEvidence ||
            request.EvidenceOwner != "developer" || request.PlannerEvidence is not { Owner: "developer", Availability: "post-implementation" } payload ||
            request.PlannerIdentity is not { } identity || identity.GoalId != goal.Id.Value ||
            identity.RequestingTaskId != request.TaskId?.Value || identity.EvidenceKey != payload.EvidenceKey ||
            identity.CriterionIndex != payload.CriterionIndex || ReportCriterionLookup.Find(goal, identity) is null)
            return [];
        return ParseProjects(payload.Needed);
    }

    private static string[] ParseProjects(string needed)
    {
        // Existing structured request wording has one unambiguous repository project alias.
        if (needed == "Sanctioned Infrastructure test-project build receipt and exact affected class identities")
            return [InfrastructureProject];
        var match = Regex.Match(needed, ProjectGrammar);
        var projects = match.Success ? Regex.Matches(match.Groups["projects"].Value, "`([^`]+)`")
            .Select(item => NormalizeProject(item.Groups[1].Value)).ToArray() : [];
        return projects.All(IsRepositoryProject) ? projects.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : [];
    }

    internal static string? Validate(Goal goal, HumanInputRequest request, TaskSpec? task,
        string candidate, Func<string, string?> gateSource, Func<TaskSpec, DeveloperBuildCandidateEvidence>? readCandidate,
        out string? evidence)
    {
        evidence = null;
        var projects = Projects(goal, request);
        if (projects.Length == 0) return "not-a-build-report-request";
        if (task?.Status != WorkTaskStatus.Completed || task.LastDispatch is not { } dispatch ||
            task.LastVerification is not { } verification) return "owner-round-incomplete";
        if (dispatch.BriefVersion != goal.AuthoritativeBrief.Version) return "build-brief-version-differs";
        if (verification.EvidenceSource != VerificationEvidenceSource.Dispatch ||
            verification.DispatchAttemptAt is { } attempt && attempt != dispatch.DispatchedAt ||
            verification.DispatchStartedAt is not { } started || started < dispatch.DispatchedAt ||
            started > verification.CompletedAt || verification.ExitCode != 0 ||
            verification.CompletedAt < dispatch.DispatchedAt) return "build-report-round-differs";
        if (verification.AuthoritativeStandardOutput is not { } output ||
            !DeveloperReportEvidence.TryReadReport(task, out var report)) return "authoritative-build-report-missing";
        var names = DeveloperDeferredTestClassNames.Parse(report).Order(StringComparer.Ordinal).ToArray();
        if (names.Length == 0) return "build-report-classes-missing";
        var required = request.PlannerEvidence?.Selectors ?? [];
        var criterionField = DeveloperReportEvidence.FindRequiredField(ReportCriterionLookup.Find(goal, request.PlannerIdentity)!);
        var requiredNames = required.Concat(criterionField is null ? [] : DeveloperDeferredTestClassNames.Parse(criterionField));
        if (requiredNames.Any(name => !names.Contains(name, StringComparer.Ordinal))) return "build-report-class-coverage-differs";
        var commands = WorkerResultBlockers.ReadCommands(output);
        var ids = Regex.Matches(commands ?? "", @"\binvocation ([a-f0-9]{32})\b")
            .Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return "build-invocation-reference-missing";
        var sources = new List<string>();
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var proof = readCandidate?.Invoke(task);
        if (proof is null) return "build-candidate-provenance-missing:dispatch-id-and-implementation-tree-required";
        foreach (var id in ids)
        {
            var invocation = task.WorkerBuildInvocations.SingleOrDefault(item => item.Id == id);
            if (invocation is null) return $"build-invocation-missing:{id}";
            try { invocation.RequireValid(); }
            catch (InvalidOperationException) { return $"build-invocation-invalid:{id}"; }
            if (invocation.GoalId != goal.Id.Value || invocation.TaskId != task.Id.Value ||
                invocation.DispatchAt != dispatch.DispatchedAt || invocation.DispatchId != proof.DispatchId)
                return $"build-invocation-round-differs:{id}";
            if (invocation.Outcome != WorkerBuildOutcome.Success || invocation.CompilerExitCode != 0 || !invocation.CustodyReleased)
                return $"build-compiler-not-successful:{id}";
            if (invocation.Observations.Any(item => item.At < dispatch.DispatchedAt || item.At > verification.CompletedAt))
                return $"build-invocation-report-time-differs:{id}";
            if (invocation.TreeDigest != proof.TreeDigest) return $"build-implementation-tree-differs:{id}";
            covered.UnionWith(invocation.Projects.Select(NormalizeProject));
            sources.Add(id);
        }
        var missingProjects = projects.Where(project => !covered.Contains(project)).ToArray();
        if (missingProjects.Length > 0) return $"build-project-coverage-missing:{string.Join(",", missingProjects)}";
        if (!proof.CandidateProven) return $"build-candidate-provenance-missing:{dispatch.ResultCommit}->{candidate}";
        var gates = names.Select(name => (Name: name, Source: gateSource(name))).ToArray();
        var missingClasses = gates.Where(item => item.Source is null).Select(item => item.Name).ToArray();
        if (missingClasses.Length > 0) return $"class-not-in-passed-gate:{string.Join(",", missingClasses)}";
        evidence = $"build_invocations={string.Join(",", sources)} dispatch={proof.DispatchId} " +
            $"dispatch_at={dispatch.DispatchedAt:O} implementation_tree={proof.TreeDigest} " +
            $"projects={string.Join(",", projects)} tests={report} " +
            $"gate={string.Join(",", gates.Select(item => $"{item.Name}@{item.Source}"))}";
        return null;
    }

    private static string NormalizeProject(string value) => value.Replace('\\', '/');
    private static bool IsRepositoryProject(string value) => !value.StartsWith('/') && !value.Contains(':') &&
        !value.Split('/').Any(part => part is ".." or "." or "");
}
