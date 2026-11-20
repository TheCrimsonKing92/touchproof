using Xunit;

namespace Mcg.AgentOrchestrator.Core.Tests;

// Parallel-safe: change paths and declaration results are supplied in memory.
public sealed class RepositoryTestImpactPlannerNonCsSourceTests
{
    [Theory]
    [InlineData(".json")]
    [InlineData(".md")]
    public void MatchingNonCSharpTestProjectFileAddsNoClassFilter(string extension)
    {
        const string project = "tests/Mcg.AgentOrchestrator.Core.Tests/";
        const string source = project + "ActualTests.cs";
        var nonSource = project + "MatchingTests" + extension;
        var reader = new StubDeclarationReader(["ActualTests", "MatchingTests"], new Dictionary<string, string>
        {
            [source] = "ActualTests",
            [nonSource] = "MatchingTests",
        });

        var plan = RepositoryTestImpactPlanner.Plan(
            RepositoryChangeClassifier.Classify([source, nonSource]), reader);

        var check = Assert.Single(plan.Checks, check => check.TestProject == RepositoryTestProject.Core);
        Assert.Equal(["ActualTests"], check.TestClassSelections);
        Assert.Equal("FullyQualifiedName~ActualTests", check.Command[^1]);
    }

    [Fact]
    public void NonCSharpOrchestrationFileAbandonsTheFilenameBasedFilter()
    {
        const string directory = "src/Mcg.AgentOrchestrator.App/Orchestration/";
        var reader = new StubDeclarationReader(["WorkflowTests", "ReferenceTests"]);

        var plan = RepositoryTestImpactPlanner.Plan(
            RepositoryChangeClassifier.Classify([directory + "Workflow.cs", directory + "Reference.md"]), reader);

        var check = Assert.Single(plan.Checks);
        Assert.Equal(RepositoryTestProject.Infrastructure, check.TestProject);
        Assert.DoesNotContain("--filter", check.Command);
        Assert.Null(check.TestClassSelections);
    }

    private sealed class StubDeclarationReader(
        IReadOnlyList<string> classNames,
        IReadOnlyDictionary<string, string>? fileDeclarations = null) : ITestClassDeclarationReader
    {
        public TestClassDeclarations ReadFile(string repositoryRelativePath) =>
            fileDeclarations is not null && fileDeclarations.TryGetValue(repositoryRelativePath, out var className)
                ? TestClassDeclarations.Resolved([className])
                : TestClassDeclarations.Unavailable;

        public TestClassDeclarations ReadProject(string repositoryRelativeDirectory) =>
            TestClassDeclarations.Resolved(classNames);
    }
}
