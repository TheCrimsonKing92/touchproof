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
    internal const string PendingFileName = "cost-goal-outcome-gates.pending.jsonl";
    internal const string PendingDirectoryName = "cost-goal-outcome-gate-publications";
    internal const string FallbackDirectoryName = "cost-goal-outcome-gate-publications-fallback";
    internal const string IndependentPendingDirectoryName = "cost-goal-outcome-gate-publications-independent";
    internal const string AttemptRoot = "acceptance-gate-attempts";
    internal const string GateKind = "gate";
    internal const string InvalidationKind = "invalidation";
    internal const string BackfillKind = "backfill-complete";
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

    internal static AcceptanceSelectedExecutionRecord? ParseSelection(ReadOnlySpan<char> json, string attemptId) =>
        GetValidatedSelection(JsonSerializer.Deserialize<AcceptanceGateLanePlan>(json, AcceptanceGateLanePlan.JsonOptions), attemptId);

    internal static AcceptanceSelectedExecutionRecord? GetValidatedSelection(AcceptanceGateLanePlan? plan, string attemptId)
    {
        // Match OwnerConsoleGateLanePlanReader, which validates the historical lane plans.
        if (plan is null || plan.SchemaVersion != AcceptanceGateLanePlan.CurrentSchemaVersion ||
            plan.AttemptId != attemptId || plan.Scheduled is null || plan.Skipped is null ||
            plan.Scheduled.Any(string.IsNullOrWhiteSpace) ||
            plan.Skipped.Any(row => row is null || string.IsNullOrWhiteSpace(row.Name) ||
                string.IsNullOrWhiteSpace(row.Reason))) return null;
        var names = plan.Scheduled.Concat(plan.Skipped.Select(row => row.Name)).ToArray();
        return names.Distinct(StringComparer.Ordinal).Count() == names.Length ? plan.SelectedExecution : null;
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

    // Record recovery intent before the result is visible. This offset includes every row backfill could add for it.
    internal static PreparedPublication? PreparePublication(string prefix, ConductorParallelAcceptanceRunArtifact result,
        AcceptanceSelectedExecutionRecord? selection, SoloGateOutcomeProvenance? solo = null)
    {
        if (selection is null || !IsRecordedGate(result) || RecordedAt(Path.GetFileName(prefix)) is not { } recordedAt ||
            LedgerPath(prefix) is not { } path || !File.Exists(prefix + AcceptanceGateLanePlan.ArtifactSuffix)) return null;
        var row = CreateObservation(prefix, recordedAt, result, selection, solo);
        var file = new FileInfo(path);
        var offset = file.Exists ? file.Length : 0;
        var pending = new PendingPublication(offset, row, RequiresResult: true);
        var line = JsonSerializer.Serialize(pending, Json);
        (string Path, bool Shared)[] intentTargets =
        [
            (PendingDirectoryPath(path, PendingDirectoryName), false),
            (LegacyPendingPath(path), true),
            (PendingDirectoryPath(path, FallbackDirectoryName), false),
            (AttemptRootPendingPath(path), true),
            (PendingDirectoryPath(path, IndependentPendingDirectoryName), false)
        ];
        Exception? lastError = null;
        foreach (var (target, shared) in intentTargets)
        {
            try { return shared ? WriteSharedIntent(target) : WriteSeparateIntent(target); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { lastError = ex; }
        }
        System.Diagnostics.Trace.TraceWarning($"Acceptance gate outcome intent unavailable for {prefix}: {lastError?.Message}");
        return new(path, null, pending);

        PreparedPublication WriteSeparateIntent(string directoryPath)
        {
            var directory = Directory.CreateDirectory(directoryPath).FullName;
            var pendingPath = Path.Combine(directory, $"{Guid.NewGuid():N}.jsonl");
            WritePending(pendingPath, [line]);
            return new(path, pendingPath, pending);
        }

        PreparedPublication WriteSharedIntent(string pendingPath)
        {
            WithBackfillLock(path, () => WritePending(pendingPath, ReadPending(pendingPath).Append(line)));
            return new(path, pendingPath, pending, SharedPending: true);
        }
    }

    internal static void Publish(PreparedPublication? publication, SoloGateOutcomeProvenance? solo = null)
    {
        if (publication is null) return;
        var (path, pendingPath, pending, sharedPending) = publication;
        try
        {
            WithBackfillLock(path, () =>
            {
                AppendObservation(path, pending.Offset, WithSoloProvenance(pending.Observation, solo));
                if (!sharedPending && pendingPath is not null) RemovePending(pendingPath);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            System.Diagnostics.Trace.TraceWarning($"Acceptance gate outcome ledger unavailable for {pending.Observation.AttemptId}: {ex.Message}");
        }
    }

    private static void WritePending(string pendingPath, IEnumerable<string> lines)
    {
        var temporaryPath = $"{pendingPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                foreach (var line in lines)
                    stream.Write(Encoding.UTF8.GetBytes(line + Environment.NewLine));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, pendingPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    internal static void RecoverPublications(string path,
        Func<string, AcceptanceGateOutcomeObservation, (bool Ready, AcceptanceGateOutcomeObservation? Observation)> readSoloObservation)
    {
        var (pendingPaths, primaryError) = ListPending(PendingDirectoryName);
        var legacyPath = LegacyPendingPath(path);
        var (fallbackPaths, fallbackError) = ListPending(FallbackDirectoryName);
        var (independentPaths, independentError) = ListPending(IndependentPendingDirectoryName);
        if (EvidenceExists(legacyPath)) fallbackPaths = [.. fallbackPaths, legacyPath];
        var attemptRootPendingPath = AttemptRootPendingPath(path);
        Exception? attemptRootPendingError = null;
        try
        {
            if (EvidenceExists(attemptRootPendingPath)) fallbackPaths = [.. fallbackPaths, attemptRootPendingPath];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            attemptRootPendingError = ex;
        }

        if (pendingPaths.Length != 0 || fallbackPaths.Length != 0 || independentPaths.Length != 0)
            WithBackfillLock(path, () => ReplayPending(pendingPaths.Concat(independentPaths).Concat(fallbackPaths)));

        // Replay reachable intents, then fail the read if any recovery path could still hide observations.
        if ((primaryError ?? fallbackError ?? attemptRootPendingError ?? independentError) is { } error)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);

        (string[] Paths, Exception? Error) ListPending(string directoryName)
        {
            var directory = PendingDirectoryPath(path, directoryName);
            try { return (Directory.GetFiles(directory, "*.jsonl"), null); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ([], ex is DirectoryNotFoundException || File.Exists(directory) ? null : ex);
            }
        }

        void ReplayPending(IEnumerable<string> paths)
        {
            foreach (var pendingPath in paths)
            {
                var allReady = true;
                foreach (var line in ReadPending(pendingPath))
                {
                    if (JsonSerializer.Deserialize<PendingPublication>(line, Json) is not
                        { Offset: >= 0, Observation: not null } pending)
                        throw new JsonException("Invalid pending gate observation.");
                    var (ready, observation) = ReadReadyObservation(path, pending, readSoloObservation);
                    if (observation is not null) AppendObservation(path, pending.Offset, observation);
                    allReady &= ready;
                }
                if (allReady) RemovePending(pendingPath);
            }
        }
    }

    private static IEnumerable<string> ReadPending(string path)
    {
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (FileNotFoundException) { yield break; }
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line) yield return line;
    }

    private static (bool Ready, AcceptanceGateOutcomeObservation? Observation) ReadReadyObservation(
        string path, PendingPublication pending,
        Func<string, AcceptanceGateOutcomeObservation, (bool Ready, AcceptanceGateOutcomeObservation? Observation)> readSoloObservation)
    {
        var row = pending.Observation;
        if (!pending.RequiresResult) return (true, row);

        var prefix = Path.Combine(Path.GetDirectoryName(path)!, AttemptRoot, row.Directory, row.AttemptId);
        if (!EvidenceExists(prefix + ".result.json")) return (false, null);
        return row.HasSoloMetadata ? readSoloObservation(prefix + ".attempt.json", row) : (true, row);
    }

    internal static AcceptanceGateOutcomeObservation WithSoloProvenance(AcceptanceGateOutcomeObservation row,
        SoloGateOutcomeProvenance? solo) => solo is null ? row : row with
        {
            StartedAt = solo.StartedAt, LaunchMainCommit = solo.LaunchMainCommit, Outcome = solo.Outcome
        };

    internal static bool EvidenceExists(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.Directory) == 0; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }

    private static void RemovePending(string path)
    {
        // A retry after an interrupted drain checks for each row before appending it again.
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"Acceptance gate outcome publication recovery deferred for {path}: {ex.Message}");
        }
    }

    internal static void Invalidate(string prefix)
    {
        if (LedgerPath(prefix) is not { } path) return;
        var line = JsonSerializer.Serialize(new
        {
            Kind = InvalidationKind,
            AttemptId = Path.GetFileName(prefix),
            Directory = Path.GetFileName(Path.GetDirectoryName(prefix)),
            RequiresStaleMetadata = true
        }, Json);
        WithBackfillLock(path, () => AppendCompleteLines(path, [line]));
    }

    // Call under WithBackfillLock. Close an interrupted record as malformed, keeping all prior byte offsets valid.
    internal static void AppendCompleteLines(string path, IEnumerable<string> lines)
    {
        bool interrupted;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            interrupted = !EndsAtLineBoundary(stream);
        }
        catch (FileNotFoundException) { interrupted = false; }
        SharedJsonlFile.AppendLines(path, interrupted ? lines.Prepend("!") : lines);
    }

    private static bool EndsAtLineBoundary(FileStream stream)
    {
        if (stream.Length == 0) return true;
        stream.Position = stream.Length - 1;
        return stream.ReadByte() == '\n';
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

    private static string PendingDirectoryPath(string path, string directoryName) => Path.Combine(Path.GetDirectoryName(path)!, directoryName);

    private static string LegacyPendingPath(string path) => Path.Combine(Path.GetDirectoryName(path)!, PendingFileName);

    // Replay can find the shared journal without listing any historical attempt directories.
    private static string AttemptRootPendingPath(string path) =>
        Path.Combine(Path.GetDirectoryName(path)!, AttemptRoot, PendingFileName);

    private static void AppendObservation(string path, long offset, AcceptanceGateOutcomeObservation row)
    {
        if (!HasPublishedObservation(path, offset, row))
            AppendCompleteLines(path, [JsonSerializer.Serialize(row, Json)]);
    }

    internal sealed record PreparedPublication(string LedgerPath, string? PendingPath, PendingPublication Pending,
        bool SharedPending = false);
    internal sealed record PendingPublication(long Offset, AcceptanceGateOutcomeObservation Observation, bool RequiresResult = false);

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
            var tail = reader.ReadToEnd();
            var start = 0;
            int end;
            while ((end = tail.IndexOf('\n', start)) >= 0)
            {
                var line = tail.AsSpan(start, end - start);
                start = end + 1;
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
