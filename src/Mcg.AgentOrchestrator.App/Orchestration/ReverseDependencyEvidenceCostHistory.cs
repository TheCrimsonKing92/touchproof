using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Read paired typed receipts only; the workspace directory owns the repository's attempt history.
internal static class ReverseDependencyEvidenceCostHistory
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static ReverseDependencyEvidenceCostInputs Read(string? worktree, string? orchestratorDirectory,
        IReadOnlyList<string> selections, IReadOnlyList<string> changedFiles, string? projectHomeDirectory = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        var now = (utcNow ?? (() => DateTimeOffset.UtcNow))();
        if (string.IsNullOrWhiteSpace(worktree) || string.IsNullOrWhiteSpace(orchestratorDirectory))
            return ReverseDependencyEvidenceCostInputs.Unavailable("repository or orchestrator directory unavailable");
        try
        {
            var settings = AcceptanceGateEngineSettings.Load(worktree, projectHomeDirectory).BalancedLanePool
                ?? new AcceptanceBalancedLanePoolSettings();
            var medians = AcceptanceClassDurationHistoryStore.ReadMedians(worktree, settings.HistoryWindow, out var failure);
            if (failure is not null) return ReverseDependencyEvidenceCostInputs.Unavailable(failure);
            var classes = ReverseDependencyEvidenceGateClasses.Read(worktree, changedFiles, projectHomeDirectory);
            var attempts = ReadAttempts(worktree, orchestratorDirectory, now);
            return ReverseDependencyEvidenceCostInputs.Compute(selections, classes, medians,
                settings.DefaultClassSecondsWhenNoHistory, attempts, now);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or
            ArgumentException or InvalidOperationException or InvalidDataException or NotSupportedException or System.Xml.XmlException)
        {
            return ReverseDependencyEvidenceCostInputs.Unavailable($"input read failed: {error.GetType().Name}: {error.Message}");
        }
    }

    private static IEnumerable<ReverseDependencyEvidenceCostInputs.Attempt> ReadAttempts(
        string worktree, string orchestratorDirectory, DateTimeOffset now)
    {
        var root = Path.Combine(orchestratorDirectory, "acceptance-gate-attempts");
        if (!Directory.Exists(root)) yield break;
        var repository = AcceptancePartitionVerdictCache.ResolveHostStateRoot(worktree);
        foreach (var path in Directory.EnumerateFiles(root, "*.attempt.json", SearchOption.AllDirectories))
        {
            var metadata = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException($"Null attempt receipt: {path}");
            if (metadata.Kind != "gate" || metadata.CompletedAt is not { } completed ||
                completed < now.AddDays(-14) || completed > now) continue;
            if (metadata.ExecutionDirectory is { } execution && !Path.GetFullPath(repository).Equals(
                    Path.GetFullPath(AcceptancePartitionVerdictCache.ResolveHostStateRoot(execution)),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
            var resultPath = path[..^".attempt.json".Length] + ".result.json";
            // Older completed attempts with no recorded attribution still belong to the denominator.
            var result = File.Exists(resultPath)
                ? JsonSerializer.Deserialize<ConductorParallelAcceptanceRunArtifact>(File.ReadAllText(resultPath), Json)
                    ?? throw new InvalidDataException($"Null result receipt: {resultPath}")
                : null;
            yield return new(metadata, result);
        }
    }
}
