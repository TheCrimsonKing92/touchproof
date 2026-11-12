namespace Mcg.AgentOrchestrator.Core;

public enum WorkerWriteIntegrityLabel
{
    Other,
    Unlabeled,
    Low,
    Medium
}

// Query-derived facts about one denied-write target, carried only within the current round.
public sealed record WorkerWriteIntegrityObservation(
    string WorktreeRelativePath,
    WorkerWriteIntegrityLabel ObservedLabel,
    bool LabelQuerySucceeded)
{
    public string? ReportedPath { get; init; }
    public string? OsDiagnostic { get; init; }
    public int? NativeQueryError { get; init; }
    public string? NativeDiagnostic { get; init; }
    public bool EstablishesAboveLow => LabelQuerySucceeded && ObservedLabel == WorkerWriteIntegrityLabel.Medium;
}

// The association applies to every member; an observation still means exactly one native target.
public sealed record WorkerWriteIntegrityEvidence(
    TaskId TaskId,
    DateTimeOffset DispatchedAt,
    DateTimeOffset DispatchStartedAt,
    string? WorktreeHeadSha,
    string? DirtyStateHash,
    string ReportedBlocker,
    IReadOnlyList<WorkerWriteIntegrityObservation> Observations,
    WorkerLowIntegrityLaunchEvidence? LowLaunch = null,
    string? EvidenceGap = null)
{
    public bool EstablishesAllTargets(TaskSpec task, TaskVerificationRecord verification)
    {
        var dispatch = task.LastDispatch;
        var launch = LowLaunch;
        if (dispatch is null || launch is null || verification.EvidenceSource != VerificationEvidenceSource.Dispatch ||
            verification.WriteIntegrityEvidenceConflict || !string.IsNullOrWhiteSpace(EvidenceGap) ||
            TaskId != task.Id || DispatchedAt != dispatch.DispatchedAt ||
            (verification.DispatchAttemptAt is { } attemptAt && attemptAt != DispatchedAt) ||
            verification.DispatchStartedAt != DispatchStartedAt ||
            DispatchStartedAt < DispatchedAt || DispatchStartedAt > verification.CompletedAt ||
            string.IsNullOrWhiteSpace(WorktreeHeadSha) || string.IsNullOrWhiteSpace(DirtyStateHash) ||
            WorktreeHeadSha != dispatch.WorktreeHeadSha || DirtyStateHash != dispatch.DirtyStateHash ||
            verification.Command != dispatch.Command || verification.WorkingDirectory != dispatch.WorkingDirectory ||
            verification.CandidateIdentity is not null && dispatch.CandidateIdentity is not null &&
                !CandidateIdentity.AreSameCandidate(verification.CandidateIdentity, dispatch.CandidateIdentity) ||
            launch.TaskId != TaskId || launch.DispatchedAt != DispatchedAt ||
            launch.DispatchStartedAt != DispatchStartedAt || launch.WorktreeHeadSha != WorktreeHeadSha ||
            launch.DirtyStateHash != DirtyStateHash || launch.ObservedIntegrityRid != 4096 ||
            launch.ProcessId <= 0 || launch.CompletionChildProcessId is <= 0 ||
            (launch.CompletionChildProcessId ?? launch.ProcessId) != verification.ChildProcessId ||
            launch.ProcessStartedAt < DispatchStartedAt || launch.ProcessStartedAt > verification.CompletedAt ||
            string.IsNullOrWhiteSpace(launch.ImagePath) || string.IsNullOrWhiteSpace(launch.NativeDiagnostic) ||
            // This proof uses only the current worker response, independently of shared evidence-reader modes.
            !WorkerResultBlockers.TryFindBlocker(verification.AuthoritativeStandardOutput ?? string.Empty, out var blocker) ||
            blocker != ReportedBlocker ||
            !ReportedBlocker.StartsWith("exact-blocker - ", StringComparison.OrdinalIgnoreCase) ||
            Observations is not { Count: > 0 and <= 8 } ||
            verification.WriteIntegrityObservation is { } singular &&
                (Observations.Count != 1 || singular != Observations[0]))
            return false;

        var targets = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        return Observations.All(observation => observation is { EstablishesAboveLow: true } &&
            !string.IsNullOrWhiteSpace(observation.WorktreeRelativePath) && targets.Add(observation.WorktreeRelativePath));
    }
}

// Only a host native execution observation can populate this proof. A requested sandbox is not proof.
// The launched worker and the child selected for exit diagnostics can be different processes.
public sealed record WorkerLowIntegrityLaunchEvidence(
    TaskId TaskId,
    DateTimeOffset DispatchedAt,
    DateTimeOffset DispatchStartedAt,
    string WorktreeHeadSha,
    string DirtyStateHash,
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    string ImagePath,
    int ObservedIntegrityRid,
    string NativeDiagnostic,
    int? CompletionChildProcessId = null);
