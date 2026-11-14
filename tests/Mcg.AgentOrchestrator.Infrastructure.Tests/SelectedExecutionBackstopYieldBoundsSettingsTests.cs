using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: parses local strings and reads the repository manifest without changing it.
public sealed class SelectedExecutionBackstopYieldBoundsSettingsTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"engine\":{}}")]
    [InlineData("{\"engine\":{\"selectedExecutionBackstopMaxUncoveredLandings\":null,\"selectedExecutionBackstopMaxOldestLandingAgeHours\":null}}")]
    public void Parse_AbsentOrNullBounds_SettingsStayNullAndResolveDefaults(string json)
    {
        var settings = AcceptanceGateEngineSettings.Parse(json);

        Assert.Null(settings.SelectedExecutionBackstopMaxUncoveredLandings);
        Assert.Null(settings.SelectedExecutionBackstopMaxOldestLandingAgeHours);
        var bounds = SelectedExecutionBackstopYieldBounds.FromSettings(settings);
        Assert.Equal(5, bounds.MaxUncoveredLandings);
        Assert.Equal(2d, bounds.MaxOldestLandingAgeHours);
        Assert.Equal(SelectedExecutionBackstopYieldBounds.Default, bounds);
    }

    [Fact]
    public void Load_RepositoryManifest_ResolvesStartingBounds()
    {
        var settings = AcceptanceGateEngineSettings.Load(VerifiedRepositoryRoot.Find());

        Assert.Null(settings.SelectedExecutionBackstopMaxUncoveredLandings);
        Assert.Null(settings.SelectedExecutionBackstopMaxOldestLandingAgeHours);
        var bounds = SelectedExecutionBackstopYieldBounds.FromSettings(settings);
        Assert.Equal(5, bounds.MaxUncoveredLandings);
        Assert.Equal(2d, bounds.MaxOldestLandingAgeHours);
        Assert.Equal(SelectedExecutionBackstopYieldBounds.Default, bounds);
    }

    [Fact]
    public void Parse_DeclaredBounds_ReadsAndResolvesBothValues()
    {
        var settings = AcceptanceGateEngineSettings.Parse("""
            {"engine":{"selectedExecutionBackstopMaxUncoveredLandings":3,
                       "selectedExecutionBackstopMaxOldestLandingAgeHours":0.5}}
            """);

        Assert.Equal(3, settings.SelectedExecutionBackstopMaxUncoveredLandings);
        Assert.Equal(0.5, settings.SelectedExecutionBackstopMaxOldestLandingAgeHours);
        var bounds = SelectedExecutionBackstopYieldBounds.FromSettings(settings);
        Assert.Equal(3, bounds.MaxUncoveredLandings);
        Assert.Equal(0.5, bounds.MaxOldestLandingAgeHours);
    }

    [Theory]
    [InlineData("{\"engine\":{\"selectedExecutionBackstopMaxUncoveredLandings\":3}}", 3, 2d)]
    [InlineData("{\"engine\":{\"selectedExecutionBackstopMaxOldestLandingAgeHours\":0.5}}", 5, 0.5)]
    public void Parse_SingleOverride_DefaultsOnlyTheAbsentBound(string json, int landings, double hours)
    {
        var settings = AcceptanceGateEngineSettings.Parse(json);

        if (landings == 3)
        {
            Assert.Equal(3, settings.SelectedExecutionBackstopMaxUncoveredLandings);
            Assert.Null(settings.SelectedExecutionBackstopMaxOldestLandingAgeHours);
        }
        else
        {
            Assert.Null(settings.SelectedExecutionBackstopMaxUncoveredLandings);
            Assert.Equal(0.5, settings.SelectedExecutionBackstopMaxOldestLandingAgeHours);
        }
        Assert.Equal(new SelectedExecutionBackstopYieldBounds(landings, hours),
            SelectedExecutionBackstopYieldBounds.FromSettings(settings));
    }

    [Theory]
    [InlineData("selectedExecutionBackstopMaxUncoveredLandings", "0")]
    [InlineData("selectedExecutionBackstopMaxUncoveredLandings", "-1")]
    [InlineData("selectedExecutionBackstopMaxOldestLandingAgeHours", "0")]
    [InlineData("selectedExecutionBackstopMaxOldestLandingAgeHours", "-0.5")]
    public void Parse_NonPositiveBound_RejectsWithSettingName(string setting, string value)
    {
        var error = Assert.Throws<InvalidDataException>(() => AcceptanceGateEngineSettings.Parse(
            "{\"engine\":{\"" + setting + "\":" + value + "}}"));

        Assert.Contains(setting, error.Message, StringComparison.Ordinal);
    }
}
