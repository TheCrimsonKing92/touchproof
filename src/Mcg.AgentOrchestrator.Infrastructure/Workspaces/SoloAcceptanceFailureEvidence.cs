using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record SoloAcceptanceCheckBinding(string CheckName, string TestIdentity, string BaselineCheckName);
internal sealed record SoloAcceptanceCheckEvidence(AcceptanceCheckResult Check,
    IReadOnlyList<SoloAcceptanceArtifact> Artifacts,
    IReadOnlyDictionary<string, SoloAcceptanceArtifact> CandidateReruns);
internal sealed record SoloAcceptanceFailureEvidence(
    SoloAcceptanceFailureReceipt Identity,
    IReadOnlyList<SoloAcceptanceCheckEvidence> Checks,
    FocusedEvidenceArmRunResult Baseline,
    IReadOnlyList<SoloAcceptanceCheckEvidence> BaselineChecks,
    IReadOnlyList<SoloAcceptanceCheckBinding> Bindings);

internal static class SoloAcceptanceFailureEvidenceWriter
{
    // Called while baseline artifacts still exist. Each arm has its own owned
    // destination; baseline cleanup can then safely delete its environment.
    internal static FocusedEvidenceArmRunResult RetainBaseline(FocusedEvidenceArmRunResult arm, string root) =>
        arm with { Checks = arm.Checks.Select(check => check with
        {
            TestResultPaths = (check.TestResultPaths ?? []).Select(path => Copy(path, root).Path).ToArray()
        }).ToArray() };

    internal static SoloAcceptanceFailureReceipt Capture(AcceptanceAttemptIdentity identity,
        string baselineSha, IReadOnlyList<AcceptanceCheckResult> checks,
        FocusedEvidenceArmRunResult baseline, IReadOnlyList<SoloAcceptanceCheckBinding> bindings)
    {
        var root = Path.Combine(identity.ResultsPrefix, "solo-failure");
        var receipt = new SoloAcceptanceFailureReceipt(1, identity.GoalId, identity.AttemptId,
            identity.VerifyingCommitSha, identity.CandidateTreeSha, identity.MainSha, baselineSha,
            root, new(Path.Combine(root, "proof.json"), ""));
        try
        {
            SoloAcceptanceCheckEvidence Retain(AcceptanceCheckResult check, string arm) => new(check,
                (check.TestResultPaths ?? []).Select(path => Copy(path, Path.Combine(root, arm))).ToArray(),
                (check.FailingTestAttributions ?? []).Where(item => item.CandidateRerun?.ReceiptPointer is not null)
                    .ToDictionary(item => item.TestIdentity,
                        item => Copy(item.CandidateRerun!.ReceiptPointer!, Path.Combine(root, "rerun")), StringComparer.Ordinal));
            var evidence = new SoloAcceptanceFailureEvidence(receipt,
                checks.Where(check => !check.Passed && !check.Advisory).Select(check => Retain(check, "candidate")).ToArray(),
                baseline, baseline.Checks.Select(check => Retain(check, "baseline")).ToArray(), bindings);
            Directory.CreateDirectory(root);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(evidence);
            File.WriteAllBytes(receipt.Evidence.Path, bytes);
            return receipt with { Evidence = receipt.Evidence with { Sha256 = Digest(bytes) } };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return receipt with { CaptureError = ex.GetType().Name + ": " + ex.Message };
        }
    }

    private static SoloAcceptanceArtifact Copy(string path, string root)
    {
        var bytes = File.ReadAllBytes(path);
        var digest = Digest(bytes);
        Directory.CreateDirectory(root);
        var retained = Path.Combine(root, digest + ".trx");
        File.WriteAllBytes(retained, bytes);
        return new(retained, digest);
    }

    internal static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

/// <summary>Owns attempt-scoped evidence capture and its binding to the acceptance result.</summary>
internal sealed class SoloAcceptanceFailureCapture(AcceptanceAttemptIdentity identity)
{
    internal SoloAcceptanceFailureReceipt? Receipt { get; private set; }

    internal void Capture(IReadOnlyList<AcceptanceCheckResult> checks,
        FocusedEvidenceArmRunResult baseline, IReadOnlyList<SoloAcceptanceCheckBinding> bindings) =>
        Receipt = SoloAcceptanceFailureEvidenceWriter.Capture(identity, baseline.Sha, checks, baseline, bindings);

    internal AcceptanceVerificationResult AttachTo(AcceptanceVerificationResult result) => result with
    {
        AttemptId = identity.AttemptId,
        SoloFailureReceipt = Receipt,
        // Entering solo attribution owns the evidence obligation even if preparation
        // or baseline execution returned before the capture callback could run.
        RequiresSoloFailureReceipt = true
    };
}
