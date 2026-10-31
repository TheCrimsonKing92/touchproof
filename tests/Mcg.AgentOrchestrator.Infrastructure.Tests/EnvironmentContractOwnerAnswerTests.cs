using Mcg.AgentOrchestrator.Infrastructure;

public sealed class EnvironmentContractOwnerAnswerTests
{
    [Fact]
    public void OwnerAnswers_AddOverrideRemoveAndCompareTools()
    {
        var model = EnvironmentContractTestFixture.Model();
        var original = EnvironmentContract.FromManifest(LearnedAcceptanceManifest.Build(model, EnvironmentContractTestFixture.Answers()).Json!);
        var answers = EnvironmentContractTestFixture.Answers(("environment/tools/cargo nextest", "none"),
            ("environment/tools/cargo", "cargo -V"), ("environment/tools/cargo/pattern", "cargo 1\\."),
            ("environment/tools/custom", "custom --version"), ("environment/tools/custom/variables", "CUSTOM_HOME"));
        var applied = OwnerAnswerApplier.Apply(model, answers);
        Assert.DoesNotContain(applied.StaleAnswers, answer => answer.Answer.FactKey.StartsWith("environment/tools/", StringComparison.Ordinal));
        var contract = EnvironmentContract.FromManifest(LearnedAcceptanceManifest.Build(model, answers).Json!);
        Assert.DoesNotContain(contract.Tools, tool => tool.Name == "cargo nextest");
        var cargo = Assert.Single(contract.Tools, tool => tool.Name == "cargo");
        Assert.Equal(["-V"], cargo.Arguments);
        Assert.Equal("cargo 1\\.", cargo.OutputPattern);
        Assert.Equal(["CUSTOM_HOME"], Assert.Single(contract.Tools, tool => tool.Name == "custom").Variables);
        Assert.Equal(["Differs tool cargo", "MissingFromManifest tool cargo nextest", "MissingFromLearned tool custom"],
            EnvironmentContractComparer.Compare(original, contract));
        Assert.All(EnvironmentContractComparer.Compare(contract, contract), row => Assert.StartsWith("Match tool ", row));
    }

    [Theory]
    [InlineData("environment/tools/cargo", "cargo --version | echo")]
    [InlineData("environment/tools/cargo/pattern", "[")]
    [InlineData("environment/tools/cargo/pattern", "(?=unsupported)")]
    public void InvalidOwnerProbe_IsRejected(string key, string value) =>
        Assert.Throws<ArgumentException>(() => OwnerAnswerValidator.Validate(key, value));

    [Theory]
    [InlineData("{\"environmentContract\":null}")]
    [InlineData("{}")]
    [InlineData("{\"environmentContract\":{\"version\":2,\"tools\":[]}}")]
    [InlineData("{\"environmentContract\":{\"version\":1,\"tools\":[{}]}}")]
    public void MalformedContract_IsRefused(string json) =>
        Assert.Throws<System.Text.Json.JsonException>(() => EnvironmentContract.FromManifest(json));
}
