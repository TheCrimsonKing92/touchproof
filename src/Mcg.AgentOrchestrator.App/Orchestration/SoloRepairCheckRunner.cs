using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record SoloRepairCheckRequest(string Key, SoloRepairFinding Finding, string CheckoutSha,
    string BaselineSha, bool RunBaseline, bool AllowCandidatePass, string ProofIdentity);

internal static class SoloRepairCheckRunner
{
    internal static SoloRepairCheckReceipt Run(OrchestratorWorkspace workspace, SoloBaselineRepairStore store,
        IGoalAcceptanceVerifier verifier, Func<string, CancellationToken, DotnetBuildEnvironmentLease> acquireStableSlot,
        SoloRepairCheckRequest request)
    {
        var (key, finding, checkoutSha, baselineSha, runBaseline, allowCandidatePass, proofIdentity) = request;
        using var ownership = OpenOwnership(workspace.SqliteStatePath, key);
        if (!TryTakeOwnership(ownership)) return new("pending", key, []);
        try
        {
            if (store.ReadVerifiedCheck(key, proofIdentity) is { } completed) return completed;
            var checkout = Path.Combine(OrchestratorTempRoot.GetPurposeDirectory("solo-repair-worktrees"),
                SoloBaselineRepairStore.Key(workspace.SqliteStatePath, key));
            var artifacts = store.ResultDirectory(key);
            // The mutex owns execution. Recover the verifier's terminal output before
            // treating a claim with no SQLite execution record as an interrupted run.
            var staged = store.ReadCheckExecution(key);
            // Keep the envelope reference so a replay can still bind a legacy charge to its selection.
            var result = staged?.Proof is { } proof
                ? (ReadSoloRepairResult(artifacts, proofIdentity, proof.Sha256) ??
                    new SoloRepairCheckReceipt("unresolved", "The staged focused verifier result is missing.", []))
                    with { Proof = proof }
                : staged ?? ReadSoloRepairResult(artifacts, proofIdentity);
            RemoveSoloRepairCheckout(workspace.ExecutionDirectory, checkout);
            if (result is null)
            {
                try
                {
                    using var lease = acquireStableSlot(key, CancellationToken.None);
                    store.TryClaimCheck(key);
                    result = ExecuteSoloRepairCheck(workspace, store, verifier, key, finding, checkout, checkoutSha, baselineSha, runBaseline,
                        allowCandidatePass, artifacts, proofIdentity, lease);
                }
                catch (Exception ex) when (ex is DotnetBuildSlotsBusyException or CohortStableSlotsDeferredException)
                {
                    return new("pending", key, []);
                }
            }
            store.CompleteCheck(key, result);
            return result;
        }
        finally { ownership.ReleaseMutex(); }
    }

