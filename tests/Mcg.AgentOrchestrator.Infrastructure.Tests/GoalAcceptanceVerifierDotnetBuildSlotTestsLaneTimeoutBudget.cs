using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static GoalAcceptanceVerifierDotnetBuildSlotTestsTrustedBaselineDiscovery;

// JobAccounting owns shared build-slot state; the runner and workspace are fixtures.
[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsLaneTimeoutBudget : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Theory]
    [Xunit.InlineData(null, 3103)]
    [Xunit.InlineData(30, 1800)]
    public async Task ManagedLane_Estimate_PreservesExplicitOrScalesDefaultTimeout(
        int? timeoutMinutes, int expectedSeconds)
    {
        var root = CreateManifestWorkspace($$"""
            {
              "version": 1,
              "engine": {
                "timeouts": { "defaultMinutes": 40 },
                "infrastructureTestLanes": [{
                  "name": "Budget", "filter": "FullyQualifiedName~BudgetLaneTests",
                  "estimatedSerialSeconds": 2068.631
                }]
              },
              "checks": [{
                "name": "infrastructure tests", "type": "dotnet-test", "runner": "vstest",
                "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
                {{(timeoutMinutes is { } minutes ? $", \"timeoutMinutes\": {minutes}" : "")}}
              }],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var goal = new GoalId(Guid.NewGuid().ToString("N"));
        var testTimeouts = new ConcurrentQueue<TimeSpan>();
        var buildTimeouts = new ConcurrentQueue<TimeSpan>();
        try
        {
            var source = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "BudgetLaneTests.cs"),
                "public class BudgetLaneTests { [Xunit.Fact] public void Executes() { } }");
            var configuration = Path.Combine(root, "executors.json");
            File.WriteAllText(configuration, "{\"executors\":[]}");
            TestOverrides.RemoteLaneExecutorConfigurationPathForTests = configuration;
            TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, _, timeout, _) =>
            {
                if (IsVstestExecution(args))
                {
                    Assert.Contains("FullyQualifiedName~BudgetLaneTests", args);
                    testTimeouts.Enqueue(timeout);
                    var resultsIndex = Array.IndexOf(args, "--results-directory");
                    var loggerIndex = Array.IndexOf(args, "--logger");
                    Assert.InRange(resultsIndex, 0, args.Length - 2);
                    Assert.InRange(loggerIndex, 0, args.Length - 2);
                    const string prefix = "trx;LogFileName=";
                    Assert.StartsWith(prefix, args[loggerIndex + 1]);
                    WriteMtpTrx(["--results-directory", args[resultsIndex + 1],
                        "--report-trx-filename", args[loggerIndex + 1][prefix.Length..]],
                        1, ["BudgetLaneTests.Executes"]);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
                }
                if (args.Length > 1 && args[0] == "dotnet" && args[1] == "build")
                    buildTimeouts.Enqueue(timeout);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                TimeSpan.FromSeconds(30));

            var result = await verifier.RunAsync(root, goal,
                stableSlotIndex: StableSlotIndex(lease.Environment.ArtifactsPath), stableSlotLease: lease);

            Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
            Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), Assert.Single(testTimeouts));
            Assert.NotEmpty(buildTimeouts);
            Assert.All(buildTimeouts, timeout => Assert.Equal(
                AcceptanceGateEngineSettings.Load(root).ResolveBuildPhaseTimeout(timeoutMinutes), timeout));
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goal);
            DeleteDirectoryWithRetry(root);
        }
    }
}
