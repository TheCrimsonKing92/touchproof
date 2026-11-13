using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class EffectiveAcceptanceManifestIdentity
{
    internal static string SemanticExecutionKey(GoalAcceptanceVerifier.AcceptanceManifestCheck check) =>
        JsonSerializer.Serialize(new
        {
            check.Name,
            check.Type,
            check.Command,
            EnvironmentFingerprint = JunitCommandEnvironmentInputs.Fingerprint(check),
            Project = AcceptancePolicyShardPlanner.NormalizePath(check.Project),
            check.Arguments,
            check.Pattern,
            FilePath = AcceptancePolicyShardPlanner.NormalizePath(check.FilePath),
            check.TimeoutMinutes,
            check.Advisory,
            check.Runner,
            check.EstimatedSerialSeconds,
            ExclusiveResourceKeys = check.ExclusiveResourceKeys
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            check.IsFocusedEvidenceSelection,
            check.FocusedEvidenceTokens,
            check.FocusedEvidenceSelections
        });

    internal static string Compute(
        IReadOnlyList<GoalAcceptanceVerifier.AcceptanceManifestCheck> effectiveChecks,
        AcceptanceGateEngineSettings? engineSettings = null)
    {
        var canonicalChecks = effectiveChecks.Select(check => new
        {
            check.Name,
            check.Type,
            check.Command,
            EnvironmentFingerprint = JunitCommandEnvironmentInputs.Fingerprint(check),
            check.Project,
            Arguments = check.Arguments.ToArray(),
            check.Pattern,
            check.FilePath,
            check.TimeoutMinutes,
            check.Advisory,
            check.Runner,
            check.EstimatedSerialSeconds,
            ExclusiveResourceKeys = check.ExclusiveResourceKeys.ToArray()
        }).ToArray();
        var settings = engineSettings ?? new AcceptanceGateEngineSettings();
        var canonicalPlan = new
        {
            Checks = canonicalChecks,
            DeclaredToolHomes = settings.DeclaredToolHomes.OrderBy(home => home.Variable, StringComparer.Ordinal)
                .ThenBy(home => home.Directory, StringComparer.Ordinal)
                .Select(home => new { home.Variable, home.Directory }).ToArray(),
            ChangeScopedAcceptanceEnabled = AcceptancePolicyShardPlanner.ChangeScopedAcceptanceEnabled(),
            Engine = new
            {
                settings.MaxConcurrentShards,
                settings.EnforceStructuralCoverage,
                settings.PartitionVerdictFullRerunEveryN,
                settings.OutputCaptureLimitBytes,
                Timeouts = new
                {
                    settings.Timeouts.DefaultMinutes,
                    settings.Timeouts.BuildServerShutdownMinutes,
                    settings.Timeouts.DiscoveryMinutes
                },
                MtpInvocations = settings.MtpInvocations
                    .OrderBy(invocation => invocation.Project, StringComparer.OrdinalIgnoreCase)
                    .Select(invocation => new
                    {
                        invocation.Project,
                        invocation.ExecutablePathTemplate,
                        Arguments = invocation.Arguments.ToArray()
                    })
                    .ToArray()
            }
        };
        var canonicalJson = JsonSerializer.Serialize(canonicalPlan);
        if (settings.DeclaredTestSettings.Count > 0)
            canonicalJson += "\n" + JsonSerializer.Serialize(settings.DeclaredTestSettings);
        if (settings.SelectedExecutionMode != AcceptanceSelectedExecutionMode.Off)
            canonicalJson += "\nselected-execution-mode=" + settings.SelectedExecutionMode;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson));
        return $"effective-manifest-sha256-{Convert.ToHexStringLower(hash)}";
    }
}
