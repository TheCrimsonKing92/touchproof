namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record SelectedExecutionBackstopState(string RangeStartCommit, string? CoveredCommit = null,
    long LastLandingOrdinal = 0, IReadOnlyList<SelectedExecutionBackstopLanding>? Landings = null,
    SelectedExecutionBackstopRequest? Request = null, SelectedExecutionBackstopMissRecord? OpenMiss = null)
{
    internal IReadOnlyList<SelectedExecutionBackstopLanding> RangeLandings => Landings ?? [];
    internal IReadOnlyList<SelectedExecutionBackstopLanding> Uncovered => RangeLandings
        .Where(landing => landing.Applied || landing.RecordUnreadable).ToArray();
}
