using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;
using static FocusedEvidenceUnionPlanningSupport;

public sealed class FocusedEvidenceUnionPlanningCommandConstructionTests
{
    [Xunit.Fact]
    public void DuplicateExcludedAlternatives_ShareOneLaunchAndRetainBothTargets()
    {
        var checks = Resolve($"({Fqn(ClassA)}&Category!=Local);{Fqn(ClassA)}&Category!=Local", out var coverage);
        var check = Assert.Single(checks);
        Assert.Equal(2, coverage.TargetToChecks.Count);
        Assert.All(coverage.TargetToChecks, target => Assert.Equal(check.Name, Assert.Single(target.CheckNames)));
    }

    [Xunit.Fact]
    public void RepresentableConjunction_EmitsIntersectingGroupsWithSharedExclusion()
    {
        var check = Assert.Single(Resolve(
            $"({Fqn(ClassA)}|{Fqn(ClassB)})&{Fqn(MethodA)}&Category!=Local", out _));
        var argv = FilterArgv(check);
        Assert.Equal(2, argv.Count(value => value == "--filter-class"));
        Assert.Equal(1, argv.Count(value => value == "--filter-method"));
        Assert.Contains("--filter-not-trait", argv);
    }

    [Xunit.Fact]
    public void CommandConstruction_RejectsUnpartitionedMixedUnionAndLocalExclusion()
    {
        var checks = Resolve($"{Fqn(ClassA)};{Fqn(MethodA)}", out _);
        var tokens = checks.SelectMany(check => check.FocusedEvidenceTokens).ToArray();
        Assert.Throws<InvalidOperationException>(() => Argv(new AcceptanceManifestCheck
        {
            Project = AcceptancePolicyShardPlanner.InfrastructureTestsProject,
            Arguments = ["--filter", string.Join("|", tokens.Select(token => token.CanonicalToken))],
            FocusedEvidenceTokens = tokens
        }));
        checks = Resolve($"({Fqn(ClassA)}&Category!=Local)|{Fqn(ClassB)}", out _);
        tokens = checks.SelectMany(check => check.FocusedEvidenceTokens).ToArray();
        Assert.Throws<InvalidOperationException>(() => Argv(new AcceptanceManifestCheck
        {
            Project = AcceptancePolicyShardPlanner.InfrastructureTestsProject,
            Arguments = ["--filter", $"({Fqn(ClassA)}&Category!=Local)|{Fqn(ClassB)}"],
            FocusedEvidenceTokens = tokens
        }));
    }
}
