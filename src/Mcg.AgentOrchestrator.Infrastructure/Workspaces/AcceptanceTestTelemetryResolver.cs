using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using DotnetTestTelemetry = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.DotnetTestTelemetry;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Owns attempt- and invocation-scoped TRX paths, including the legacy artifact-root fallback.
internal static class AcceptanceTestTelemetryResolver
{
    internal static DotnetTestTelemetry Resolve(
        AcceptanceManifestCheck check,
        DotnetBuildEnvironment environment,
        string? attemptPrefix,
        AcceptanceInvocationContext? invocationContext,
        string? resultsDirectoryOverride = null) =>
        Resolve(check, environment, attemptPrefix, invocationContext?.Ordinal ?? 0, resultsDirectoryOverride);

    internal static DotnetTestTelemetry Resolve(
        AcceptanceManifestCheck check,
        DotnetBuildEnvironment environment,
        string? attemptPrefix,
        int invocationOrdinal,
        string? resultsDirectoryOverride = null)
    {
        var directory = !string.IsNullOrWhiteSpace(resultsDirectoryOverride)
            ? resultsDirectoryOverride
            : string.IsNullOrWhiteSpace(attemptPrefix)
                ? Path.Combine(environment.ArtifactsPath, "TestResults")
                : Path.GetDirectoryName(attemptPrefix);
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Path.Combine(environment.ArtifactsPath, "TestResults");
        }

        var filePrefix = string.IsNullOrWhiteSpace(attemptPrefix)
            ? $"{environment.LeaseId}.{AcceptanceArtifactFileNames.Slug(check.Name)}"
            : $"{Path.GetFileName(attemptPrefix)}.{AcceptanceArtifactFileNames.Slug(check.Name)}";
        filePrefix = AppendInvocationSuffix(filePrefix, invocationOrdinal);
        var fileName = AcceptanceArtifactFileNames.BoundFileName(AcceptanceArtifactFileNames.SanitizeFileName(filePrefix), ".trx");
        var path = Path.Combine(directory, fileName);
        return new DotnetTestTelemetry([path], []);
    }

    internal static string AppendInvocationSuffix(string stem, int ordinal) =>
        ordinal > 0
            ? $"{stem}-run-{ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : stem;
}
