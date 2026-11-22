using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class CostGoalOutcomeGateLedgerReader(string orchestratorDirectory)
{
    private readonly string path = Path.GetFullPath(Path.Combine(orchestratorDirectory, AcceptanceGateOutcomeLedger.FileName));
    private readonly Dictionary<(string Directory, string AttemptId), RetainedObservation> observations = new();
    private readonly HashSet<(string Directory, string AttemptId)> invalidated = [];
    private readonly HashSet<(string Directory, string AttemptId)> pendingInvalidations = [];
    private long offset;
    private bool backfilled;

    internal int GateFileReads { get; private set; }
    internal int LinesReadLastRun { get; private set; }

    internal OutcomeGateObservation[] Read(DateTimeOffset recordedBefore)
    {
        LinesReadLastRun = 0;
        ReadTail();
        if (!backfilled) Backfill();
        ConfirmInvalidations();

        return observations.Values
            .Where(entry => entry.Gate.RecordedAt < recordedBefore && HasRetainedEvidence(entry.Gate.Id))
            .Select(entry => entry.WithoutMetadata is { } fallback && !File.Exists(entry.Gate.Id + ".attempt.json")
                ? fallback : entry.Gate)
            .OrderByDescending(observation => observation.RecordedAt)
            .ThenBy(observation => observation.Id, StringComparer.Ordinal)
            .ToArray();
    }

    // Retention can remove one attempt's files while leaving other attempts in its directory.
    private static bool HasRetainedEvidence(string prefix) =>
        File.Exists(prefix + AcceptanceGateLanePlan.ArtifactSuffix) && File.Exists(prefix + ".result.json");

    private void Backfill() => AcceptanceGateOutcomeLedger.WithBackfillLock(path, () =>
    {
        ReadTail();
        if (backfilled) return;

        var lines = CostGoalOutcomeMetric.ScanRecordedGateRows(orchestratorDirectory, DateTimeOffset.MaxValue,
            (directory, attemptId) => !observations.ContainsKey((directory, attemptId)) &&
                !invalidated.Contains((directory, attemptId)),
            () => GateFileReads++)
            .Select(row => JsonSerializer.Serialize(row, AcceptanceGateOutcomeLedger.Json)).ToList();
        lines.Add(JsonSerializer.Serialize(new { Kind = AcceptanceGateOutcomeLedger.BackfillKind }, AcceptanceGateOutcomeLedger.Json));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        SharedJsonlFile.AppendLines(path, lines);
        ReadTail();
    });

    private void ConfirmInvalidations()
    {
        foreach (var key in pendingInvalidations.ToArray())
        {
            var metadataPath = Path.Combine(orchestratorDirectory, AcceptanceGateOutcomeLedger.AttemptRoot,
                key.Directory, key.AttemptId + ".attempt.json");
            if (HasStaleMetadata(metadataPath, key.AttemptId)) Invalidate(key);
        }
    }

    // The ledger row is written first. If publishing stale metadata failed, keep the old observation until retry.
    private static bool HasStaleMetadata(string path, string attemptId)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(stream, AcceptanceGateOutcomeLedger.Json) is
                { Outcome: ConductorParallelAcceptanceAttemptOutcome.StaleCandidate } attempt && attempt.AttemptId == attemptId;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    private void Invalidate((string Directory, string AttemptId) key)
    {
        pendingInvalidations.Remove(key);
        invalidated.Add(key);
        observations.Remove(key);
    }

    private void ReadTail()
    {
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { Reset(); return; }
        using (stream)
        {
            var end = stream.Length;
            if (end < offset) Reset();
            if (end == offset) return;

            stream.Position = offset;
            Span<byte> buffer = stackalloc byte[4096];
            using var line = new MemoryStream();
            while (stream.Position < end)
            {
                var count = stream.Read(buffer[..(int)Math.Min(buffer.Length, end - stream.Position)]);
                if (count == 0) break;
                var unread = buffer[..count];
                int newline;
                while ((newline = unread.IndexOf((byte)'\n')) >= 0)
                {
                    line.Write(unread[..newline]);
                    Consume(line.GetBuffer().AsSpan(0, (int)line.Length));
                    line.SetLength(0);
                    unread = unread[(newline + 1)..];
                    offset = stream.Position - unread.Length;
                }
                line.Write(unread);
            }
        }
    }

    private void Consume(ReadOnlySpan<byte> line)
    {
        LinesReadLastRun++;
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(line);
            if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("kind", out var kind) ||
                kind.ValueKind != JsonValueKind.String) return;
            if (kind.ValueEquals(AcceptanceGateOutcomeLedger.BackfillKind))
            {
                backfilled = true;
                return;
            }
            if (!ReadName(json, "directory", out var directory) || !ReadName(json, "attemptId", out var attemptId)) return;
            var key = (directory, attemptId);
            if (kind.ValueEquals(AcceptanceGateOutcomeLedger.InvalidationKind))
            {
                if (json.TryGetProperty("requiresStaleMetadata", out var required) && required.ValueKind == JsonValueKind.True)
                    pendingInvalidations.Add(key);
                else
                    Invalidate(key);
            }
            else if (kind.ValueEquals(AcceptanceGateOutcomeLedger.GateKind) && !invalidated.Contains(key) &&
                json.Deserialize<AcceptanceGateOutcomeObservation>(AcceptanceGateOutcomeLedger.Json) is
                    { Outcome: "Passed" or "Failed" } row)
            {
                var gate = ToObservation(orchestratorDirectory, row);
                var withoutMetadata = row.HasSoloMetadata
                    ? gate with { StartedAt = gate.RecordedAt, LaunchMainCommit = row.OwnerFallbackLaunchMainCommit } : null;
                observations.TryAdd(key, new(gate, withoutMetadata));
            }
        }
        catch (JsonException) { /* Ignore malformed complete rows and continue from the next line. */ }
    }

    private static bool ReadName(JsonElement json, string property, out string name)
    {
        name = json.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : "";
        return name.Length > 0 && name is not ("." or "..") && name == Path.GetFileName(name);
    }

    private static OutcomeGateObservation ToObservation(string directory, AcceptanceGateOutcomeObservation row) =>
        new(Path.Combine(directory, AcceptanceGateOutcomeLedger.AttemptRoot, row.Directory, row.AttemptId),
            row.RecordedAt, row.StartedAt, row.LaunchMainCommit, row.Mode, row.FullGateReason,
            row.ExecutedClassCount, row.SelectionMilliseconds);

    private sealed record RetainedObservation(OutcomeGateObservation Gate, OutcomeGateObservation? WithoutMetadata);

    private void Reset()
    {
        offset = 0;
        backfilled = false;
        observations.Clear();
        invalidated.Clear();
        pendingInvalidations.Clear();
    }
}
