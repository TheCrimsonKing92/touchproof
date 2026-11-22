using Mcg.AgentOrchestrator.App.Cli;

public sealed class ShadowStartPathTriggerTests
{
    [Theory]
    [InlineData("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs", true)]
    [InlineData("src/Mcg.AgentOrchestrator.Core/Application/AgentOrchestratorKernel.GoalLifecycle.cs", true)]
    [InlineData("src/Mcg.AgentOrchestrator.Execution/Persistence/StoreSchemaRegistry.cs", true)]
    [InlineData("src/Mcg.AgentOrchestrator.Core/Application/AcceptanceCriteriaParser.cs", true)]
    [InlineData(@"./src\Mcg.AgentOrchestrator.App\Cli\CliArgumentParser.CommandCatalog.cs", true)]
    [InlineData("src/Mcg.AgentOrchestrator.Execution/PersistenceExtras/StoreSchemaRegistry.cs", false)]
    [InlineData("src/Mcg.AgentOrchestrator.App/Cli/CliArgumentParserExamples.cs", false)]
    [InlineData("docs/state-model.md", false)]
    [InlineData("README.md", false)]
    [InlineData("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliHelpTests.cs", false)]
    [InlineData("tests/Mcg.AgentOrchestrator.Core.Tests/AcceptanceCriteriaParserTests.cs", false)]
    public void Touches_RecognizesScopedProductionPaths(string path, bool expected) =>
        Assert.Equal(expected, ShadowStartPathTrigger.Touches([path]));

    [Fact]
    public void Touches_MatchesIfAnyChangedPathIsInScope()
    {
        Assert.False(ShadowStartPathTrigger.Touches([]));
        Assert.True(ShadowStartPathTrigger.Touches(
            ["README.md", "src/Mcg.AgentOrchestrator.Core/Conductor/ConductorAutonomyPolicy.cs"]));
    }
}
