using Mcg.AgentOrchestrator.Core;
using RepositoryFixture = RepositoryTestReachTests.RepositoryFixture;

// Parallel-safe: fixture roots are unique; real repository access is read-only.
public sealed class WideImpactTestSelectionReplayTests
{
    private const string StandingRulesPath = "src/Mcg.AgentOrchestrator.Core/WorkerStandingRules.cs";
    private const string TaskBriefsPath =
        "src/Mcg.AgentOrchestrator.Core/Application/AgentOrchestratorKernel.TaskBriefs.cs";
    private const string TaskBriefProjectionPath =
        "src/Mcg.AgentOrchestrator.Core/Application/AgentOrchestratorKernel.TaskBriefProjection.cs";
    private const string UnrelatedSourcePath = "src/Mcg.AgentOrchestrator.Core/UnrelatedRules.cs";

    [Theory]
    [InlineData(StandingRulesPath)]
    [InlineData(TaskBriefsPath)]
    [InlineData(TaskBriefProjectionPath)]
    public void DeclaredInput_SelectsKernelConstructingClass(string changedPath)
    {
        using var fixture = new RepositoryFixture();
        Populate(fixture);

        var selection = ReverseDependencyTestImpactReader.Read(fixture.Root, [changedPath], bypassCache: true);

        AssertResolved(selection);
        Assert.Equal(new[] { "AgentOrchestratorKernelTests", "KernelConstructingTests" }, selection.TestClassNames);
    }

    [Fact]
    public void UnrelatedShippingSource_KeepsOwnedClassOnlyReach()
    {
        using var fixture = new RepositoryFixture();
        Populate(fixture);

        var selection = ReverseDependencyTestImpactReader.Read(
            fixture.Root, [UnrelatedSourcePath], bypassCache: true);

        AssertResolved(selection);
        Assert.Equal(new[] { "AgentOrchestratorKernelTests", "UnrelatedRulesTests" }, selection.TestClassNames);
    }

    [Fact]
    public void DeclaredAndUnrelatedInputs_SelectUnionOfBoth()
    {
        using var fixture = new RepositoryFixture();
        Populate(fixture);

        var selection = ReverseDependencyTestImpactReader.Read(
            fixture.Root, [StandingRulesPath, UnrelatedSourcePath], bypassCache: true);

        AssertResolved(selection);
        Assert.Equal(new[] { "AgentOrchestratorKernelTests", "KernelConstructingTests", "UnrelatedRulesTests" },
            selection.TestClassNames);
    }

    [Fact]
    public void RealRepository_StandingRulesChangeSelectsSubscriptionPreflight()
    {
        var selection = ReverseDependencyTestImpactReader.Read(VerifiedRepositoryRoot.Find(), [StandingRulesPath]);

        AssertResolved(selection);
        Assert.Contains("WorkerDispatchTestsSubscriptionPreflight", selection.TestClassNames);
    }

    private static void AssertResolved(ReverseDependencyTestSelection selection) =>
        Assert.True(selection.Outcome == ReverseDependencySelectionOutcome.Resolved, selection.Reason);

    private static void Populate(RepositoryFixture fixture)
    {
        // Distinct declarations make every declared path depend on the additional kernel seed.
        fixture.Write(StandingRulesPath, "public class WorkerStandingRules { }");
        fixture.Write(TaskBriefsPath, "public class BriefRules { }");
        fixture.Write(TaskBriefProjectionPath, "public class BriefProjectionRules { }");
        fixture.Write(UnrelatedSourcePath, "public class UnrelatedRules { }");
        fixture.Write("src/Mcg.AgentOrchestrator.Core/Application/AgentOrchestratorKernel.cs", """
            public class AgentOrchestratorKernel
            {
                private WorkerStandingRules standing;
                private BriefRules briefs;
                private BriefProjectionRules projection;
                private UnrelatedRules unrelated;
            }
            """);
        fixture.Write($"{RepositoryFixture.TestDirectory}/AgentOrchestratorKernelTests.cs", """
            public class AgentOrchestratorKernelTests
            {
                private AgentOrchestratorKernel kernel;
                [Xunit.Fact] public void Runs() { }
            }
            """);
        fixture.Write($"{RepositoryFixture.TestDirectory}/KernelConstructingTests.cs", """
            public class KernelConstructingTests
            {
                [Xunit.Fact] public void Runs() { _ = new AgentOrchestratorKernel(); }
            }
            """);
        fixture.Write($"{RepositoryFixture.TestDirectory}/UnrelatedRulesTests.cs", """
            public class UnrelatedRulesTests
            {
                private UnrelatedRules rules;
                [Xunit.Fact] public void Runs() { }
            }
            """);
    }
}
