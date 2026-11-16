using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

// Supplemental observations only: these rows never determine gate liveness or acceptance state.
internal static class GateStatusRemoteWaitDetail
{
    internal enum OwnerCheck { Alive, Dead, Unverified }

    internal sealed record Row(string GoalId, string AttemptId, int? OwnerProcessId,
        string? CandidateSha, DateTimeOffset? LastProgressAt, TimeSpan? Age,
        ConductorParallelAcceptanceAttemptOutcome? Outcome, OwnerCheck Owner,
        bool IdentityVerified, bool Stale, string HeartbeatPath)
    {
        internal string State => Stale ? "stale" :
            Outcome is { } outcome && outcome != ConductorParallelAcceptanceAttemptOutcome.Running ? "terminal" :
            IdentityVerified && Owner == OwnerCheck.Alive && Age is not null &&
            Outcome == ConductorParallelAcceptanceAttemptOutcome.Running ? "running" : "unverified";
    }

    internal static IReadOnlyList<Row> Read(string root, DateTimeOffset observedAt, TimeSpan window,
        Func<int, DateTimeOffset?>? startTimeOf = null, Func<int, bool>? isProcessAbsent = null)
    {
        var rows = new List<Row>();
        if (!Directory.Exists(root)) return rows;
        try
        {
            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*.heartbeat.json",
                         new EnumerationOptions { RecurseSubdirectories = true,
                             MaxRecursionDepth = ConductorAttemptHeartbeatScanner.MaxHeartbeatRecursionDepth,
                             IgnoreInaccessible = true, AttributesToSkip = 0 }))
            {
                try
                {
                    if (file.Name.EndsWith(".gate-heartbeat.json", StringComparison.OrdinalIgnoreCase)) continue;
                    using var heartbeat = Open(file.FullName);
                    if (!string.Equals(Text(heartbeat.RootElement, "state"), "running", StringComparison.OrdinalIgnoreCase)) continue;
                    var progress = Timestamp(heartbeat.RootElement, "lastProgressAt");
                    var attemptId = file.Name[..^".heartbeat.json".Length];
                    var metadata = ReadMetadata(Path.Combine(file.DirectoryName!, attemptId + ".attempt.json"), attemptId);
                    var owner = CheckOwner(metadata, startTimeOf ?? RemoteExecutorOccupancy.DefaultStartTime,
                        isProcessAbsent ?? AcceptanceHolderIdentity.IsProcessAbsent);
                    rows.Add(new Row(metadata?.GoalId ?? "unknown", attemptId, metadata?.OwnerProcessId,
                        metadata?.CandidateSha, progress, progress is { } at && at <= observedAt ? observedAt - at : null,
                        metadata?.Outcome, owner, metadata?.IdentityVerified == true,
                        file.LastWriteTimeUtc < (observedAt - window).UtcDateTime, file.FullName));
                }
                catch (IOException) { } // Best-effort observation while writers replace their artifacts.
                catch (UnauthorizedAccessException) { }
                catch (JsonException) { }
            }
        }
        catch (IOException) { } // Keep observations already gathered if a directory disappears.
        catch (UnauthorizedAccessException) { }
        return rows.OrderBy(row => row.GoalId, StringComparer.Ordinal)
            .ThenBy(row => row.AttemptId, StringComparer.Ordinal)
            .ThenBy(row => row.HeartbeatPath, StringComparer.Ordinal).ToArray();
    }

    private sealed record Metadata(string GoalId, int? OwnerProcessId, string? CandidateSha,
        DateTimeOffset? OwnerStartedAt, ConductorParallelAcceptanceAttemptOutcome? Outcome)
    {
        internal bool IdentityVerified => OwnerProcessId is not null && OwnerStartedAt is not null &&
            !string.IsNullOrWhiteSpace(CandidateSha) && Outcome is not null;
    }

    private static Metadata? ReadMetadata(string path, string attemptId)
    {
        try
        {
            using var document = Open(path);
            var element = document.RootElement;
            if (!string.Equals(Text(element, "attemptId"), attemptId, StringComparison.Ordinal)) return null;
            var goalId = Text(element, "goalId");
            if (string.IsNullOrWhiteSpace(goalId)) return null;
            var pid = Property(element, "ownerProcessId");
            var ownerPid = pid.ValueKind == JsonValueKind.Number && pid.TryGetInt32(out var value) && value > 0
                ? (int?)value : null;
            var outcomeElement = Property(element, "outcome");
            ConductorParallelAcceptanceAttemptOutcome? outcome = null;
            if (outcomeElement.ValueKind == JsonValueKind.Number && outcomeElement.TryGetInt32(out var number) &&
                Enum.IsDefined(typeof(ConductorParallelAcceptanceAttemptOutcome), number))
                outcome = (ConductorParallelAcceptanceAttemptOutcome)number;
            else if (outcomeElement.ValueKind == JsonValueKind.String &&
                     Enum.TryParse<ConductorParallelAcceptanceAttemptOutcome>(outcomeElement.GetString(), true, out var named) &&
                     Enum.IsDefined(named)) outcome = named;
            return new Metadata(goalId, ownerPid, Text(element, "branchHeadSha"),
                Timestamp(element, "ownerProcessStartedAt"), outcome);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }

    private static OwnerCheck CheckOwner(Metadata? metadata, Func<int, DateTimeOffset?> startTimeOf,
        Func<int, bool> isProcessAbsent)
    {
        if (metadata?.IdentityVerified != true || metadata.OwnerProcessId is not { } pid || metadata.OwnerStartedAt is not { } recorded)
            return OwnerCheck.Unverified;
        try
        {
            if (startTimeOf(pid) is { } live) return live == recorded ? OwnerCheck.Alive : OwnerCheck.Dead;
            return isProcessAbsent(pid) ? OwnerCheck.Dead : OwnerCheck.Unverified;
        }
        catch (Exception) { return OwnerCheck.Unverified; } // An inaccessible owner is not proof of death.
    }

    private static JsonDocument Open(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonDocument.Parse(stream);
    }

    private static JsonElement Property(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return default;
    }

    private static string? Text(JsonElement element, string name)
    {
        var text = Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static DateTimeOffset? Timestamp(JsonElement element, string name) =>
        DateTimeOffset.TryParse(Text(element, name), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;
}
