using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;
using static FocusedEvidenceUnionPlanningSupport;

public sealed class FocusedEvidenceUnionPlanningExclusionScopeTests
{
    [Xunit.Fact]
    public void Exclusions_KeepAlternativeScopeAndSharedConjunctionScope()
    {
        var checks = Resolve($"({Fqn(ClassA)}&Category!=Local)|{Fqn(MethodA)}", out _);
        Assert.Equal(2, checks.Count);
        var excluded = Assert.Single(checks, check => check.FocusedEvidenceTokens.Any(token => token.Kind == FocusedEvidenceTokenKind.ExcludedTrait));
        Assert.Contains(excluded.FocusedEvidenceTokens, token => token.Value == ClassA);
        Assert.DoesNotContain(Assert.Single(checks, check => check != excluded).FocusedEvidenceTokens,
            token => token.Kind == FocusedEvidenceTokenKind.ExcludedTrait);

        checks = Resolve($"({Fqn(ClassA)}|{Fqn(MethodA)})&Category!=Shared&FullyQualifiedName!~ExcludedTests", out _);
        Assert.Equal(2, checks.Count);
        Assert.All(checks, check =>
        {
            Assert.Contains(check.FocusedEvidenceTokens, token => token.Value == "Shared");
            Assert.Contains(check.FocusedEvidenceTokens, token => token.Value == "ExcludedTests");
            Assert.NotEmpty(Argv(check));
        });
    }
}
