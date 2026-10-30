namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class AcceptanceBalancedLanePoolSettings
{
    public int LaneCount { get; init; } = 4;
    public int HistoryWindow { get; init; } = 5;
    public string LaneNamePrefix { get; init; } = "Balanced";
    public string ResidualLaneName { get; init; } = "Infrastructure residual";
    public double DefaultClassSecondsWhenNoHistory { get; init; } = 1;

    internal void Validate(IReadOnlyList<AcceptanceTestLane> lanes)
    {
        if (LaneCount < 1 || HistoryWindow < 1 || HistoryWindow > 100 ||
            string.IsNullOrWhiteSpace(LaneNamePrefix) || string.IsNullOrWhiteSpace(ResidualLaneName) ||
            !double.IsFinite(DefaultClassSecondsWhenNoHistory) || DefaultClassSecondsWhenNoHistory <= 0)
            throw new InvalidDataException("Balanced lane pool requires positive lane count, history window (1-100), fallback seconds and non-empty names.");
        var names = Enumerable.Range(1, LaneCount).Select(index => $"{LaneNamePrefix} {index}")
            .Append(ResidualLaneName).ToArray();
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length ||
            lanes.Any(lane => names.Contains(lane.Name, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("Balanced lane names collide with another acceptance lane.");
    }
}
