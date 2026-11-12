using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: prompt transformations and read-only repository source inspection.
public sealed class ReviewerBlockingFindingActorRulePromptTests
{
    [Fact]
    public void EveryReviewerVariantCarriesTheSharedRuleExactlyOnce()
    {
        AssertRule(SdlcRolePromptRequirements.Build(AgentRole.Reviewer));
        foreach (var complexity in Enum.GetValues<TaskComplexity>())
        {
            AssertRule(SdlcRolePromptRequirements.Build(AgentRole.Reviewer, complexity));
            foreach (var highRisk in new[] { false, true })
                AssertRule(SdlcRolePromptRequirements.Build(AgentRole.Reviewer, complexity, highRisk));
        }
    }

    [Fact]
    public void OtherRolesDoNotReceiveTheReviewerRule()
    {
        foreach (var role in Enum.GetValues<AgentRole>().Where(role => role != AgentRole.Reviewer))
        {
            Assert.DoesNotContain(ReviewerBlockingFindingActorRule.RequirementLine,
                SdlcRolePromptRequirements.Build(role));
            foreach (var complexity in Enum.GetValues<TaskComplexity>())
                Assert.DoesNotContain(ReviewerBlockingFindingActorRule.RequirementLine,
                    SdlcRolePromptRequirements.Build(role, complexity));
        }
    }

    [Fact]
    public void BothRequirementBlocksReferenceOneRuleOwner()
    {
        var source = File.ReadAllText(Path.Combine(VerifiedRepositoryRoot.Find(),
            "src/Mcg.AgentOrchestrator.Core/Application/SdlcRolePromptRequirements.cs"));

        Assert.Equal(2, source.Split("ReviewerBlockingFindingActorRule.RequirementLine").Length - 1);
        Assert.DoesNotContain("A finding may be blocking only if", source);
    }

    private static void AssertRule(IReadOnlyList<string> lines)
    {
        var rule = Assert.Single(lines.Where(line => line == ReviewerBlockingFindingActorRule.RequirementLine));
        Assert.Equal("- " + ReviewerBlockingFindingActorRule.RuleText, rule);
        var index = lines.ToList().IndexOf(rule);
        Assert.Equal("- Listed in matching OPERATOR-OWNED/ACCEPTANCE-GATE-OWNED: not-verifiable; else attest met/not-met.",
            lines[index - 1]);
    }
}
