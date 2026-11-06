using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// All git-backed observations live here. Every command uses the requested immutable revisions.
internal sealed class AdmissionFactsRepository(string integrationBranch)
{
    private readonly WorkerGitContext _git = new();
    private readonly PassedCohortApprovalHoldEvaluationCache _approval = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _reports = new();
    internal int LastMergeLaunches { get; private set; }

    internal IEnumerable<string> DrainReports()
    {
        while (_reports.TryDequeue(out var report)) yield return report;
    }

    internal IReadOnlyList<AdmissionFacts> Derive(IReadOnlyList<AdmissionFactsInputs> inputs, CancellationToken token)
    {
        LastMergeLaunches = 0;
        var finished = new List<AdmissionFacts>();
        foreach (var group in inputs.GroupBy(input => (input.Repository, input.Main)))
        {
            token.ThrowIfCancellationRequested();
            var members = group.ToArray();
            var started = System.Diagnostics.Stopwatch.StartNew();
            var launchesBefore = LastMergeLaunches;
            var merges = ReadMerges(members, token);
            _reports.Enqueue($"ADMISSION_FACTS_MERGES candidates={members.Length} elapsed_ms={started.ElapsedMilliseconds} git_launches={LastMergeLaunches - launchesBefore}");
            while (_reports.Count > 64) _reports.TryDequeue(out _);
            for (var index = 0; index < members.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var input = members[index];
                if (merges[index] is not { } merge) continue; // ReadMerges already reported this candidate's failure.
                try
                {
                    var paths = ChangedFiles(input.Repository, input.ExecutionHead, input.Branch);
                    var riskPaths = input.Main == input.ExecutionHead ? paths : ChangedFiles(input.Repository, input.Main, input.Branch);
                    ChangeRiskTier? risk = riskPaths.Length == 0 ? null :
                        (ChangeRiskTier)(int)LandingDecisionEngine.ClassifyRisk(RepositoryChangeClassifier.Classify(riskPaths));
                    var decisions = CollaborationItemStore.ForDirectory(
                        OrchestratorWorkspace.ForDirectory(input.Repository).OrchestratorDirectory);
                    var denylist = _approval.LoadDenylist(input.Repository, input.Main);
                    var approval = _approval.Evaluate(input.Repository, denylist, decisions, input.GoalId.Value,
                        input.Main, input.Branch, paths);
                    // Never cache a failed approval observation or an incomplete/stale conflict probe.
                    if (approval.Fingerprint == "unavailable" || approval.HoldReason.Contains(" diagnostic=", StringComparison.Ordinal))
                        throw new InvalidOperationException($"Approval observation unavailable: {approval.HoldReason}");
                    if (AdmissionFactsInputs.ReadApprovals(input.Repository, input.GoalId) != input.Approvals)
                        throw new InvalidOperationException("Approval inputs changed during derivation.");
                    var probe = merge.IsClean ? null : GoalWorktrees.ProbeAdditiveConflictMerge(
                        input.Repository, input.GoalId, JsonSerializer.Deserialize<string[]>(input.FrozenPaths)!, integrationBranch);
                    if (probe is { Outcome: AdditiveConflictProbeOutcome.Indeterminate })
                        throw new InvalidOperationException($"Conflict probe indeterminate: reason={probe.Reason} branch={probe.BranchRevision ?? "unresolved"} main={probe.MainRevision ?? "unresolved"}");
                    if (probe is not null && (probe.BranchRevision != input.Branch || probe.MainRevision != input.Main))
                        throw new InvalidOperationException($"Conflict probe inputs unavailable or changed: reason={probe.Reason} branch={probe.BranchRevision ?? "unresolved"} main={probe.MainRevision ?? "unresolved"}");
                    finished.Add(new(input, new(true, paths), new(merge.IsClean, merge.ConflictPaths), risk, approval, probe));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception exception) { ReportFailure(input, exception); }
            }
        }
        return finished;
    }

    private ReviewerMergeTreeStatus?[] ReadMerges(AdmissionFactsInputs[] members, CancellationToken token)
    {
        try
        {
            return _git.ReadBatchedMergeTreeStatus(members[0].Repository, members[0].Main,
                members.Select(input => input.Branch).ToArray(), launched: () => LastMergeLaunches++).Results
                .Select(result => (ReviewerMergeTreeStatus?)result).ToArray();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            // The batch reader's per-candidate fallback may itself fail. Recover the other
            // members independently so one broken object cannot discard the whole group.
            _reports.Enqueue($"ADMISSION_FACTS_MERGE_RECOVERY candidates={members.Length} detail={exception.Message.ReplaceLineEndings(" ")}");
            return members.Select(input =>
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    return _git.ReadBatchedMergeTreeStatus(input.Repository, input.Main, [input.Branch],
                        launched: () => LastMergeLaunches++).Results[0];
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception failure) { ReportFailure(input, failure); return null; }
            }).ToArray();
        }
    }

    private void ReportFailure(AdmissionFactsInputs input, Exception exception)
    {
        _reports.Enqueue($"ADMISSION_FACTS_FAILED goal={input.GoalId.Value} inputs={input.Identity} error={exception.GetType().Name} detail={exception.Message.ReplaceLineEndings(" ")}");
        while (_reports.Count > 64) _reports.TryDequeue(out _);
    }

    private static string[] ChangedFiles(string directory, string main, string branch)
    {
        var diff = GitCli.Run(directory, "diff", "--name-only", "-z", $"{main}...{branch}");
        if (!diff.Succeeded || diff.DrainTimedOut) throw new InvalidOperationException($"Admission diff failed: {diff.Error}");
        return diff.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
