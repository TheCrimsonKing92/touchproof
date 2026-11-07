using Mcg.AgentOrchestrator.Core;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using CommandResult = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.CommandResult;
using DotnetTestTelemetry = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.DotnetTestTelemetry;
using FocusedEvidenceSelectionCoverage = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceSelectionCoverage;
using FocusedEvidenceIdentityExtraction = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceIdentityExtraction;
using FocusedEvidenceFilterToken = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceFilterToken;
using FocusedEvidenceTokenKind = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceTokenKind;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class GoalAcceptanceVerifierTestTelemetry
{
    internal static AcceptanceCheckResult WithNormalizedTrxResult(AcceptanceCheckResult receipt) =>
        receipt.TestResultPaths is [var trxPath]
            ? NormalizedGateResultStep.Apply(receipt, trxPath, NormalizedGateResultStep.TrxSourceLabel) : receipt;

    internal static DotnetTestTelemetry? ResolveDotnetTestTelemetry(
        string[] arguments,
        AcceptanceManifestCheck check,
        DotnetBuildEnvironment environment,
        string? attemptResultsPrefix,
        AcceptanceInvocationContext? invocationContext)
    {
        if (!GoalAcceptanceVerifier.IsDotnetTestCommand(arguments))
        {
            return null;
        }

        return ResolveTestTelemetry(check, environment, attemptResultsPrefix, invocationContext) with
        {
            Arguments = AddVstestTelemetryArguments(arguments, check, environment, attemptResultsPrefix, invocationContext)
        };
    }

    internal static DotnetTestTelemetry ResolveTestTelemetry(
        AcceptanceManifestCheck check,
        DotnetBuildEnvironment environment,
        string? attemptResultsPrefix,
        AcceptanceInvocationContext? invocationContext,
        string? resultsDirectoryOverride = null) =>
        AcceptanceTestTelemetryResolver.Resolve(
            check, environment, attemptResultsPrefix, invocationContext, resultsDirectoryOverride);

    internal static void PrepareTestTelemetryForRun(DotnetTestTelemetry telemetry) =>
        AcceptanceTestRunPreparation.Prepare(telemetry.Paths);

    internal static IReadOnlyList<string>? CopyCompletedTestReceiptsToAttemptFolder(
        IReadOnlyList<string>? sourcePaths,
        string? attemptPrefix)
    {
        if (sourcePaths is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(attemptPrefix))
        {
            return sourcePaths;
        }

        var receiptDirectory = Path.GetDirectoryName(attemptPrefix);
        if (string.IsNullOrWhiteSpace(receiptDirectory))
        {
            throw new InvalidOperationException(
                $"Acceptance attempt TRX prefix '{attemptPrefix}' does not identify an attempt receipt folder.");
        }

        var durablePaths = new string[sourcePaths.Count];
        for (var index = 0; index < sourcePaths.Count; index++)
        {
            var sourcePath = sourcePaths[index];
            if (!File.Exists(sourcePath))
            {
                durablePaths[index] = sourcePath;
                continue;
            }

            var destinationPath = Path.Combine(receiptDirectory, Path.GetFileName(sourcePath));
            try
            {
                Directory.CreateDirectory(receiptDirectory);
                if (!sourcePath.Equals(destinationPath, StringComparison.OrdinalIgnoreCase))
                {
                    var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
                    File.Copy(sourcePath, temporaryPath, overwrite: true);
                    File.Move(temporaryPath, destinationPath, overwrite: true);
                }

                LaneSlotTimingReader.CopyBeside(sourcePath, destinationPath);
                durablePaths[index] = destinationPath;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"Failed to preserve completed test receipt from '{sourcePath}' to attempt folder '{destinationPath}'.",
                    ex);
            }
        }

        return durablePaths;
    }

    internal static string[] AddVstestTelemetryArguments(
        string[] arguments,
        AcceptanceManifestCheck check,
        DotnetBuildEnvironment environment,
        string? attemptResultsPrefix,
        AcceptanceInvocationContext? invocationContext)
    {
        var telemetry = ResolveTestTelemetry(check, environment, attemptResultsPrefix, invocationContext);
        var directory = Path.GetDirectoryName(telemetry.Paths[0]) ?? Path.Combine(environment.ArtifactsPath, "TestResults");
        var fileName = Path.GetFileName(telemetry.Paths[0]);
        var effectiveArguments = new List<string>(arguments)
        {
            "--logger",
            $"trx;LogFileName={fileName}",
            "--results-directory",
            directory
        };

        return [.. effectiveArguments];
    }

    internal static void EmitMissingTrxReceiptIfNeeded(bool passed, DotnetTestTelemetry? telemetry)
    {
        if (!passed || telemetry is null)
        {
            return;
        }

        var missing = telemetry.Paths
            .Where(path => AcceptanceBestEffortFileOperations.TryGetFileLength(path) <= 0)
            .ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        Console.WriteLine($"TRX_TELEMETRY_UNAVAILABLE paths={GoalAcceptanceVerifier.QuoteProgressToken(string.Join(";", missing))}");
        Console.Out.Flush();
    }

    internal static IReadOnlyList<string> ExtractTrxFailureIdentities(IEnumerable<string>? trxPaths)
    {
        if (trxPaths is null)
        {
            return [];
        }

        var identities = new List<string>();
        foreach (var trxPath in trxPaths.Where(File.Exists))
        {
            try
            {
                identities.AddRange(AcceptanceTrxTestIdentityResolver.ExtractTrxFailureIdentities(trxPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                // An unreadable receipt is evidence-inconclusive. Never infer a code failure from its output tail.
            }
        }

        return identities
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    internal static TrxCompletionEvidence InspectTrxCompletionEvidence(IEnumerable<string>? trxPaths) =>
        AcceptanceShardCompletionAdjudicator.InspectTrxCompletion(trxPaths);

    internal static AcceptanceShardCompletionDecision DecideTestShardCompletion(
        CommandResult result,
        TrxCompletionEvidence trx,
        string? policyFailure = null,
        string? policySignal = null,
        bool allowNonzeroExit = false,
        bool requireTrxEvidence = true,
        bool allowEmptySelection = false) =>
        AcceptanceShardCompletionAdjudicator.DecideTestShard(
            GoalAcceptanceVerifier.IsInterrupted(result),
            result.ExitCode,
            trx,
            policyFailure,
            policySignal,
            allowNonzeroExit,
            requireTrxEvidence, allowEmptySelection);

    internal static AcceptanceShardCompletionDecision DecideNonTestCommandCompletion(CommandResult result) =>
        AcceptanceShardCompletionAdjudicator.DecideNonTestCommand(
            GoalAcceptanceVerifier.IsInterrupted(result), result.ExitCode);

    internal static FocusedEvidenceSelectionCoverage InspectFocusedEvidenceSelectionCoverage(
        IReadOnlyList<IReadOnlyList<FocusedEvidenceFilterToken>> selections,
        IEnumerable<string>? trxPaths) =>
        FocusedEvidenceSelectionCoverageInspector.Inspect(selections, trxPaths);

    internal static FocusedEvidenceIdentityExtraction ExtractTrxExecutedTestIdentities(IEnumerable<string>? trxPaths)
        => AcceptanceTrxExecutedTestIdentityExtractor.Extract(trxPaths);

    internal static bool IsFocusedEvidenceIdentityMatch(string identity, string selection) =>
        identity.Contains(selection, StringComparison.OrdinalIgnoreCase);
}
