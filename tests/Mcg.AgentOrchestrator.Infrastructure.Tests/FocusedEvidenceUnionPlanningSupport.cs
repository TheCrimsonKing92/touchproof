using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;

internal static class FocusedEvidenceUnionPlanningSupport
{
    internal const string ClassA = "AcceptanceLaneStallPolicyTests";
    internal const string ClassB = "AcceptancePartitionVerdictCacheWithinAttemptRerunTests";
    internal const string MethodA = "MtpTestRunnerScriptTests.MtpScriptFilterTranslationAndFilenameBoundingMatchGateConventions";
    internal const string MethodB = "MtpTestRunnerScriptTests.BooleanGrammar_PreservesRepresentableOperatorsAndRejectsLoss";
    internal static string Fqn(string value) => "FullyQualifiedName~" + value;

    internal static void UnsupportedShapes_RejectBeforeLaunch(string request)
    {
        Assert.False(TryBuildFocusedEvidenceChecks(request, new AcceptanceGateEngineSettings(),
            MtpTestRunnerScriptTests.RepositoryRoot(), out var checks, out _, out var rejection));
        Assert.Empty(checks);
        Assert.NotNull(rejection);
    }

    internal static IReadOnlyList<AcceptanceManifestCheck> Resolve(
        string request, out FocusedEvidenceCoverage coverage, string? root = null)
    {
        Assert.True(TryBuildFocusedEvidenceChecks(request, new AcceptanceGateEngineSettings(),
            root ?? MtpTestRunnerScriptTests.RepositoryRoot(), out var checks, out coverage, out var rejection), rejection.Detail);
        return checks;
    }

    internal static string[] Argv(AcceptanceManifestCheck check, string? resultsPath = null)
    {
        var assembly = typeof(MtpTestRunnerScriptTests).Assembly.Location;
        var artifacts = Path.GetDirectoryName(assembly)!;
        var settings = new AcceptanceGateEngineSettings
        {
            MtpInvocations = [new AcceptanceMtpInvocation
            {
                Project = check.Project!,
                ExecutablePathTemplate = Path.GetFileName(assembly),
                Arguments = ["{executable}", "--results-directory", "{resultsDirectory}", "--report-trx",
                    "--report-trx-filename", "{trxFileName}"]
            }]
        };
        var environment = new DotnetBuildEnvironment("union", artifacts, artifacts, "", [], "union");
        return AcceptanceCheckCommandBuilder.BuildMtpTestArguments(check, settings, environment,
            new DotnetTestTelemetry([resultsPath ?? Path.Combine(artifacts, "union.trx")], []),
            excludeHostIntegration: false, (_, _) => throw new InvalidOperationException("Focused translation must own this launch."));
    }

    internal static string[] FilterArgv(AcceptanceManifestCheck check)
    {
        var argv = Argv(check);
        var index = Array.FindIndex(argv, argument => argument.StartsWith("--filter-", StringComparison.Ordinal));
        Assert.True(index >= 0);
        return argv[index..];
    }
}
