namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    internal sealed partial class AcceptanceManifestCheck
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsResolvedInfrastructureLane { get; init; }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool RequiresBuildSystemChange { get; init; }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool AllowsEmptySelection { get; init; }
    }

    private static AcceptanceBalancedLaneExpansion ResolveInfrastructureLanes(
        AcceptanceGateEngineSettings settings, string worktreePath)
    {
        if (settings.BalancedLanePool is not { } pool)
            return new(AcceptanceLaneMembership.ResolveOwnedCollections(settings.InfrastructureTestLanes, worktreePath), null);
        var medians = AcceptanceClassDurationHistoryStore.ReadMedians(worktreePath, pool.HistoryWindow, out var failureReason);
        var expansion = AcceptanceBalancedLaneExpander.Expand(settings.InfrastructureTestLanes,
            AcceptanceTestClassSourceScanner.Scan(worktreePath), medians, pool);
        return expansion with { Layout = expansion.Layout! with { HistoryReadFailure = failureReason } };
    }

    private static AcceptanceManifestCheck BuildInfrastructureShardCheck(
        AcceptanceManifestCheck check,
        AcceptanceTestLane lane) =>
        new()
        {
            Name = $"{check.Name}: {lane.Name}",
            Type = check.Type,
            Command = check.Command,
            Project = check.Project,
            Arguments = [.. check.Arguments, "--filter", lane.Filter],
            Pattern = check.Pattern,
            FilePath = check.FilePath,
            TimeoutMinutes = check.TimeoutMinutes,
            Advisory = check.Advisory,
            Runner = check.Runner,
            EstimatedSerialSeconds = lane.EstimatedSerialSeconds,
            ExclusiveResourceKeys = lane.ExclusiveResourceKeys,
            RequiresBuildSystemChange = lane.RequiresBuildSystemChange,
            IsResolvedInfrastructureLane = true,
            AllowsEmptySelection = lane.AllowsEmptySelection
        };

    private static IEnumerable<string> TranslateCheckMtpFilter(
        AcceptanceManifestCheck check, string filter) =>
        check.IsResolvedInfrastructureLane
            ? AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter(filter)
            : TranslateMtpFilter(filter);
}
