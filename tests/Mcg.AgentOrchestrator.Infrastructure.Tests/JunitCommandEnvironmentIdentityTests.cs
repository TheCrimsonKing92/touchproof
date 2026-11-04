using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: a unique cache journal, explicit identity resolvers, no git or global state.
public sealed class JunitCommandEnvironmentIdentityTests : GoalAcceptanceVerifierTestBase
{
    [Fact]
    public void EquivalentInputs_HaveStablePlanAndExecutionIdentity()
    {
        var absent = JunitCommandEnvironmentParserTests.Parse();
        var empty = JunitCommandEnvironmentParserTests.Parse(",\"environment\":{}");
        Assert.Equal(EffectiveAcceptanceManifestIdentity.Compute([absent]), EffectiveAcceptanceManifestIdentity.Compute([empty]));
        Assert.Equal(GoalAcceptanceVerifier.SemanticExecutionKeyForTests(absent), GoalAcceptanceVerifier.SemanticExecutionKeyForTests(empty));
        var first = JunitCommandEnvironmentParserTests.Parse(",\"environment\":{\"B\":\"two\",\"A\":\"one\"}");
        var second = JunitCommandEnvironmentParserTests.Parse(",\"environment\":{\"A\":\"one\",\"B\":\"two\"}");
        Assert.Equal(EffectiveAcceptanceManifestIdentity.Compute([first]), EffectiveAcceptanceManifestIdentity.Compute([second]));
        Assert.Equal(GoalAcceptanceVerifier.SemanticExecutionKeyForTests(first), GoalAcceptanceVerifier.SemanticExecutionKeyForTests(second));
        var casing = JunitCommandEnvironmentParserTests.Parse(",\"environment\":{\"a\":\"one\",\"b\":\"two\"}");
        Assert.Equal(OperatingSystem.IsWindows(), JunitCommandEnvironmentInputs.Fingerprint(first) == JunitCommandEnvironmentInputs.Fingerprint(casing));
        var changed = JunitCommandEnvironmentParserTests.Parse(",\"environment\":{\"A\":\"ONE\",\"B\":\"two\"}");
        Assert.NotEqual(GoalAcceptanceVerifier.SemanticExecutionKeyForTests(first), GoalAcceptanceVerifier.SemanticExecutionKeyForTests(changed));
        Assert.DoesNotContain("\"one\"", GoalAcceptanceVerifier.SemanticExecutionKeyForTests(first));
    }

    [Fact]
    public void ChangedFixtureInput_PreventsPreviouslyPositiveCacheReuse()
    {
        var root = CreateManifestWorkspace("{\"checks\":[]}");
        try
        {
            var lane = new Check { Name = "infrastructure tests: environment identity", Type = "dotnet-test",
                Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                Arguments = ["--filter", "FullyQualifiedName~EnvironmentIdentityFixtureTests"] };
            var inputA = JunitCommandEnvironmentParserTests.Parse(",\"environment\":{\"FIXTURE\":\"fixtures/a\"}");
            var inputB = JunitCommandEnvironmentParserTests.Parse(",\"environment\":{\"FIXTURE\":\"fixtures/b\"}");
            AcceptancePartitionVerdictCache Cache(Check input, string attempt) => Assert.IsType<AcceptancePartitionVerdictCache>(
                AcceptancePartitionVerdictCache.Create(new(new GoalId("11111111111111111111111111111111"), root,
                    [lane, input], 5, true, _ => "tree", _ => "main", _ => "commit", () => attempt,
                    () => EffectiveAcceptanceManifestIdentity.Compute([lane, input]), () => false,
                    ResolveClosureHash: _ => null, ResolveChangedFiles: (_, _, _) => [], ResolveDisjointLandingFacts: _ => null)));
            var first = Cache(inputA, "first");
            first.RecordExecution(lane, new(lane.Name, true, 0, null, TestResultPaths: []));
            Assert.NotNull(first.CompleteAttempt());
            var positive = Assert.IsType<AcceptanceCheckResult>(Cache(inputA, "same").TryReuse(lane));
            Assert.True(positive.Passed);
            Assert.Contains("reuse_rule=pair", positive.ResultSummary);
            Assert.Null(Cache(inputB, "changed").TryReuse(lane));
            Assert.False(AcceptanceIdenticalTreeReuseRule.TryGetCheckIdentity(inputA, out _));
        }
        finally { DeleteDirectoryWithRetry(root); }
    }
}
