using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class CostGoalOutcomeGateLedgerReader(string orchestratorDirectory)
{
    private const string LegacyPendingKind = "legacy-pending";
    private const string LegacyClosedKind = "legacy-closed";
    private readonly string path = Path.GetFullPath(Path.Combine(orchestratorDirectory, AcceptanceGateOutcomeLedger.FileName));
    private readonly Dictionary<(string Directory, string AttemptId), RetainedObservation> observations = new();
    private readonly HashSet<(string Directory, string AttemptId)> invalidated = [];
    private readonly HashSet<(string Directory, string AttemptId)> pendingInvalidations = [];
    private readonly Dictionary<(string Directory, string AttemptId), CostGoalOutcomeMetric.GatePlanPath> legacyGates = new();
    private long offset;
    private bool backfilled;

    internal int GateFileReads { get; private set; }
    internal int LinesReadLastRun { get; private set; }
    internal Action? AfterPublicationRecoveryForTests { get; set; }

    internal OutcomeGateObservation[] Read(DateTimeOffset recordedBefore)
    {
        LinesReadLastRun = 0;
        if (!backfilled)
        {
            ReadTail();
            if (!backfilled) Backfill();
        }
        var recoveredThrough = offset;
        AcceptanceGateOutcomeLedger.RecoverPublications(path, ReadReadySoloObservation);
        AfterPublicationRecoveryForTests?.Invoke();
        ReadTail();
        if (offset != recoveredThrough || legacyGates.Count != 0)
        {
            // A later append can follow a deferred publication made visible after the recovery snapshot.
            // Reconcile that tail with recovery while subsequent appends are blocked.
            AcceptanceGateOutcomeLedger.WithBackfillLock(path, () =>
            {
                AcceptanceGateOutcomeLedger.RecoverPublications(path, ReadReadySoloObservation);
                ReadTail();
                RecoverLegacyGates();
            });
        }
        if (!backfilled) Backfill();
        var unreadableInvalidations = ConfirmInvalidations();

        return observations.Where(pair => !unreadableInvalidations.Contains(pair.Key))
            .Select(pair => pair.Value)
            .Where(entry => entry.Gate.RecordedAt < recordedBefore && HasRetainedEvidence(entry.Gate.Id))
            .Select(entry => entry.WithoutMetadata is { } fallback && !File.Exists(entry.Gate.Id + ".attempt.json")
                ? fallback : entry.Gate)
            .OrderByDescending(observation => observation.RecordedAt)
            .ThenBy(observation => observation.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static (bool Ready, AcceptanceGateOutcomeObservation? Observation) ReadReadySoloObservation(
        string metadataPath, AcceptanceGateOutcomeObservation row)
    {
        // The wrapper or PersistResultCandidate can complete the attempt after the result becomes visible.
        try
        {
            var attempt = ReadMetadata(metadataPath) ?? throw new JsonException("Invalid solo gate metadata for pending observation.");
            if (attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.Running) return (false, null);
            return attempt.RecordedGateProvenance(row.AttemptId) is { } solo
                ? (true, AcceptanceGateOutcomeLedger.WithSoloProvenance(row, solo)) : (true, null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return (true, row); }
    }

    // Retention can remove one attempt's files while leaving other attempts in its directory.
    private static bool HasRetainedEvidence(string prefix) =>
        File.Exists(prefix + AcceptanceGateLanePlan.ArtifactSuffix) && File.Exists(prefix + ".result.json");

    private void Backfill() => AcceptanceGateOutcomeLedger.WithBackfillLock(path, () =>
    {
        ReadTail();
        if (backfilled) return;

        var lines = new List<string>();
        foreach (var gate in CostGoalOutcomeMetric.DiscoverGatePlans(orchestratorDirectory, DateTimeOffset.MaxValue,
            requireReadable: true, includeRunningSolo: true))
        {
            var directory = Path.GetFileName(Path.GetDirectoryName(gate.Prefix))!;
            var key = (directory, gate.AttemptId);
            if (observations.ContainsKey(key) || invalidated.Contains(key) || legacyGates.ContainsKey(key)) continue;
            var (waiting, row) = CostGoalOutcomeMetric.ReadLegacyGate(gate, () => GateFileReads++);
            if (waiting) lines.Add(SerializeLegacyState(LegacyPendingKind, key));
            else if (row is not null) lines.Add(JsonSerializer.Serialize(row, AcceptanceGateOutcomeLedger.Json));
        }
        lines.Add(JsonSerializer.Serialize(new { Kind = AcceptanceGateOutcomeLedger.BackfillKind }, AcceptanceGateOutcomeLedger.Json));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AcceptanceGateOutcomeLedger.AppendCompleteLines(path, lines);
        ReadTail();
    });

    // Only the specific unfinished attempts seen at backfill can require migration reads after the marker.
    private void RecoverLegacyGates()
    {
        if (legacyGates.Count == 0) return;
        var lines = new List<string>();
        foreach (var (key, gate) in legacyGates)
        {
            var (waiting, row) = CostGoalOutcomeMetric.ReadLegacyGate(gate, () => GateFileReads++);
            if (!waiting)
                lines.Add(row is not null ? JsonSerializer.Serialize(row, AcceptanceGateOutcomeLedger.Json)
                    : SerializeLegacyState(LegacyClosedKind, key));
        }
        if (lines.Count == 0) return;
        AcceptanceGateOutcomeLedger.AppendCompleteLines(path, lines);
        ReadTail();
    }

    private static string SerializeLegacyState(string kind, (string Directory, string AttemptId) key) =>
        JsonSerializer.Serialize(new { Kind = kind, key.Directory, key.AttemptId }, AcceptanceGateOutcomeLedger.Json);

    private HashSet<(string Directory, string AttemptId)> ConfirmInvalidations()
    {
        var unreadable = new HashSet<(string, string)>();
        foreach (var key in pendingInvalidations.ToArray())
        {
            var metadataPath = Path.Combine(orchestratorDirectory, AcceptanceGateOutcomeLedger.AttemptRoot,
                key.Directory, key.AttemptId + ".attempt.json");
            var stale = HasStaleMetadata(metadataPath, key.AttemptId);
            if (stale is true) Invalidate(key);
            else if (stale is null) unreadable.Add(key);
        }
        return unreadable;
    }

    // The ledger row is written first. If publishing stale metadata failed, keep the old observation until retry.
    private static bool? HasStaleMetadata(string path, string attemptId)
    {
        try
        {
            return ReadMetadata(path) is
                { Outcome: ConductorParallelAcceptanceAttemptOutcome.StaleCandidate } attempt && attempt.AttemptId == attemptId;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static ConductorParallelAcceptanceAttempt? ReadMetadata(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(stream, AcceptanceGateOutcomeLedger.Json);
    }

    private void Invalidate((string Directory, string AttemptId) key)
    {
        pendingInvalidations.Remove(key);
        invalidated.Add(key);
        observations.Remove(key);
        legacyGates.Remove(key);
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
            if (kind.ValueEquals(LegacyPendingKind) && !observations.ContainsKey(key) && !invalidated.Contains(key) &&
                AcceptanceGateOutcomeLedger.RecordedAt(attemptId) is { } recordedAt)
            {
                var prefix = Path.Combine(orchestratorDirectory, AcceptanceGateOutcomeLedger.AttemptRoot, directory, attemptId);
                legacyGates.TryAdd(key, new(prefix, attemptId, recordedAt));
                return;
            }
            if (kind.ValueEquals(LegacyClosedKind))
            {
                legacyGates.Remove(key);
                return;
            }
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
                legacyGates.Remove(key);
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
        legacyGates.Clear();
    }
}
