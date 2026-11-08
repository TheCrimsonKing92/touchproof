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
            // The injected-runner constructor keeps every process invocation inside this fixture.
            var verifier = new GoalAcceptanceVerifier(TestOverrides, runner);

            var result = await verifier.RunAsync(root, new GoalId("12345678123456781234567812345678"), stableSlotIndex: 0);

            Assert.NotNull(output);
            var check = Assert.Single(result.Checks!, item => item.Name == "example tests");
            if (removed is not null)
            {
                Assert.False(check.Passed);
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
                Assert.True(check.Passed);
                Assert.Equal(AcceptanceShardCompletionSignals.MissingTrxInjectedRunnerCompatibility,
                    check.CompletionDecision!.PolicySignal);
            }
        }
        finally { DeleteDirectoryWithRetry(root); }
    }

    [Fact]
    public void CompleteOutput_WithoutTrx_PreservesStrictMissingTrxVerdict()
    {
        var root = Path.Combine(Path.GetTempPath(), "vstest-strict-completion", Guid.NewGuid().ToString("N"));
        try
        {
            var output = VstestCompleteOutputFixture.Create(root, "Example.Tests");
            var check = new GoalAcceptanceVerifier.AcceptanceManifestCheck
            {
                Name = "example tests", Type = "dotnet-test", Runner = "vstest", Project = "tests/Example.Tests.csproj"
            };
            var build = new AcceptanceCheckResult("build", true, 0, null, root);

            Assert.Null(VstestOutputCompletenessProbe.BeforeNoBuildRun(check,
                ["dotnet", "test", check.Project, "--no-build"], null, build, false));
            Assert.True(File.Exists(Path.Combine(output, "testhost.dll")));
            var decision = AcceptanceShardCompletionAdjudicator.DecideTestShard(false, 0,
                AcceptanceShardCompletionAdjudicator.InspectTrxCompletion([]), requireTrxEvidence: true);

            Assert.False(decision.Passed);
            Assert.Equal(AcceptanceShardCompletionPredicates.MissingTrx, decision.FailedPredicate);
        }
        finally { DeleteDirectoryWithRetry(root); }
    }
}
