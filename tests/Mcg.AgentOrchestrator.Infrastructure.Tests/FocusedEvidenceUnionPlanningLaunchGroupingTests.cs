using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;
using static FocusedEvidenceUnionPlanningSupport;

public sealed class FocusedEvidenceUnionPlanningLaunchGroupingTests
{
    [Xunit.Theory]
    [Xunit.InlineData("|")]
    [Xunit.InlineData(";")]
    public void MixedUnion_PlansHomogeneousLaunchesAndAllTargetMappings(string separator)
    {
        var request = string.Join(separator, new[] { ClassA, ClassB, MethodA, MethodB }.Select(Fqn));
        var checks = Resolve(request, out var coverage);
        Assert.Equal(2, checks.Count);
        Assert.All(checks, check => Assert.Single(check.FocusedEvidenceTokens.Select(token => token.Kind).Distinct()));
        Assert.Equal(new[] { ClassA, ClassB, MethodA, MethodB }.Order(),
            checks.SelectMany(check => check.FocusedEvidenceTokens).Select(token => token.Value).Order());
        if (separator == "|")
            Assert.Equal(checks.Select(check => check.Name).Order(), Assert.Single(coverage.TargetToChecks).CheckNames.Order());
        else
            Assert.All(coverage.TargetToChecks, target => Assert.Single(target.CheckNames));
        Assert.All(checks, check => Assert.NotEmpty(Argv(check)));
    }

    [Xunit.Fact]
    public void HomogeneousUnions_DeduplicateTokensAndKeepOriginalTargets()
    {
        foreach (var values in new[] { new[] { ClassA, ClassB }, new[] { MethodA, MethodB } })
        {
            var checks = Resolve($"{Fqn(values[0])}|{Fqn(values[1])};{Fqn(values[0])}", out var coverage);
            var check = Assert.Single(checks);
            Assert.Equal(2, check.FocusedEvidenceTokens.Count);
            Assert.Equal(2, coverage.TargetToChecks.Count);
            Assert.All(coverage.TargetToChecks, target => Assert.Equal(check.Name, Assert.Single(target.CheckNames)));
        }
    }

    [Xunit.Fact]
    public void RecordedRequest_PreservesExactTokensWithoutImportingBranchImplementation()
    {
        // Source: packaged goal/timeline.json HumanInputReceived. Canonical metadata:
        // .orchestrator/pre-review-evidence-attempts/ad3fafc01dd042be82b03c1b4f4143b2/
        // ad3fafc0-0-20261012225158714-a2c41dc780bb47b98de522ca75173189.attempt.json
        const string recorded = "Infrastructure.Tests:FullyQualifiedName~AcceptanceLaneStallPolicyTests; Infrastructure.Tests:FullyQualifiedName~AcceptancePartitionVerdictCacheWithinAttemptRerunTests; Infrastructure.Tests:FullyQualifiedName~MtpTestRunnerScriptTests.ManagedMtpRunnerExecutesEveryRepositoryTestProjectAfterIsolatedBuild; Infrastructure.Tests:FullyQualifiedName~MtpTestRunnerScriptTests.NestedDiagnostics_CurrentManagedRun_ProvidesCustody; Infrastructure.Tests:FullyQualifiedName~MtpTestRunnerScriptTestsSilenceGuard";
        var root = Directory.CreateTempSubdirectory("focused-recorded-").FullName;
        try
        {
            var directory = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "MtpTestRunnerScriptTests.cs"), """
                public class AcceptanceLaneStallPolicyTests { [Xunit.Fact] public void Runs() { } }
                public class AcceptancePartitionVerdictCacheWithinAttemptRerunTests { [Xunit.Fact] public void Runs() { } }
                public class MtpTestRunnerScriptTestsSilenceGuard { [Xunit.Fact] public void Runs() { } }
                public class MtpTestRunnerScriptTests
                {
                    [Xunit.Fact] public void ManagedMtpRunnerExecutesEveryRepositoryTestProjectAfterIsolatedBuild() { }
                    [Xunit.Fact] public void NestedDiagnostics_CurrentManagedRun_ProvidesCustody() { }
                }
                """);
            var checks = Resolve(recorded, out var coverage, root);
            Assert.Equal(2, checks.Count);
            Assert.Equal(5, coverage.TargetToChecks.Count);
            Assert.Equal(recorded.Split(';').Select(target => target.Trim()).Order(), coverage.TargetToChecks.Select(target => target.Target).Order());
            Assert.Equal(3, checks.Sum(check => Argv(check).Count(argument => argument == "--filter-class")));
            Assert.Equal(2, checks.Sum(check => Argv(check).Count(argument => argument == "--filter-method")));
            Assert.All(checks, check => Assert.False(Argv(check).Contains("--filter-class") && Argv(check).Contains("--filter-method")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
