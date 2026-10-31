namespace Mcg.AgentOrchestrator.Core;

public static class EmptyOutputDispatchEvidence
{
    public static readonly TimeSpan SilentLaunchFailureMaxDuration = TimeSpan.FromMinutes(1);

    public static bool IsTransientEmptyOutputDispatchFlake(TaskVerificationRecord verification)
    {
        if (DispatchEvidenceSummaries.IsPreflightFailure(verification))
        {
            return false;
        }

        if (IsDispatchRecoveryPolicyDiagnostic(verification))
        {
            return false;
        }

        if (DispatchCompletionEvidence.HasArtifactEvidence(verification.WorkerResultPresent, verification.HasCommittedChanges))
        {
            return false;
        }

        if (IsSilentLaunchFailure(verification))
        {
            return true;
        }

        // exit 0 with evidence the worker actually produced output is never a transient empty-output flake.
        // The heartbeat stdout-byte count is the flush-race-proof signal: a worker that streamed bytes per its
        // heartbeat genuinely ran (the out.log file read can race the exit flush and momentarily report empty,
        // which mis-flaked successful exit-0 workers ~21x — backlog 58407042). A genuine exit-0 STALL reports
        // zero heartbeat bytes and no artifact, so it still falls through to the flake path for failover.
        if (verification.ExitCode == 0 &&
            verification.HeartbeatStandardOutputBytes > 0)
        {
            return false;
        }

        if (HasStandardOutputFileBytes(verification))
        {
            return false;
        }

        if (verification.ExitCode != 0)
        {
            return string.IsNullOrWhiteSpace(verification.StandardOutput) &&
                !HasSubstantiveStandardError(verification.StandardError);
        }

        if (!string.IsNullOrWhiteSpace(verification.StandardOutput) ||
            !string.IsNullOrWhiteSpace(verification.StandardError))
        {
            return false;
        }

        return true;
    }

    public static bool IsSilentLaunchFailure(TaskVerificationRecord verification)
    {
        if (verification.ExitCode == 0 ||
            DispatchEvidenceSummaries.IsPreflightFailure(verification) ||
            IsDispatchRecoveryPolicyDiagnostic(verification) ||
            DispatchFailureClassifier.IsDirtyDispatchGuardFailure(verification) ||
            DispatchCompletionEvidence.HasArtifactEvidence(verification.WorkerResultPresent, verification.HasCommittedChanges) ||
            !HasZeroByteStandardOutput(verification) ||
            !HasZeroByteStandardError(verification))
        {
            return false;
        }

        if (verification.DispatchStartedAt is not { } startedAt)
            return false;

        var duration = verification.CompletedAt - startedAt;
        return duration >= TimeSpan.Zero && duration <= SilentLaunchFailureMaxDuration;
    }

    private static bool IsDispatchRecoveryPolicyDiagnostic(TaskVerificationRecord verification) =>
        verification.StandardError.Contains("Dispatch recovery policy action='mark-stale'", StringComparison.Ordinal) ||
        verification.StandardError.Contains("Dispatch recovery policy action='retry-stale'", StringComparison.Ordinal) ||
        verification.StandardError.Contains("Dispatch recovery policy action='budget-exhausted'", StringComparison.Ordinal);

    internal static bool HasZeroByteStandardOutput(TaskVerificationRecord verification)
    {
        if (!string.IsNullOrWhiteSpace(verification.StandardOutputPath) &&
            File.Exists(verification.StandardOutputPath))
        {
            return new FileInfo(verification.StandardOutputPath).Length == 0;
        }

        return verification.StandardOutput.Length == 0;
    }

    internal static bool HasStandardOutputFileBytes(TaskVerificationRecord verification)
    {
        if (string.IsNullOrWhiteSpace(verification.StandardOutputPath) ||
            !File.Exists(verification.StandardOutputPath))
        {
            return false;
        }

        return new FileInfo(verification.StandardOutputPath).Length > 0;
    }

    private static bool HasZeroByteStandardError(TaskVerificationRecord verification)
    {
        // The redirected file is authoritative for worker bytes, but the verification text can
        // contain orchestrator-injected diagnostics that never appeared in that file. Positive
        // failure evidence must win over an empty file; bookkeeping receipts do not constitute a
        // worker verdict and are deliberately ignored here.
        if (HasSubstantiveStandardError(verification.StandardError))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(verification.StandardErrorPath) &&
            File.Exists(verification.StandardErrorPath))
        {
            return new FileInfo(verification.StandardErrorPath).Length == 0;
        }

        return true;
    }

    private static bool HasSubstantiveStandardError(string standardError)
    {
        if (string.IsNullOrWhiteSpace(standardError))
        {
            return false;
        }

        return standardError
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(line => !IsRunnerBookkeepingLine(line));
    }

    internal static bool IsRunnerBookkeepingLine(string line) =>
        line.StartsWith("RESOURCE ", StringComparison.Ordinal) ||
        line.StartsWith("CLASSIFIER ", StringComparison.Ordinal) ||
        line.StartsWith(DispatchFailureDiagnosticMarker.Prefix, StringComparison.Ordinal) ||
        line.StartsWith(DispatchRejectionDiagnosticMarker.Prefix, StringComparison.Ordinal) ||
        line.StartsWith("Dispatch recovery policy action=", StringComparison.Ordinal) ||
        line.StartsWith("Developer/Tester dispatch did not produce required relevant file-change evidence.", StringComparison.Ordinal);
}
