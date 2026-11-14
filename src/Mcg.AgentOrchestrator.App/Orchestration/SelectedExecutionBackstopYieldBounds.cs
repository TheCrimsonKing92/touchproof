using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record SelectedExecutionBackstopYieldBounds(int MaxUncoveredLandings, double MaxOldestLandingAgeHours)
{
    internal static SelectedExecutionBackstopYieldBounds Default { get; } = new(
        SelectedExecutionBackstopYieldPolicy.MaximumUncoveredLandings,
        SelectedExecutionBackstopYieldPolicy.MaximumOldestLandingAgeHours);

    internal static SelectedExecutionBackstopYieldBounds FromSettings(AcceptanceGateEngineSettings settings) => new(
        settings.SelectedExecutionBackstopMaxUncoveredLandings ?? Default.MaxUncoveredLandings,
        settings.SelectedExecutionBackstopMaxOldestLandingAgeHours ?? Default.MaxOldestLandingAgeHours);

    internal string ToLogFields() => FormattableString.Invariant(
        $"selectedExecutionBackstopMaxUncoveredLandings={MaxUncoveredLandings} selectedExecutionBackstopMaxOldestLandingAgeHours={MaxOldestLandingAgeHours}");

    internal static string ToLogFieldsFor(SelectedExecutionBackstopCoordinator? coordinator) =>
        (coordinator?.Bounds ?? Default).ToLogFields();
}
