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

// One instance belongs to one class run and its built test assembly. Identity is the
// requested argv, before the launch helper appends its invocation-owned scratch path.
internal sealed class ManagedDiscoveryLaunchLedger(
    Func<string[], MtpTestRunnerScriptTests.ProcessResult> launch,
    bool reuse)
{
    private readonly object sync = new();
    private readonly Dictionary<string[], Entry> entries = new(new ArgumentListComparer());

    internal MtpTestRunnerScriptTests.ProcessResult Request(params string[] arguments)
    {
        Entry entry;
        lock (sync)
        {
            // The caller may reuse or mutate its array after this request.
            var key = arguments.ToArray();
            if (!entries.TryGetValue(key, out entry!))
            {
                entry = new Entry(key);
                entry.Outcome = new Lazy<MtpTestRunnerScriptTests.ProcessResult>(
                    () => Launch(entry), LazyThreadSafetyMode.ExecutionAndPublication);
                entries.Add(key, entry);
            }
            entry.Requests++;
        }
        return reuse ? entry.Outcome.Value : Launch(entry);
    }

    internal IReadOnlyList<LaunchCount> Snapshot()
    {
        lock (sync)
            return entries.Values.Select(entry => new LaunchCount(
                entry.Arguments.ToArray(), entry.Requests, entry.Launches)).ToArray();
    }

    private MtpTestRunnerScriptTests.ProcessResult Launch(Entry entry)
    {
        lock (sync)
            entry.Launches++;
        // Do not let the launch delegate mutate the dictionary's key.
        return launch(entry.Arguments.ToArray());
    }

    internal sealed record LaunchCount(string[] Arguments, int Requests, int Launches);

    private sealed class Entry(string[] arguments)
    {
        internal string[] Arguments { get; } = arguments;
        internal int Requests { get; set; }
        internal int Launches { get; set; }
        internal Lazy<MtpTestRunnerScriptTests.ProcessResult> Outcome { get; set; } = null!;
    }

    private sealed class ArgumentListComparer : IEqualityComparer<string[]>
    {
        public bool Equals(string[]? left, string[]? right) =>
            ReferenceEquals(left, right) || (left is not null && right is not null &&
                left.SequenceEqual(right, StringComparer.Ordinal));

        public int GetHashCode(string[] arguments)
        {
            var hash = new HashCode();
            foreach (var argument in arguments)
                hash.Add(argument, StringComparer.Ordinal);
            return hash.ToHashCode();
        }
    }
}
