using Mcg.AgentOrchestrator.Core;
using System.Text.Json;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class OwnerProtectedConfigurationPolicy
{
    internal sealed record OwnerProtectedDecision(AcceptanceCheckResult? Failure, AcceptanceCheckResult? Pass,
        bool OwnerApprovalSatisfied = false);

    internal static bool HasOwnerPolicyApprovalForTests(
        ICollaborationItemStore store, GoalId goalId, string candidateSha) =>
        AcceptancePolicyChangeDecision.IsApprovedAsync(store, goalId.Value, candidateSha)
            .GetAwaiter().GetResult();

    internal static AcceptanceCheckResult? TryClassifyManifestTrustWithGitForTests(
        string worktreePath,
        IReadOnlyList<string>? changedFiles,
        Func<string, string[], string?> resolveGitText) =>
        TryClassifyManifestTrustCore(worktreePath, changedFiles, resolveGitText, TrunkBranchName.Default);

    internal static AcceptanceCheckResult? TryClassifyManifestTrustWithGitForTests(
        string worktreePath,
        IReadOnlyList<string>? changedFiles,
        Func<string, string[], string?> resolveGitText,
        ICollaborationItemStore decisions,
        GoalId goalId,
        string candidateSha)
    {
        var failure = TryClassifyManifestTrustCore(worktreePath, changedFiles, resolveGitText, TrunkBranchName.Default);
        if (failure is null || HasOwnerPolicyApprovalForTests(decisions, goalId, candidateSha))
            return null;
        var fingerprint = AcceptanceOwnerProtectedFingerprint.Compute(
            worktreePath, changedFiles, resolveGitText, "HEAD", readCommittedCandidate: false, TrunkBranchName.Default);
        return fingerprint is not null && AcceptancePolicyChangeDecision.IsApprovedForFingerprintAsync(
            decisions, goalId.Value, fingerprint).GetAwaiter().GetResult() ? null : failure;
    }

    internal static AcceptanceCheckResult? TryClassifyManifestTrustCore(
        string worktreePath,
        IReadOnlyList<string>? changedFiles,
        Func<string, string[], string?> resolveGitText, string integrationBranch)
    {
        if (changedFiles is null)
            return null;

        const string manifestPath = "config/acceptance-manifest.json";
        var manifestChanged = changedFiles.Any(path =>
            NormalizePath(path).Equals(manifestPath, StringComparison.OrdinalIgnoreCase));
        var candidateManifestPath = Path.Combine(worktreePath, "config", "acceptance-manifest.json");
        var manifestMissing = changedFiles.Count > 0 &&
            changedFiles.Any(path => !RepositoryChangeClassifier.IsOwnerProtectedPolicyPath(path)) &&
            !File.Exists(candidateManifestPath);
        var details = new List<string>();

        foreach (var rawPath in AcceptanceOwnerProtectedFingerprint.SelectPolicyPaths(worktreePath, changedFiles, resolveGitText, "HEAD", integrationBranch))
        {
            var path = NormalizePath(rawPath);
            var fullPath = Path.GetFullPath(Path.Combine(worktreePath, path.Replace('/', Path.DirectorySeparatorChar)));
            var root = Path.GetFullPath(worktreePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Owner-protected path escapes worktree: {path}");
            var candidate = File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
            var trusted = resolveGitText(worktreePath, ["show", $"{integrationBranch}:{path}"]);
            IReadOnlyList<string> fields;
            try
            {
                fields = RepositoryChangeClassifier.DescribeJsonChanges(trusted, candidate);
            }
            catch (JsonException)
            {
                fields = ["(unparseable JSON)"];
            }
            details.Add($"{path}: changed field(s) {string.Join(", ", fields.Count == 0 ? ["$"] : fields)}");
        }

        if (manifestChanged || manifestMissing)
        {
            var trusted = resolveGitText(worktreePath, ["show", $"{integrationBranch}:config/acceptance-manifest.json"]);
            if (string.IsNullOrWhiteSpace(trusted))
            {
                if (manifestChanged)
                {
                    if (details.Count == 0 && !File.Exists(candidateManifestPath))
                        return new AcceptanceCheckResult(
                            "acceptance manifest trusted dimensions", false, 1,
                            "Trusted main acceptance manifest could not be compared; refusing candidate engine settings.",
                            ResultSummary: "trusted manifest comparison unavailable");
                    details.Add($"{manifestPath}: changed field(s) $ (trusted main unavailable)");
                }
            }
            else if (manifestMissing)
            {
                details.Add($"{manifestPath}: changed field(s) $ (removed or renamed)");
            }
            else
            {
                var candidate = File.ReadAllText(candidateManifestPath);
                try
                {
                    var decision = RepositoryChangeClassifier.ClassifyAcceptanceManifestChange(trusted, candidate);
                    var fields = RepositoryChangeClassifier.DescribeJsonChanges(trusted, candidate);
                    if (decision.RequiresTrustedReview && details.Count == 0 &&
                        fields.All(IsExistingGuardedEnginePath))
                        return new AcceptanceCheckResult(
                            "acceptance manifest trusted dimensions", false, 1, decision.Evidence,
                            ResultSummary: "operator review required");
                    if (fields.Count > 0 || decision.RequiresTrustedReview)
                        details.Add($"{manifestPath}: changed field(s) {string.Join(", ", fields.Count == 0 ? decision.SecurityCriticalChanges : fields)}");
                }
                catch (JsonException)
                {
                    details.Add($"{manifestPath}: changed field(s) $ (unparseable JSON)");
                }
            }
        }

        return details.Count == 0 ? null : new AcceptanceCheckResult(
            "owner-protected configuration", false, 1,
            $"{string.Join("; ", details)}; owner decision required for this candidate",
            ResultSummary: "operator review required");
    }

    private static bool IsExistingGuardedEnginePath(string path) =>
        path is "engine.enforceStructuralCoverage" or "engine.partitionVerdictFullRerunEveryN" ||
        path.StartsWith("engine.mtpInvocations", StringComparison.Ordinal);

    internal static OwnerProtectedDecision EvaluateOwnerProtectedConfigurationForTests(string worktreePath,
        GoalId? goalId, IReadOnlyList<string>? changedFiles,
        IReadOnlyList<AcceptanceOwnerProtectedCohortMember>? cohortMembers,
        Func<string, string[], string?> gitText, ICollaborationItemStore? decisions,
        Func<AcceptanceTestInventory> inventory, string? candidateSha) =>
        EvaluateOwnerProtectedConfigurationCore(worktreePath, goalId, changedFiles, cohortMembers,
            gitText, decisions, inventory, () => candidateSha, TrunkBranchName.Default);

    internal static OwnerProtectedDecision EvaluateOwnerProtectedConfigurationCore(string worktreePath,
        GoalId? goalId, IReadOnlyList<string>? changedFiles,
        IReadOnlyList<AcceptanceOwnerProtectedCohortMember>? cohortMembers,
        Func<string, string[], string?> gitText, ICollaborationItemStore? decisions,
        Func<AcceptanceTestInventory> inventory, Func<string?> candidateSha, string integrationBranch)
    {
        var failure = TryClassifyManifestTrustCore(worktreePath, changedFiles, gitText, integrationBranch);
        if (failure is null) return new(null, null);
        if (goalId is { } singleGoal && decisions is not null &&
            IsApproved(worktreePath, changedFiles, gitText, decisions, singleGoal, candidateSha(),
                "HEAD", readCommittedCandidate: false, integrationBranch))
        {
            if (cohortMembers is { Count: > 0 })
            {
                var cohortApproved = CohortChangesAreApproved(worktreePath, cohortMembers, gitText, decisions,
                    inventory, integrationBranch, out var manifestApproved);
                return new(null, null, OwnerApprovalSatisfied: cohortApproved && manifestApproved);
            }
            return new(null, null, OwnerApprovalSatisfied: true);
        }
        var equivalent = IsEquivalentManifestChange(worktreePath, changedFiles, gitText, inventory, "HEAD",
                gitText(worktreePath, ["show", $"{integrationBranch}:config/acceptance-manifest.json"]),
                File.Exists(Path.Combine(worktreePath, "config", "acceptance-manifest.json"))
                    ? File.ReadAllText(Path.Combine(worktreePath, "config", "acceptance-manifest.json")) : null, integrationBranch);
        if (cohortMembers is { Count: > 0 })
        {
            if (!CohortChangesAreApproved(worktreePath, cohortMembers, gitText, decisions, inventory,
                    integrationBranch, out var manifestOwnerApproved))
                return new(failure, null);
            return equivalent
                ? new(null, new AcceptanceCheckResult("owner-protected configuration", true, 0,
                    "partition-equivalent", ResultSummary: "partition-equivalent"), manifestOwnerApproved)
                : new(null, null, manifestOwnerApproved);
        }
        if (equivalent)
            return new(null, new AcceptanceCheckResult("owner-protected configuration", true, 0,
                "partition-equivalent", ResultSummary: "partition-equivalent"));
        return new(failure, null);
    }

    private static bool IsEquivalentManifestChange(string worktreePath, IReadOnlyList<string>? changedFiles,
        Func<string, string[], string?> gitText, Func<AcceptanceTestInventory> inventory,
        string candidateRef, string? trusted, string? candidate, string integrationBranch)
    {
        if (changedFiles is null || !changedFiles.Any(path => NormalizePath(path).Equals(
                "config/acceptance-manifest.json", StringComparison.OrdinalIgnoreCase)) ||
            string.IsNullOrWhiteSpace(trusted) || string.IsNullOrWhiteSpace(candidate)) return false;
        try
        {
            var noRename = gitText(worktreePath,
                ["diff", "--name-only", "--no-renames", $"{integrationBranch}...{candidateRef}", "--"]);
            if (noRename is null || changedFiles.Concat(SplitPaths(noRename))
                    .Any(RepositoryChangeClassifier.IsOwnerProtectedPolicyPath))
                return false;
            return AcceptanceManifestPartitionEquivalence.IsEquivalent(trusted, candidate, inventory);
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or
                                      ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsApproved(string worktreePath, IReadOnlyList<string>? changedFiles,
        Func<string, string[], string?> gitText, ICollaborationItemStore decisions,
        GoalId goalId, string? candidateSha, string candidateRef, bool readCommittedCandidate, string integrationBranch)
    {
        if (candidateSha is null) return false;
        if (AcceptancePolicyChangeDecision.IsApprovedAsync(decisions, goalId.Value, candidateSha)
            .GetAwaiter().GetResult()) return true;
        var fingerprint = AcceptanceOwnerProtectedFingerprint.Compute(worktreePath, changedFiles, gitText,
            candidateRef, readCommittedCandidate, integrationBranch);
        return fingerprint is not null && AcceptancePolicyChangeDecision.IsApprovedForFingerprintAsync(
            decisions, goalId.Value, fingerprint).GetAwaiter().GetResult();
    }

    private static bool CohortChangesAreApproved(string worktreePath,
        IReadOnlyList<AcceptanceOwnerProtectedCohortMember> members,
        Func<string, string[], string?> gitText, ICollaborationItemStore? decisions,
        Func<AcceptanceTestInventory> inventory, string integrationBranch, out bool manifestOwnerApproved)
    {
        const string manifest = "config/acceptance-manifest.json";
        manifestOwnerApproved = false;
        try
        {
            var combinedDiff = gitText(worktreePath, ["diff", "--name-only", "--no-renames", $"{integrationBranch}...HEAD", "--"]);
            if (combinedDiff is null) return false;
            var combined = ProtectedPaths(SplitPaths(combinedDiff));
            var changes = new List<(AcceptanceOwnerProtectedCohortMember Member, string[] Files, HashSet<string> Protected)>();
            foreach (var member in members)
            {
                if (!AcceptancePolicyChangeDecision.IsFullSha(member.CandidateSha)) return false;
                var diff = gitText(worktreePath,
                    ["diff", "--name-only", "--no-renames", $"{integrationBranch}...{member.CandidateSha}", "--"]);
                if (diff is null) return false;
                var files = SplitPaths(diff);
                changes.Add((member, files, ProtectedPaths(files)));
            }
            foreach (var path in combined)
            {
                var owners = changes.Where(change => change.Protected.Contains(path)).ToArray();
                if (owners.Length != 1) return false;
                var memberText = gitText(worktreePath, ["show", $"{owners[0].Member.CandidateSha}:{path}"]);
                var combinedText = gitText(worktreePath, ["show", $"HEAD:{path}"]);
                if (!string.Equals(memberText, combinedText, StringComparison.Ordinal)) return false;
            }
            var hasManifestChange = changes.Any(change => change.Protected.Contains(manifest));
            var allManifestChangesApproved = true;
            foreach (var change in changes.Where(change => change.Protected.Count > 0))
            {
                if (change.Protected.Any(path => !combined.Contains(path))) return false;
                if (decisions is not null && IsApproved(worktreePath, change.Files, gitText, decisions, change.Member.GoalId,
                        change.Member.CandidateSha, change.Member.CandidateSha, readCommittedCandidate: true, integrationBranch)) continue;
                if (change.Protected.Contains(manifest)) allManifestChangesApproved = false;
                if (change.Protected.Count == 1 && change.Protected.Contains(manifest) &&
                    IsEquivalentManifestChange(worktreePath, change.Files, gitText, inventory, change.Member.CandidateSha,
                        gitText(worktreePath, ["show", $"{integrationBranch}:{manifest}"]),
                        gitText(worktreePath, ["show", $"{change.Member.CandidateSha}:{manifest}"]), integrationBranch)) continue;
                return false;
            }
            manifestOwnerApproved = combined.Count > 0 && hasManifestChange && allManifestChangesApproved;
            return combined.Count > 0;
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or
                                      ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string[] SplitPaths(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static HashSet<string> ProtectedPaths(IReadOnlyList<string> changedFiles)
    {
        var paths = changedFiles.Where(RepositoryChangeClassifier.IsOwnerProtectedPolicyPath)
            .Select(NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (changedFiles.Any(path => NormalizePath(path).Equals("config/acceptance-manifest.json",
                StringComparison.OrdinalIgnoreCase))) paths.Add("config/acceptance-manifest.json");
        return paths;
    }
}
