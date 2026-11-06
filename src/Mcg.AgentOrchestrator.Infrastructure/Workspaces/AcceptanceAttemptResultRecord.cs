using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.Infrastructure;

// The owner of the results prefix is the sole writer of its terminal record.
internal static class AcceptanceAttemptResultRecord
{
    internal const string GateDispatchKind = "gate";

    internal static void Write(string path, ConductorParallelAcceptanceRunArtifact artifact)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                using var writer = new StreamWriter(stream, leaveOpen: true);
                writer.Write(JsonSerializer.Serialize(artifact,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static ConductorParallelAcceptanceRunArtifact ForVerification(
        AcceptanceVerificationResult result, string? branchHeadSha, string? mainHeadSha) =>
        new("accepted", null, null, null, null,
            AcceptanceVerificationSummary.FromVerification(result,
                result.Checks?.Where(check => !check.Passed).ToArray() ?? [],
                result.Checks?.Where(check => !check.Passed && !check.Advisory)
                    .Select(check => check.Name).ToArray() ?? [],
                branchHeadSha, mainHeadSha, result.TestResultPaths, null, null),
            branchHeadSha, mainHeadSha, null, null);

    internal static ConductorParallelAcceptanceRunArtifact ForFault(
        Exception exception, string message, string? branchHeadSha, string? mainHeadSha) =>
        new("fault", exception switch
        {
            AcceptanceExecutionIdentityChangedException { IsChangedIdentity: true } => "identity-stale",
            DotnetBuildSlotsBusyException => "blocked-build-slot",
            BuildLockBlockedException => "blocked-build-lock",
            AcceptanceInfrastructureDeferredException deferred when
                deferred.ReasonCode == "structural-coverage-permit-unavailable" =>
                "structural-coverage-permit-unavailable",
            AcceptanceInfrastructureDeferredException => "infrastructure-deferred",
            AcceptanceGateEngineException => "gate-engine-fault",
            OperationCanceledException => "cancelled",
            _ => "exception"
        }, message, null, null, null, branchHeadSha, mainHeadSha, null, null,
            InfrastructureReasonCode: (exception as AcceptanceInfrastructureDeferredException)?.ReasonCode,
            InfrastructureExitCode: (exception as AcceptanceInfrastructureDeferredException)?.ExitCode,
            InfrastructureOutputTail: (exception as AcceptanceInfrastructureDeferredException)?.OutputTail,
            InfrastructureBuildLockAttribution: (exception as AcceptanceInfrastructureDeferredException)?.BuildLockAttribution,
            FaultType: (exception as AcceptanceGateEngineException)?.FaultType,
            FaultStack: (exception as AcceptanceGateEngineException)?.FaultStack,
            GatePhase: (exception as AcceptanceGateEngineException)?.GatePhase,
            GateTarget: (exception as AcceptanceGateEngineException)?.GateTarget);
}