    private static SoloRepairCheckReceipt ExecuteSoloRepairCheck(OrchestratorWorkspace workspace,
        SoloBaselineRepairStore store, IGoalAcceptanceVerifier verifier, string key, SoloRepairFinding finding,
        string checkout, string checkoutSha, string baselineSha, bool runBaseline, bool allowCandidatePass, string artifacts,
        string proofIdentity, DotnetBuildEnvironmentLease lease)
    {
        var root = workspace.ExecutionDirectory;
        try
        {
            SoloRepairCheckReceipt? receipt = null;
            var observedResult = false;
            try
            {
                Directory.CreateDirectory(artifacts);
                using var qos = HostedVerifierRunQos.Begin(workspace);
                var added = GitCli.Run(root, "worktree", "add", "--detach", "--quiet", checkout, checkoutSha);
                if (!added.Succeeded || added.DrainTimedOut || GoalWorktrees.ResolveRequiredRef(checkout, "HEAD") != checkoutSha)
                    throw new InvalidOperationException($"Could not prepare exact revision {checkoutSha} for request {key}.");
                var focusedFinding = SoloAcceptanceFailureReceiptReader.BindRetainedRepairRequest(finding);
                AcceptanceExecutionRunner.RunFocusedVerification(verifier, checkout, goalId: null, focusedFinding.Request!,
                    lease.Environment.BuildPermitIndex, lease, runBaseline, CancellationToken.None, workspace.IntegrationBranch,
                    projectHomeDirectory: workspace.ProjectHomeDirectoryOrNull, retainFocusedBaselineArtifacts: runBaseline,
                    observeFocusedResult: result =>
                    {
                        observedResult = true;
                        SoloRepairCheckReceipt interpreted;
                        try
                        {
                            interpreted = runBaseline
                                ? SoloAcceptanceFailureReceiptReader.ReadRepairAttribution(result, checkoutSha, baselineSha,
                                    focusedFinding, artifacts, allowCandidatePass)
                                : SoloAcceptanceFailureReceiptReader.ReadRepairCheck(result, FindingEvidenceArm.Candidate,
                                    checkoutSha, focusedFinding, artifacts);
                        }
                        catch (Exception ex) when (SoloAcceptanceFailureReceiptReader.IsRepairEvidenceError(ex))
                        {
                            interpreted = new("unresolved", ex.Message, []);
                        }
                        receipt = WriteSoloRepairResult(artifacts, proofIdentity, interpreted);
                    });
            }
            catch (Exception ex) when (!observedResult && ex is not DotnetBuildSlotsBusyException &&
                SoloAcceptanceFailureReceiptReader.IsRepairEvidenceError(ex))
            {
                receipt = new("unresolved", ex.Message, []);
            }
            // Record verifier evidence before releasing the checkout or acknowledging the check.
            store.RecordCheckExecution(key, receipt!);
            return receipt!;
        }
        finally { RemoveSoloRepairCheckout(root, checkout); }
    }

    private static SoloRepairCheckReceipt WriteSoloRepairResult(string resultsRoot, string identity, SoloRepairCheckReceipt result)
    {
        var path = Path.Combine(resultsRoot, "focused-result.json");
        var temporary = path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new SoloRepairVerifierResult(identity, result, ProjectBound: true));
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
        return result with { Proof = new(path, SoloBaselineRepairStore.Digest(bytes)) };
    }

    private static SoloRepairCheckReceipt? ReadSoloRepairResult(string resultsRoot, string identity, string? expectedDigest = null)
    {
        var path = Path.Combine(resultsRoot, "focused-result.json");
        try
        {
            return SoloBaselineRepairStore.ReadVerifierResult(path, resultsRoot, identity, expectedDigest);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException && !File.Exists(path))
        {
            if (expectedDigest is not null) return null;
            var temporary = path + ".tmp";
            SoloRepairCheckReceipt recovered;
            try { recovered = SoloBaselineRepairStore.ReadVerifierResult(temporary, resultsRoot, identity); }
            catch (Exception temporaryError) when (SoloAcceptanceFailureReceiptReader.IsRepairEvidenceError(temporaryError))
            {
                // A partial write never acknowledged an execution. Reuse its existing claim.
                return null;
            }
            File.Move(temporary, path);
            return recovered with { Proof = recovered.Proof! with { Path = path } };
        }
        catch (Exception ex) when (SoloAcceptanceFailureReceiptReader.IsRepairEvidenceError(ex))
        {
            return new("unresolved", ex.Message, []);
        }
    }

    private static void RemoveSoloRepairCheckout(string root, string checkout)
    {
        if (Directory.Exists(checkout) &&
            GitCli.Run(root, "worktree", "remove", "--force", checkout) is not { Succeeded: true, DrainTimedOut: false })
            throw new InvalidOperationException($"Could not remove solo repair checkout {checkout}.");
    }

    internal static Mutex OpenOwnership(string statePath, string key) =>
        new(false, "mcg-solo-repair-" + SoloBaselineRepairStore.Key(statePath, key));

    internal static bool TryTakeOwnership(Mutex ownership)
    {
        try { return ownership.WaitOne(0); }
        catch (AbandonedMutexException) { return true; }
    }
}
