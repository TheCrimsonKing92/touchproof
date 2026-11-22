using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class CostGoalOutcomeGateLedgerReader(string orchestratorDirectory)
{
    private readonly string path = Path.GetFullPath(Path.Combine(orchestratorDirectory, AcceptanceGateOutcomeLedger.FileName));
    private readonly Dictionary<(string Directory, string AttemptId), RetainedObservation> observations = new();
    private readonly HashSet<(string Directory, string AttemptId)> invalidated = [];
    private long offset;
    private bool backfilled;

    internal int GateFileReads { get; private set; }
    internal int LinesReadLastRun { get; private set; }

    internal OutcomeGateObservation[] Read(DateTimeOffset recordedBefore)
    {
        LinesReadLastRun = 0;
        ReadTail();
        if (!backfilled) Backfill();

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

    private void Backfill()
    {
        var lockPath = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lockPath)));
        using var mutex = new Mutex(false, $"Mcg.CostGoalOutcomeGateBackfill.{hash}");
        try { mutex.WaitOne(); }
        catch (AbandonedMutexException) { /* Read the durable ledger after taking ownership. */ }
        try
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
        }
        finally { mutex.ReleaseMutex(); }
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
                invalidated.Add(key);
                observations.Remove(key);
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
    }
}
