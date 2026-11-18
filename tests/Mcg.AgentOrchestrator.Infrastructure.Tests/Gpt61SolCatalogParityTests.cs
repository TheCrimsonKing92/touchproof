using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class Gpt61SolCatalogParityTests
{
    [Fact]
    public void Gpt61SolUsesTheGpt6SolReasoningDefault()
    {
        Assert.Equal("medium", AgentCatalog.DefaultSubscriptionReasoningEffort("OpenAI", AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias));
        Assert.Equal(
            AgentCatalog.DefaultSubscriptionReasoningEffort("OpenAI", AgentCatalog.OpenAiGpt6SolSubscriptionModelAlias),
            AgentCatalog.DefaultSubscriptionReasoningEffort("OpenAI", AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias));
    }

    [Theory]
    [InlineData(AgentRole.Developer, "OpenAI", true)]
    [InlineData(AgentRole.Tester, "OpenAI", true)]
    [InlineData(AgentRole.Reviewer, "OpenAI", true)]
    [InlineData(AgentRole.Planner, "OpenAI", true)]
    [InlineData(AgentRole.Researcher, "OpenAI", true)]
    [InlineData(AgentRole.Ideation, "OpenAI", false)]
    [InlineData(AgentRole.Developer, "Anthropic", false)]
    public void Gpt61SolMatchesGpt6SolTypedContextRules(AgentRole role, string provider, bool expected)
    {
        Assert.Equal(expected, WorkerContextHelpers.UsesTypedContextPackage(role, provider, AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias));
        Assert.Equal(
            WorkerContextHelpers.UsesTypedContextPackage(role, provider, AgentCatalog.OpenAiGpt6SolSubscriptionModelAlias),
            WorkerContextHelpers.UsesTypedContextPackage(role, provider, AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias));
    }

    [Theory]
    [InlineData(AgentRole.Researcher, "OpenAI", true)]
    [InlineData(AgentRole.Planner, "OpenAI", true)]
    [InlineData(AgentRole.Developer, "OpenAI", true)]
    [InlineData(AgentRole.Tester, "OpenAI", true)]
    [InlineData(AgentRole.Reviewer, "OpenAI", true)]
    [InlineData(AgentRole.Tester, "Anthropic", false)]
    public void Gpt61TerraMatchesGpt61SolTypedContextRules(AgentRole role, string provider, bool expected)
    {
        Assert.Equal(expected, WorkerContextHelpers.UsesTypedContextPackage(role, provider, "gpt-6.1-terra"));
        Assert.Equal(
            WorkerContextHelpers.UsesTypedContextPackage(role, provider, AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias),
            WorkerContextHelpers.UsesTypedContextPackage(role, provider, "gpt-6.1-terra"));
    }

    [Theory]
    [InlineData(AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias, AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias, "medium")]
    [InlineData(AgentCatalog.StaleOpenAiCodexSubscriptionModelAlias, AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias, "medium")]
    [InlineData("GpT-6-SoL", AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias, "medium")]
    [InlineData("gpt-6-luna", "gpt-6-luna", "medium")]
    [InlineData("gpt-6-astra", "gpt-6-astra", "medium")]
    [InlineData("custom-gpt-6-sol", "custom-gpt-6-sol", "low")]
    [InlineData(" gpt-6-sol", " gpt-6-sol", "low")]
    public void LoadRetiresOldAliasAndRepairsStaleCodexAlias(string storedAlias, string expectedAlias, string expectedReasoning)
    {
        var path = Path.Combine(CreateTempDirectory(), "agents.json");
        AgentCatalogStore.Save(path, new AgentCatalog([
            new AgentDefinition(
                new AgentId("openai-developer"), "OpenAI developer", AgentRole.Developer,
                new ModelProfile("OpenAI", "gpt-custom", ModelCapability.Text | ModelCapability.Code, SubscriptionMode.ApiKey),
                ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
                Subscription: new SubscriptionLaunchProfile("codex-cli", storedAlias))
        ]));

        var subscription = AgentCatalogStore.Load(path).GetRequired(AgentRole.Developer).Subscription!;
        Assert.Equal(expectedAlias, subscription.ModelAlias);
        Assert.Equal(expectedReasoning, subscription.ReasoningEffort);
    }

    [Fact]
    public void Gpt62AliasesUseTheMediumReasoningDefault()
    {
        Assert.Equal("gpt-6.2-sol", AgentCatalog.OpenAiGpt62SolSubscriptionModelAlias);
        Assert.Equal("gpt-6.2-astra", AgentCatalog.OpenAiGpt62AstraSubscriptionModelAlias);
        Assert.Equal("medium", AgentCatalog.DefaultSubscriptionReasoningEffort("OpenAI", "gpt-6.2-sol"));
        Assert.Equal("medium", AgentCatalog.DefaultSubscriptionReasoningEffort("OpenAI", "gpt-6.2-astra"));
        Assert.Equal("medium", AgentCatalog.DefaultSubscriptionReasoningEffort("OpenAI", "gpt-6.1-sol"));
        Assert.Null(AgentCatalog.DefaultSubscriptionReasoningEffort("Anthropic", "gpt-6.2-sol"));
        Assert.Null(AgentCatalog.DefaultSubscriptionReasoningEffort("Anthropic", "gpt-6.2-astra"));
    }

    [Theory]
    [InlineData("gpt-6.2-sol", AgentRole.Developer, "OpenAI", true)]
    [InlineData("gpt-6.2-sol", AgentRole.Tester, "OpenAI", true)]
    [InlineData("gpt-6.2-sol", AgentRole.Reviewer, "OpenAI", true)]
    [InlineData("gpt-6.2-sol", AgentRole.Planner, "OpenAI", true)]
    [InlineData("gpt-6.2-sol", AgentRole.Researcher, "OpenAI", true)]
    [InlineData("gpt-6.2-sol", AgentRole.Ideation, "OpenAI", false)]
    [InlineData("gpt-6.2-sol", AgentRole.Developer, "Anthropic", false)]
    [InlineData("gpt-6.2-astra", AgentRole.Developer, "OpenAI", true)]
    [InlineData("gpt-6.2-astra", AgentRole.Tester, "OpenAI", true)]
    [InlineData("gpt-6.2-astra", AgentRole.Reviewer, "OpenAI", true)]
    [InlineData("gpt-6.2-astra", AgentRole.Planner, "OpenAI", true)]
    [InlineData("gpt-6.2-astra", AgentRole.Researcher, "OpenAI", true)]
    [InlineData("gpt-6.2-astra", AgentRole.Ideation, "OpenAI", false)]
    [InlineData("gpt-6.2-astra", AgentRole.Developer, "Anthropic", false)]
    public void Gpt62AliasesMatchGpt61SolTypedContextRules(string alias, AgentRole role, string provider, bool expected)
    {
        Assert.Equal(expected, WorkerContextHelpers.UsesTypedContextPackage(role, provider, alias));
        Assert.Equal(expected, WorkerContextHelpers.UsesTypedContextPackage(role, provider, "gpt-6.1-sol"));
    }

    [Theory]
    [InlineData("gpt-6.2-sol")]
    [InlineData("gpt-6.2-astra")]
    public void LoadKeepsExplicitGpt62AliasesWithMediumReasoning(string storedAlias)
    {
        var path = Path.Combine(CreateTempDirectory(), "agents.json");
        AgentCatalogStore.Save(path, new AgentCatalog([
            new AgentDefinition(
                new AgentId("openai-developer"), "OpenAI developer", AgentRole.Developer,
                new ModelProfile("OpenAI", "gpt-custom", ModelCapability.Text | ModelCapability.Code, SubscriptionMode.ApiKey),
                ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
                Subscription: new SubscriptionLaunchProfile("codex-cli", storedAlias))
        ]));

        var subscription = AgentCatalogStore.Load(path).GetRequired(AgentRole.Developer).Subscription!;
        Assert.Equal(storedAlias, subscription.ModelAlias);
        Assert.Equal("medium", subscription.ReasoningEffort);
    }

}
