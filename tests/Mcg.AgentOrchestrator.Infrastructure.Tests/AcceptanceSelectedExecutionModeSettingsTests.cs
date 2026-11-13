using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: parses manifest strings and reads the verified repository manifest without changing shared state.
public sealed class AcceptanceSelectedExecutionModeSettingsTests
{
    [Fact]
    public void Load_CheckedInManifest_UsesShadow() =>
        Assert.Equal(AcceptanceSelectedExecutionMode.Shadow,
            AcceptanceGateEngineSettings.Load(VerifiedRepositoryRoot.Find()).SelectedExecutionMode);

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"engine\":{}}")]
    public void Parse_OmittedMode_DefaultsOff(string json) =>
        Assert.Equal(AcceptanceSelectedExecutionMode.Off, AcceptanceGateEngineSettings.Parse(json).SelectedExecutionMode);

    [Theory]
    [InlineData("Off")]
    [InlineData("Shadow")]
    [InlineData("Enforce")]
    [InlineData("shadow")]
    public void Parse_DeclaredMode_ResolvesName(string value)
    {
        var settings = AcceptanceGateEngineSettings.Parse(
            "{\"engine\":{\"selectedExecutionMode\":" + JsonSerializer.Serialize(value) + "}}");
        Assert.Equal(value.ToUpperInvariant(), settings.SelectedExecutionMode.ToString().ToUpperInvariant());
    }

    [Theory]
    [InlineData("\"unknown\"")]
    [InlineData("\"1\"")]
    [InlineData("1")]
    [InlineData("null")]
    public void Parse_InvalidMode_RejectsWithSettingDiagnostic(string value)
    {
        var error = Assert.Throws<InvalidDataException>(() => AcceptanceGateEngineSettings.Parse(
            "{\"engine\":{\"selectedExecutionMode\":" + value + "}}"));
        Assert.Equal("Acceptance manifest engine selectedExecutionMode must be Off, Shadow, or Enforce.", error.Message);
    }
}
