using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ExecutorSlotSampleReader
{
    internal static IReadOnlyList<ExecutorSlotSample> Read(string healthPath, string executorId,
        DateTimeOffset since, DateTimeOffset now)
    {
        var rows = new List<RemoteExecutorOutcomeRow>();
        var inferred = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in SharedJsonlFile.ReadLines(healthPath))
        {
            var record = RemoteExecutorHealthLedger.TryParse(line);
            if (record is null || record.ExecutorId != executorId) continue;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            double? seconds = null;
            DateTimeOffset? started = null;
            var focused = record.Attempt is null && (record.GoalId is not null || record.EvidenceShadow is not null);
            if (root.TryGetProperty("attempt", out var attempt) && attempt.ValueKind == JsonValueKind.Object)
            {
                if (attempt.TryGetProperty("last_status", out var status) && status.ValueKind == JsonValueKind.Object &&
                    status.TryGetProperty("seconds", out var duration) && duration.ValueKind == JsonValueKind.Number &&
                    duration.TryGetDouble(out var value)) seconds = value;
                if (attempt.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
                    foreach (var step in steps.EnumerateArray())
                        if (step.ValueKind == JsonValueKind.Object && step.TryGetProperty("started_at", out var start) &&
                            start.ValueKind == JsonValueKind.String && start.TryGetDateTimeOffset(out var at) &&
                            (started is null || at < started)) started = at;
            }
            if (focused && record.RemoteDurationSeconds is > 0 && double.IsFinite(record.RemoteDurationSeconds.Value))
            {
                seconds = record.RemoteDurationSeconds;
                try
                {
                    started = record.ObservedAt.AddSeconds(-seconds.Value);
                    inferred.Add(record.GateAttemptId);
                }
                catch (ArgumentOutOfRangeException) { started = null; }
            }
            rows.Add(new(record.ObservedAt, record.ExecutorId, record.GateAttemptId,
                focused ? "focused-evidence" : record.Lane,
                JsonNamingPolicy.KebabCaseLower.ConvertName(record.Outcome.ToString()), record.Reason, seconds, started));
        }
        return ExecutorSlotConcurrencyAttribution.Build(rows, executorId, since, now)
            .Select(sample => inferred.Contains(sample.AttemptId) ? sample with { IntervalSource = "remote-duration" } : sample).ToArray();
    }

    internal static ExecutorSlotControllerSettings ReadSettings(string path)
    {
        var settings = new ExecutorSlotControllerSettings();
        if (File.Exists(path))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("executorSlotController", out var policy))
                settings = policy.Deserialize<ExecutorSlotControllerSettings>(new JsonSerializerOptions
                    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling =
                        System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
                    ?? throw new InvalidOperationException("invalid-executor-slot-controller-settings");
        }
        settings.Validate();
        return settings;
    }
}
