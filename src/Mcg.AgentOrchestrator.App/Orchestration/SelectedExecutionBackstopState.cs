namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record SelectedExecutionBackstopState(string RangeStartCommit, string? CoveredCommit = null,
    long LastLandingOrdinal = 0, IReadOnlyList<SelectedExecutionBackstopLanding>? Landings = null,
    SelectedExecutionBackstopRequest? Request = null, SelectedExecutionBackstopMissRecord? OpenMiss = null,
    IReadOnlyList<SelectedExecutionBackstopLandingInstant>? LandingInstants = null)
{
    internal IReadOnlyList<SelectedExecutionBackstopLanding> RangeLandings => Landings ?? [];
    internal IReadOnlyList<SelectedExecutionBackstopLanding> Uncovered => RangeLandings
        .Where(landing => landing.Applied || landing.RecordUnreadable).ToArray();
    internal IReadOnlyList<SelectedExecutionBackstopLandingInstant> RecordedLandingInstants => LandingInstants ?? [];
    internal DateTimeOffset? OldestUncoveredAt => Uncovered.MinBy(landing => landing.Ordinal) is { } oldest
        ? RecordedLandingInstants.FirstOrDefault(instant => instant.Ordinal == oldest.Ordinal)?.RecordedAt : null;
}

internal sealed record SelectedExecutionBackstopLandingInstant(long Ordinal, DateTimeOffset RecordedAt);
