using System.Globalization;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: bounds are immutable, culture is execution-local, and stores are fixture-local.
public sealed class SelectedExecutionBackstopYieldBoundsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ToLogFields_DefaultOrOverride_RendersInvariantValues(bool overridden)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var bounds = overridden ? new SelectedExecutionBackstopYieldBounds(3, 0.5)
                : SelectedExecutionBackstopYieldBounds.Default;

            Assert.Equal(overridden
                ? "selectedExecutionBackstopMaxUncoveredLandings=3 selectedExecutionBackstopMaxOldestLandingAgeHours=0.5"
                : "selectedExecutionBackstopMaxUncoveredLandings=5 selectedExecutionBackstopMaxOldestLandingAgeHours=2",
                bounds.ToLogFields());
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void ToLogFieldsFor_NoCoordinator_UsesDefaultBounds() =>
        Assert.Equal(SelectedExecutionBackstopYieldBounds.Default.ToLogFields(),
            SelectedExecutionBackstopYieldBounds.ToLogFieldsFor(null));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ToLogFieldsFor_Coordinator_UsesItsEffectiveBounds(bool overridden)
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var bounds = overridden ? new SelectedExecutionBackstopYieldBounds(3, 0.5) : null;
        var coordinator = new SelectedExecutionBackstopCoordinator(fixture.Store, fixture, fixture.Items,
            fixture.Home, () => fixture.Now, fixture.Lines.Enqueue, bounds: bounds);

        Assert.Same(bounds ?? SelectedExecutionBackstopYieldBounds.Default, coordinator.Bounds);
        Assert.Equal((bounds ?? SelectedExecutionBackstopYieldBounds.Default).ToLogFields(),
            SelectedExecutionBackstopYieldBounds.ToLogFieldsFor(coordinator));
    }
}
