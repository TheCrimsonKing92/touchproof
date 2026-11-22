using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record AcceptanceGateOutcomeObservation(
    string AttemptId, string Directory, DateTimeOffset RecordedAt, DateTimeOffset StartedAt,
    string? LaunchMainCommit, string Mode, string? FullGateReason, int? ExecutedClassCount,
    double SelectionMilliseconds, string Outcome)
{
    public string Kind => AcceptanceGateOutcomeLedger.GateKind;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HasSoloMetadata { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OwnerFallbackLaunchMainCommit { get; init; }
}

internal sealed record SoloGateOutcomeProvenance(DateTimeOffset StartedAt, string? LaunchMainCommit, string Outcome);

internal static class AcceptanceGateOutcomeLedger
{
    internal const string FileName = "cost-goal-outcome-gates.jsonl";
    internal const string AttemptRoot = "acceptance-gate-attempts";
    internal const string GateKind = "gate";
    internal const string InvalidationKind = "invalidation";
    internal const string BackfillKind = "backfill-complete";
    private const string PlanTargetPrefix = "GATE_LANE_PLAN ";
    private static readonly Regex GateAttemptId = new(@"-(?<time>[0-9]{17})-[0-9a-f]{32}$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    internal static DateTimeOffset? RecordedAt(string attemptId)
    {
        var match = GateAttemptId.Match(attemptId);
        return match.Success && DateTimeOffset.TryParseExact(match.Groups["time"].Value, "yyyyMMddHHmmssfff",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : null;
    }

    internal static AcceptanceSelectedExecutionRecord? ReadSelection(AcceptanceGateProgress progress, string attemptId)
    {
        if (!progress.CurrentTarget.StartsWith(PlanTargetPrefix, StringComparison.Ordinal) ||
            !File.Exists(progress.HeartbeatPath)) return null;
        try
        {
            var plan = JsonSerializer.Deserialize<AcceptanceGateLanePlan>(
                progress.CurrentTarget.AsSpan(PlanTargetPrefix.Length), AcceptanceGateLanePlan.JsonOptions);
            // Match OwnerConsoleGateLanePlanReader, which validates the historical lane plans.
            if (plan is null || plan.SchemaVersion != AcceptanceGateLanePlan.CurrentSchemaVersion ||
                plan.AttemptId != attemptId || plan.Scheduled is null || plan.Skipped is null ||
                plan.Scheduled.Any(string.IsNullOrWhiteSpace) ||
                plan.Skipped.Any(row => row is null || string.IsNullOrWhiteSpace(row.Name) ||
                    string.IsNullOrWhiteSpace(row.Reason))) return null;
            var names = plan.Scheduled.Concat(plan.Skipped.Select(row => row.Name)).ToArray();
            return names.Distinct(StringComparer.Ordinal).Count() == names.Length ? plan.SelectedExecution : null;
        }
        catch (JsonException) { return null; }
    }

    internal static bool IsRecordedGate(ConductorParallelAcceptanceRunArtifact result) =>
        result is { Kind: "accepted", DispatchKind: GateKind, Acceptance.TestResultPaths.Count: > 0 };

    internal static AcceptanceGateOutcomeObservation CreateObservation(string prefix, DateTimeOffset recordedAt,
        ConductorParallelAcceptanceRunArtifact result, AcceptanceSelectedExecutionRecord selection,
        SoloGateOutcomeProvenance? solo = null)
    {
        var attemptId = Path.GetFileName(prefix);
        var ownerMain = attemptId.Contains("-gate-", StringComparison.Ordinal) ? result.MainHeadSha : null;
        var launchCommit = solo is not null ? solo.LaunchMainCommit : ownerMain;
        var outcome = solo?.Outcome ?? (result.Acceptance!.Passed ? "Passed" : "Failed");
        return new(attemptId, Path.GetFileName(Path.GetDirectoryName(prefix))!, recordedAt,
            solo?.StartedAt ?? recordedAt, launchCommit, selection.Mode, selection.FullGateReason,
            selection.ExecutedClassCount, selection.SelectionMilliseconds, outcome)
        {
            HasSoloMetadata = solo is not null,
            OwnerFallbackLaunchMainCommit = solo is not null ? ownerMain : null
        };
    }

    // Take this before exposing the result. Backfill can only see this publication after this offset.
    internal static long CapturePublicationOffset(string prefix, AcceptanceSelectedExecutionRecord? selection)
    {
        if (selection is null || LedgerPath(prefix) is not { } path) return 0;
        var file = new FileInfo(path);
        return file.Exists ? file.Length : 0;
    }

    internal static void Publish(string prefix, ConductorParallelAcceptanceRunArtifact result,
        AcceptanceSelectedExecutionRecord? selection, long publicationOffset, SoloGateOutcomeProvenance? solo = null)
    {
        if (selection is null || !IsRecordedGate(result) || RecordedAt(Path.GetFileName(prefix)) is not { } recordedAt ||
            LedgerPath(prefix) is not { } path) return;
        var row = CreateObservation(prefix, recordedAt, result, selection, solo);
        try
        {
            WithBackfillLock(path, () =>
            {
                if (!HasPublishedObservation(path, publicationOffset, row))
                    SharedJsonlFile.AppendLine(path, JsonSerializer.Serialize(row, Json));
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            System.Diagnostics.Trace.TraceWarning($"Acceptance gate outcome ledger unavailable for {prefix}: {ex.Message}");
        }
    }

    internal static void Invalidate(string prefix)
    {
        if (LedgerPath(prefix) is not { } path) return;
        SharedJsonlFile.AppendLine(path, JsonSerializer.Serialize(new
        {
            Kind = InvalidationKind,
            AttemptId = Path.GetFileName(prefix),
            Directory = Path.GetFileName(Path.GetDirectoryName(prefix)),
            RequiresStaleMetadata = true
        }, Json));
    }

    internal static void WithBackfillLock(string path, Action action)
    {
        var lockPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) lockPath = lockPath.ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lockPath)));
        using var mutex = new Mutex(false, $"Mcg.CostGoalOutcomeGateBackfill.{hash}");
        try { mutex.WaitOne(); }
        catch (AbandonedMutexException) { /* Read the durable ledger after taking ownership. */ }
        try { action(); }
        finally { mutex.ReleaseMutex(); }
    }

    private static string? LedgerPath(string prefix)
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(prefix));
        return Path.GetFileName(root) == AttemptRoot ? Path.Combine(Path.GetDirectoryName(root!)!, FileName) : null;
    }

    private static bool HasPublishedObservation(string path, long offset, AcceptanceGateOutcomeObservation row)
    {
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
        using (stream)
        {
            if (offset > stream.Length) offset = 0;
            if (offset == stream.Length) return false;
            stream.Position = offset;
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    var json = JsonSerializer.Deserialize<JsonElement>(line);
                    if (json.ValueKind == JsonValueKind.Object &&
                        json.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.ValueEquals(GateKind) &&
                        json.TryGetProperty("attemptId", out var id) && id.ValueKind == JsonValueKind.String && id.ValueEquals(row.AttemptId) &&
                        json.TryGetProperty("directory", out var directory) && directory.ValueKind == JsonValueKind.String &&
                        directory.ValueEquals(row.Directory)) return true;
                }
                catch (JsonException) { /* A publication offset can fall inside another append. */ }
            }
        }
        return false;
    }
}
