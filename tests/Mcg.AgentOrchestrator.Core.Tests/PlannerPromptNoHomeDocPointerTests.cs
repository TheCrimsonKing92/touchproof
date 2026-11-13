using Mcg.AgentOrchestrator.Core;

// Parallel-safe: constructs immutable requirements and only reads candidate source files.
[LiveTreeStructural]
public sealed class PlannerPromptNoHomeDocPointerTests
{
    [Xunit.Theory]
    [Xunit.InlineData(TaskComplexity.Complex, false)]
    [Xunit.InlineData(TaskComplexity.Complex, true)]
    [Xunit.InlineData(TaskComplexity.Simple, false)]
    [Xunit.InlineData(TaskComplexity.Simple, true)]
    public void Build_PlannerVariants_KeepEvidenceOwnershipSelfContained(
        TaskComplexity complexity, bool hasDurableResearch)
    {
        var requirements = SdlcRolePromptRequirements.Build(
            AgentRole.Planner, complexity,
            includeHighRiskReviewerEnumerationContract: false, hasDurableResearch: hasDurableResearch);

        Xunit.Assert.All(requirements, line =>
            Xunit.Assert.DoesNotContain("docs/", line, StringComparison.Ordinal));
        Xunit.Assert.Single(requirements, line => line ==
            "- For each criterion name the evidence owner (Developer/Tester/Reviewer/Acceptance/Operator), the owning seam, and the class `TEST-VERIFIABLE` or `REAL-WORLD-DEPENDENT`.");
        Xunit.Assert.Equal(hasDurableResearch, requirements[1].StartsWith(
            "- Synthesize the plan from the complete Durable Research Notes", StringComparison.Ordinal));
        Xunit.Assert.Equal(complexity == TaskComplexity.Complex, requirements.Contains(
            "- Challenge ambiguous requirements; name assumptions, sequencing risks, and explicit stop conditions."));
    }

    [Xunit.Fact]
    public void RefinerInstruction_EvidenceOwnership_HasNoHomeDocumentPointer()
    {
        var path = Path.Combine(VerifiedRepositoryRoot.Find(),
            "src", "Mcg.AgentOrchestrator.App", "Orchestration", "SpecRefinerPlanner.cs");
        var instruction = Xunit.Assert.Single(File.ReadAllLines(path), line =>
            line.Contains("Use evidence_owner \"acceptance-gate\"", StringComparison.Ordinal));

        Xunit.Assert.DoesNotContain("docs/", instruction, StringComparison.Ordinal);
    }
}
