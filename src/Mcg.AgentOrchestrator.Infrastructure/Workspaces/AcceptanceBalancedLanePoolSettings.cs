namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class AcceptanceBalancedLanePoolSettings
{
    public int LaneCount { get; init; } = 4;
    public int HistoryWindow { get; init; } = 5;
    public string LaneNamePrefix { get; init; } = "Balanced";
    public string ResidualLaneName { get; init; } = "Infrastructure residual";
    public double DefaultClassSecondsWhenNoHistory { get; init; } = 1;
    public IReadOnlyList<string> TraitExclusions { get; init; } = [];
    public IReadOnlyList<string> ExclusiveResourceKeys { get; init; } = [];
    // Named lanes may share the pool's trait guards without losing their raw filter contract.
    public IReadOnlyList<string> PoolEligibleLaneNames { get; init; } = [];

    internal void Validate(IReadOnlyList<AcceptanceTestLane> lanes)
    {
        if (LaneCount < 1 || HistoryWindow < 1 || HistoryWindow > 100 ||
            string.IsNullOrWhiteSpace(LaneNamePrefix) || string.IsNullOrWhiteSpace(ResidualLaneName) ||
            !double.IsFinite(DefaultClassSecondsWhenNoHistory) || DefaultClassSecondsWhenNoHistory <= 0)
            throw new InvalidDataException("Balanced lane pool requires positive lane count, history window (1-100), fallback seconds and non-empty names.");
        if (TraitExclusions is null || TraitExclusions.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Balanced lane pool trait exclusions must contain non-empty exclusion expressions.");
        foreach (var exclusion in TraitExclusions)
        {
            var arguments = AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter(exclusion).ToArray();
            if (arguments.Length != 2 || arguments[0] != "--filter-not-trait")
                throw new InvalidDataException("Balanced lane pool trait exclusions must each declare one trait exclusion.");
        }
        if (PoolEligibleLaneNames is null || PoolEligibleLaneNames.Any(string.IsNullOrWhiteSpace) ||
            PoolEligibleLaneNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != PoolEligibleLaneNames.Count)
            throw new InvalidDataException("Balanced lane pool eligible lane names must be non-empty and unique.");
        var poolArguments = TraitExclusions.SelectMany(AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter).ToArray();
        // The packer validates pool settings without a lane catalog; expansion validates the references.
        foreach (var name in PoolEligibleLaneNames.Where(_ => lanes.Count > 0))
        {
            var lane = lanes.SingleOrDefault(lane => lane.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"Balanced lane pool eligible lane '{name}' does not exist.");
            if (lane.ExclusiveResourceKeys.Count > 0 || lane.OwnedCollections.Count > 0 || lane.RequiresBuildSystemChange)
                throw new InvalidDataException($"Balanced lane pool eligible lane '{name}' has isolation requirements.");
            var arguments = AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter(lane.Filter).ToArray();
            if (arguments.Chunk(2).Any(pair => pair[0] == "--filter-not-trait" &&
                    !AcceptanceCheckCommandBuilder.HasMtpTraitExclusion(poolArguments, pair[1])))
                throw new InvalidDataException($"Balanced lane pool eligible lane '{name}' has a trait exclusion missing from the pool.");
        }
        if (ExclusiveResourceKeys is null || ExclusiveResourceKeys.Any(string.IsNullOrWhiteSpace) ||
            ExclusiveResourceKeys.Select(key => key.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != ExclusiveResourceKeys.Count)
            throw new InvalidDataException("Balanced lane pool exclusive resource keys must be non-empty and unique.");
        var names = Enumerable.Range(1, LaneCount).Select(index => $"{LaneNamePrefix} {index}")
            .Append(ResidualLaneName).ToArray();
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length ||
            lanes.Any(lane => names.Contains(lane.Name, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("Balanced lane names collide with another acceptance lane.");
    }
}
