namespace Mcg.AgentOrchestrator.Core;

public static class SandboxCommitBlockedEvidence
{
    // A Low-IL (sandboxed) worker can do valid work but be structurally unable to self-commit — it
    // cannot write .git (Medium integrity), and therefore cannot run the git-dependent suite to
    // produce verification evidence. That failure is benign: the edits are real, only the commit was
    // blocked by OS confinement. We recognise it (git permission/index.lock signature + the worker
    // having produced useful work) so the orchestrator can commit the edits at Medium and let the
    // acceptance suite — which always runs before any merge to main — be the authoritative gate.
    internal static bool IsSandboxCommitBlockedFailure(TaskSpec task, TaskVerificationRecord verification)
    {
        if (verification.Succeeded || !CanReceiveSandboxCommitBlockedOutcome(task.RequiredRole))
        {
            return false;
        }

        if (HasSandboxCommitBlockedEvidence(verification.StandardOutput, verification.StandardError))
        {
            return true;
        }

        return HasSandboxCommitBlockedEvidence(
            verification,
            includeRetainedText: false);
    }

    public static bool IsSandboxCommitBlockedFailure(
        AgentRole role,
        int exitCode,
        string standardOutput,
        string standardError) =>
        exitCode != 0 &&
        CanReceiveSandboxCommitBlockedOutcome(role) &&
        HasSandboxCommitBlockedEvidence(standardOutput, standardError);

    private static bool CanReceiveSandboxCommitBlockedOutcome(AgentRole role) =>
        DispatchRoleOutputCapabilities.TryGet(role, out var capability) &&
        capability != DispatchRoleOutputCapability.ReadOnly;

    private static bool HasSandboxCommitBlockedEvidence(string standardOutput, string standardError)
    {
        var output = $"{standardOutput}\n{standardError}";
        var commitBlocked =
            output.Contains("index.lock", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("blocked on committing", StringComparison.OrdinalIgnoreCase) ||
            (output.Contains(".git", StringComparison.OrdinalIgnoreCase) &&
             output.Contains("Permission denied", StringComparison.OrdinalIgnoreCase));

        // The worker must have reported doing real work — the WORKER_RESULT contract marker, or any
        // other useful-output signal. The downstream auto-verify additionally requires committed
        // changes against main and the acceptance suite, so this is the lightest of several gates.
        var didUsefulWork =
            standardOutput.Contains("WORKER_RESULT", StringComparison.OrdinalIgnoreCase) ||
            ProviderFailureSignals.HasUsefulPreWorkOutput(standardOutput);

        return commitBlocked && didUsefulWork;
    }

    private static bool HasSandboxCommitBlockedEvidence(TaskVerificationRecord verification, bool includeRetainedText)
    {
        var commitBlocked = false;
        var didUsefulWork = false;
        foreach (var line in DispatchEvidenceSummaries.EnumerateEvidenceLines(
                     verification,
                     includeStandardOutput: true,
                     includeStandardError: true,
                     includeRetainedText: includeRetainedText,
                     includeArtifactText: true))
        {
            commitBlocked |=
                line.Contains("index.lock", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("blocked on committing", StringComparison.OrdinalIgnoreCase) ||
                (line.Contains(".git", StringComparison.OrdinalIgnoreCase) &&
                 line.Contains("Permission denied", StringComparison.OrdinalIgnoreCase));

            didUsefulWork |=
                line.Contains("WORKER_RESULT", StringComparison.OrdinalIgnoreCase) ||
                ProviderFailureSignals.HasUsefulPreWorkOutput(line);

            if (commitBlocked && didUsefulWork)
            {
                return true;
            }
        }

        return false;
    }
}
