using System.Reflection;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: an explicit storage namespace owns this fixture's goal artifacts and locks.
public sealed class GoalAcceptanceVerifierIncompleteVstestOutputTests : GoalAcceptanceVerifierTestBase
{
    [Theory]
    [InlineData(null)]
    [InlineData("Example.Tests.runtimeconfig.json")]
    [InlineData("testhost.dll")]
    public async Task NoBuildVstest_MissingAsset_RejectsBeforeRunner(string? removed)
    {
        var root = CreateManifestWorkspace("""
            { "version": 1, "checks": [
              { "name": "example tests", "type": "dotnet-test", "runner": "vstest",
                "project": "tests/Example.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
            ], "forbiddenChangedPathGlobs": [] }
            """);
        try
        {
            var calls = new List<string[]>();
            string? output = null;
            TestOverrides.BuildStorageRootForTests = new DotnetBuildStorageRoot(Path.Combine(root, "storage"));
            Func<string[], string, TimeSpan, CancellationToken, Task<GoalAcceptanceVerifier.CommandResult>> runner =
                (args, _, _, _) =>
                {
                    calls.Add(args);
                    if (args.Length > 1 && args[1] == "build")
                    {
                        var artifacts = args[Array.IndexOf(args, "--artifacts-path") + 1];
                        output = VstestCompleteOutputFixture.Create(artifacts, "Example.Tests");
                        if (removed is not null) File.Delete(Path.Combine(output, removed));
                    }
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
                };
            // Keep the real runner's strict TRX contract while controlling only process results.
            var constructor = typeof(GoalAcceptanceVerifier).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(candidate => candidate.GetParameters().Any(parameter => parameter.Name == "requiresTestTelemetryReceipt"));
            var verifier = (GoalAcceptanceVerifier)constructor.Invoke(
                [TestOverrides.CrosswalkExecutableForTests, runner, runner, TimeProvider.System, null, true, TestOverrides, TestOverrides.BuildStorageRootForTests]);

            var result = await verifier.RunAsync(root, new GoalId("12345678123456781234567812345678"), stableSlotIndex: 0);

            Assert.NotNull(output);
            var check = Assert.Single(result.Checks!, item => item.Name == "example tests");
            Assert.False(check.Passed);
            if (removed is not null)
            {
                Assert.DoesNotContain(calls, args => args.Length > 1 && args[1] == "test");
                Assert.Equal(AcceptanceShardCompletionPredicates.IncompleteVstestOutput, check.CompletionDecision!.FailedPredicate);
                Assert.Equal(AcceptanceShardCompletionPredicates.IncompleteVstestOutput, check.FailureClassification);
                Assert.NotEqual(AcceptanceShardCompletionPredicates.MissingTrx, check.CompletionDecision.FailedPredicate);
                Assert.Contains(output, check.OutputTail);
                Assert.Contains(removed, check.OutputTail);
            }
            else
            {
                Assert.Contains(calls, args => args.Length > 1 && args[1] == "test" && args.Contains("--no-build"));
                Assert.Equal(AcceptanceShardCompletionPredicates.MissingTrx, check.CompletionDecision!.FailedPredicate);
            }
        }
        finally { DeleteDirectoryWithRetry(root); }
    }
}
