using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: read-only repository inspection and an invocation-scoped cold parse cache.
// Durations are diagnostic observations, never correctness thresholds.
public sealed class WorkerBuildRequestStructuralPhaseMeasurementTests(ITestOutputHelper output)
{
    [Fact]
    public void CurrentTree_UnboundedEvaluation_RecordsMonotonicPhaseMeasurements()
    {
        var root = VerifiedRepositoryRoot.Find();
        var revision = Git(root, "rev-parse", "HEAD").Trim();
        var dirty = Git(root, "status", "--porcelain", "--untracked-files=normal", "--", "src", "tests", "config");
        var cache = new AcceptanceTestClassSourceCache();
        using var scope = AcceptanceTestClassSourceCache.Use(cache);
        var timings = new DeveloperCompletionStructuralPhaseTimings();
        Assert.Equal(0, cache.ParsedFileCount);

        // Deliberately bypass BoundedStructuralPreflight. HEAD is the identical baseline input
        // for the recorded invocation; dirty candidate files still participate in the scans.
        var findings = DeveloperCompletionStructuralPreflight.Evaluate(root, projectHomeDirectory: null,
            integrationBranch: revision, timings: timings);

        output.WriteLine("structural_phase_measurement=" + JsonSerializer.Serialize(new
        {
            root, revision, baseline = revision, projectHomeDirectory = (string?)null,
            relevantChanges = dirty, cache = "cold invocation-scoped parse cache; OS filesystem cache uncontrolled",
            clock = "TimeProvider.System.GetTimestamp/GetElapsedTime (monotonic)",
            method = "direct unbounded Evaluate; total contains all phases; other spans do not overlap; absent phases were not reached",
            phaseMilliseconds = timings.Elapsed.ToDictionary(item => item.Key, item => item.Value.TotalMilliseconds),
            parsedFiles = cache.ParsedFileCount, findings.EvaluationComplete, findings.HasViolation, findings.Message
        }));

        Assert.True(findings.EvaluationComplete, findings.UnavailableDiagnostic);
        Assert.Contains("total", timings.Elapsed.Keys);
        Assert.Contains("ratchet", timings.Elapsed.Keys);
        Assert.Contains("test-class-scan", timings.Elapsed.Keys);
        Assert.Contains("lane-membership", timings.Elapsed.Keys);
        Assert.Contains("baseline-read", timings.Elapsed.Keys);
        Assert.Contains("substring-check", timings.Elapsed.Keys);
        Assert.Contains("lane-isolation", timings.Elapsed.Keys);
        Assert.True(cache.ParsedFileCount > 0, "The current repository test-source scan was not reached.");
    }

    [Fact]
    public void PhaseRecorder_UsesInjectedMonotonicClockAndIncludesTotal()
    {
        var clock = new WorkerBuildRequestFixture.FixtureTime();
        var timings = new DeveloperCompletionStructuralPhaseTimings(clock);
        var result = timings.Measure("total", () => timings.Measure("phase", () =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(42));
            return "observed";
        }));
        Assert.Equal("observed", result);
        Assert.Equal(TimeSpan.FromMilliseconds(42), timings.Elapsed["phase"]);
        Assert.Equal(TimeSpan.FromMilliseconds(42), timings.Elapsed["total"]);
    }

    private static string Git(string root, params string[] arguments)
    {
        var result = GitCli.RunForRepository(root, arguments);
        Assert.True(result.Succeeded && !result.DrainTimedOut && result.ProcessStarted, result.Error);
        return result.Output;
    }
}
