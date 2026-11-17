namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record SelectedExecutionBackstopState(string RangeStartCommit, string? CoveredCommit = null,
    long LastLandingOrdinal = 0, IReadOnlyList<SelectedExecutionBackstopLanding>? Landings = null,
    // Kept for deserializing stored requests; service clears them before reconciliation.
    SelectedExecutionBackstopRequest? Request = null, SelectedExecutionBackstopMissRecord? OpenMiss = null,
    IReadOnlyList<SelectedExecutionBackstopLandingInstant>? LandingInstants = null)
{
    internal IReadOnlyList<SelectedExecutionBackstopLanding> RangeLandings => Landings ?? [];
    internal IReadOnlyList<SelectedExecutionBackstopLandingInstant> RecordedLandingInstants => LandingInstants ?? [];
}

internal sealed record SelectedExecutionBackstopLandingInstant(long Ordinal, DateTimeOffset RecordedAt);
