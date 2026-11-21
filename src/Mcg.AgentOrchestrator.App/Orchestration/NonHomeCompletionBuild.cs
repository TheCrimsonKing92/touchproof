using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class NonHomeCompletionBuild
{
    internal static CompletionBuildPlacementResult Run(CompletionBuildEvidenceOperation operation, string receiptPath)
    {
        var root = operation.WorktreeRoot;
        var targets = operation.BuildTargets!;
        if (targets.Count == 0)
            throw new InvalidOperationException("Completion build has no selected targets.");
        var selection = new DotnetProjectDiscoveryAdapter().SelectBuildTargets(root);
        if (selection.Error is { } error) throw new IOException(error);
        if (!selection.WithRequiredProjects(operation.Projects).Order(StringComparer.Ordinal)
                .SequenceEqual(targets.Order(StringComparer.Ordinal)))
            return new(CompletionBuildEvidencePhase.StaleCandidate, -1, "Selected targets changed before completion build.", "", "");
        var storage = DotnetBuildStorageLayout.CaptureStorageRoot();
        var environment = DotnetBuildEnvironmentManager.CreateAttempt(new GoalId(operation.GoalId),
            "completion-build-" + operation.Id, storageRoot: storage, repositoryRoot: root);
        var acquisition = DotnetBuildEnvironmentManager.TryAcquireFirstAvailableBuildPermit(environment, Remaining(),
            claimant: new BuildPermitClaimant($"completion-build:{operation.Id}",
                BuildPermitClaimantKind.WorkerBuild, Environment.ProcessId, OwnerBound: true));
        if (acquisition is not DotnetBuildLeaseAcquisition.Acquired acquired)
            throw new IOException("Completion build could not acquire a host build permit.");
        using var slot = acquired.Lease;
        var logRoot = Path.Combine(Path.GetDirectoryName(operation.ResultPath)!, operation.Id);
        var tempRoot = Path.Combine(slot.Environment.ArtifactsPath, "tmp", operation.Id);
        Directory.CreateDirectory(logRoot);
        Directory.CreateDirectory(tempRoot);
        var logs = new List<string>();
        var gcEnvironment = BuildGcEnvironment.ForStatePath(operation.StatePath);
        for (var index = 0; index < targets.Count; index++)
        {
            if (!WorktreeTreeDigest.TryCompute(root, out var digest, out _) || digest != operation.TreeDigest)
                return new(CompletionBuildEvidencePhase.StaleCandidate, -1, "Candidate changed during completion build.", "", "");
            var target = targets[index];
            var info = WorkerBuildRequestHost.BuildStartInfo(root, target, "Debug", slot.Environment.Arguments, tempRoot, gcEnvironment);
            var stdout = Path.Combine(logRoot, $"{index}.out.log");
            var stderr = Path.Combine(logRoot, $"{index}.err.log");
            using var child = OwnedProcessGroup.StartSuspendedContainedWithFileCapture(info, stdout, stderr);
            child.Resume();
            var exited = child.WaitForOwnedExit(Remaining());
            if (!child.Group.TerminateAndConfirmEmpty(TimeSpan.FromSeconds(10)))
                throw new IOException("Owned completion build descendants did not become terminal.");
            if (!exited) throw new OperationCanceledException();
            var exitCode = child.TryReadOwnedExitCode() ?? throw new IOException("Owned compiler exit was not observed.");
            if (exitCode != 0)
            {
                var errors = WorkerBuildLogErrorReader.Read([stdout, stderr]);
                var diagnostic = $"dotnet build '{target}' exited {exitCode}; stdout={stdout}; stderr={stderr}";
                if (errors is { Total: > 0 }) diagnostic += Environment.NewLine + errors.Format(10);
                return new(CompletionBuildEvidencePhase.CompilerFailure, exitCode, diagnostic, "", "");
            }
            logs.Add(stdout);
        }

        if (!WorktreeTreeDigest.TryCompute(root, out var finalDigest, out _) || finalDigest != operation.TreeDigest)
            return new(CompletionBuildEvidencePhase.StaleCandidate, -1, "Candidate changed during completion build.", "", "");
        var summaryPath = operation.ResultPath + ".stdout";
        var summary = "PASS build: 0 errors; targets=" + string.Join(", ", targets);
        File.WriteAllLines(summaryPath, logs.Prepend(summary));
        WriteReceipt(operation, receiptPath, summaryPath);
        var verdict = WorkerBuildReceipt.Evaluate(receiptPath, root);
        if (!verdict.Matches || WorkerBuildReceiptCoverage.FindMissingProjects(operation.Projects, verdict.Projects).Count != 0)
            throw new IOException("Completion build did not produce exact required receipt coverage: " + verdict.Reason);
        return new(CompletionBuildEvidencePhase.Success, 0, summary, summary, "");

        TimeSpan Remaining()
        {
            var remaining = operation.Deadline - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : throw new OperationCanceledException();
        }
    }

    private static void WriteReceipt(CompletionBuildEvidenceOperation operation, string receiptPath, string logPath)
    {
        var receipt = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["treeDigest"] = operation.TreeDigest,
            ["finalTreeDigest"] = operation.TreeDigest,
            ["digestAlgorithm"] = WorktreeTreeDigest.Algorithm,
            ["buildOutcome"] = "success",
            ["generatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["worktreeRoot"] = operation.WorktreeRoot,
            ["configuration"] = "Debug",
            ["projects"] = JsonSerializer.SerializeToNode(operation.Projects),
            ["projectResults"] = JsonSerializer.SerializeToNode(operation.Projects.Select(project => new
            {
                project, exitCode = 0, logPath
            }))
        };
        if (File.Exists(WorkerBuildProjectScope.MetadataPath(operation.WorktreeRoot)))
        {
            var scope = WorkerBuildProjectScope.Resolve(operation.WorktreeRoot, []);
            receipt["scopeMetadataDigest"] = scope.MetadataDigest;
            receipt["scopeStableDigest"] = scope.StableMetadataDigest;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        var temporary = receiptPath + "." + operation.Id + ".tmp";
        try
        {
            File.WriteAllText(temporary, receipt.ToJsonString());
            File.Move(temporary, receiptPath, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
