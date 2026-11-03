using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure policy with invocation-local inputs.
public sealed class ContractedToolSkipPolicyTests
{
    [Theory]
    [InlineData("cargo nextest not found")]
    [InlineData("cargo nextest is not installed")]
    [InlineData("cargo nextest unavailable")]
    [InlineData("missing required tool: cargo nextest")]
    public void ExplicitReason_CaseTrimAndPeriod_ReturnsContractName(string reason)
    {
        var contract = Contract("cargo nextest");
        foreach (var message in new[] { reason, reason + ".", " \t" + reason.ToUpperInvariant() + ".\r\n" })
            Assert.Equal("cargo nextest", ContractedToolSkipPolicy.MissingTool(message, contract));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("java not found")]
    [InlineData("cargo not found")]
    [InlineData("cargo nextest-extra not found")]
    [InlineData("\"cargo nextest not found\"")]
    [InlineData("'cargo nextest not found'")]
    [InlineData("Setup says cargo nextest not found")]
    [InlineData("cargo nextest not found; retry later")]
    [InlineData("cargo  nextest not found")]
    [InlineData("cargo nextest  not found")]
    [InlineData("cargo nextest not found..")]
    [InlineData("missing required tool: cargo nextest.exe")]
    public void OtherReason_DoesNotInferMissingContractedTool(string? reason)
    {
        Assert.Null(ContractedToolSkipPolicy.MissingTool(reason, Contract("cargo nextest")));
    }

    [Fact]
    public void NoContract_ExplicitReason_KeepsSkipBehavior()
    {
        Assert.Null(ContractedToolSkipPolicy.MissingTool("java not found", null));
        Assert.Null(ContractedToolSkipPolicy.MissingTool("java not found", new(1, [])));
    }

    internal static EnvironmentContract Contract(params string[] names) =>
        new(1, names.Select(name => new EnvironmentToolRequirement(name, "probe", [])).ToArray());
}
