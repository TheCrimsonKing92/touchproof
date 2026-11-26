using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Transactional history storage. Unmarked tasks retain inline authority and mirror equality checks.
/// GoalTimelineStore owns the goal-level Timeline reader contract and bypass-reader inventory.
/// A task's HistoryInTable marker is {"Verification":count,"Dispatch":count}: each count is a
/// non-negative integer, or null for a null list. Absent/null markers mean unmarked. Marked tasks
/// ignore inline lists and require exactly the stated rows at contiguous zero-based sequences.
/// This build writes marked documents; only history rows contain the two lists.
///
/// Latest-record reader contract (step 3a): the marker also accepts optional boolean
/// LatestVerificationInTable and LatestDispatchInTable independently. Absent/false retains inline
/// authority, even if the newest row differs. True ignores inline and takes the highest sequence;
/// its kind must have a positive count and usable rows. Omitted and null inline values both work.
/// The read-side HistoryMarker is separate from serialized HistoryCounts. This build (step 3b)
/// writes each flag only when the non-null latest, serialized with the history options, equals
/// the highest-sequence row's exact entry_json prepared for this save (ordinal string equality).
/// Otherwise latest stays inline and its flag is absent. Caller snapshots are never modified.
/// Step 3b may be adopted only after a build containing step 3a has been adopted AND one later
/// build has been adopted after it. Its writer must first prove latest equals the newest row;
/// TaskSpec can match latest dispatch by timestamp rather than list position.
/// Under that adoption rule, ConductorContinuitySupervisor.RunAsync's retained currentBuild
/// revert target must contain step 3a. A build older than step 3a loads flagged latest records
/// as null and saves those nulls inline, so it cannot be the revert target.
///
/// Latest-record reader inventory, established by searches over src and scripts (excluding bin,
/// obj, .scratch and prototype state): LastVerification|LastDispatch; Deserialize.*GoalSnapshot;
/// Deserialize.*TaskSnapshot; snapshot_json; json_extract. Hydrating paths are LoadFromConnectionAsync
/// (LoadAsync, LoadGoalsAsync, LoadStatusGoalsAsync, LoadBoardSnapshotAsync), LoadGoalAsync,
/// both prefix lookups, LoadGoalSnapshotAndVersionAsync (snapshot transaction/merge save), and
/// LoadGoalStateAndVersionAsync (state transactions). Selected/archive loads resolve original markers.
/// Bypass readers and handling (all source entries under Persistence):
/// - AttentionReads and ProviderHoldSeedEvidenceReader: the sparse readers supply marked latest elements,
///   decoded with each reader's existing sparse shape; cached provider evidence stores resolved values.
/// - ProviderConnections: ReadMetadataAsync shares marker validation and latest authority while
///   projecting only attribution/status fields, excluding invocation output and command text.
/// - ReadTerminalMetadata: ReadLatestDispatchAsync resolves commit evidence; metadata-only hot stubs
///   already carry ResultCommit. ListConductLoopGoalMetadataAsync and AcceptanceQueueReads use
///   LatestFieldSql for ResultCommit and CompletedAt respectively, with an inline-only legacy-table fallback.
/// - GoalSnapshotArchive: hydrate before computing its metadata stub's ResultCommit; preserve original
///   archived text and rows. Invalid marked histories remain hot, where loading reports the defect.
/// - BacklogReads: the inline LastVerification shape probe permits absence because every marked task
///   is then validated by RequiresReconstructionAsync; suspect domain replay uses HydrateAsync.
/// - EpicReads.ReadEpicGoalsAsync: consumers use status/timeline only; Goal.FromSnapshot restores
///   status from the stored status, independently of missing latest records. No latest-value consumer.
/// - TryReadGoalSnapshot/quarantine listing, similarity projection diagnostic fallback, and the
///   operator utility diagnostics/status verb consume no latest value; missing values are correct.
/// - OrchestratorSqliteTools requeue: remove both latest flags alongside the cleared latest fields;
///   keep counts, unrelated marker members and all historical rows. This is the sole write-side exception.
/// RetryAdmissionReservationStore, RetryAdmissionReplayBaseline and TaskGraphRepairAdmission receive
/// already hydrated snapshots/domain goals, rather than reading stored latest fields themselves.
///
/// Past-entry reader inventory (all under Persistence): SqliteOrchestratorStateRepository's
/// LoadFromConnectionAsync (full/selected kernel reconstruction), LoadGoalAsync (single goal),
/// FindGoalSnapshotsByIdPrefixAsync and FindTaskSnapshotsByIdPrefixAsync (cited historical evidence),
/// LoadGoalSnapshotAndVersionAsync and LoadGoalStateAndVersionAsync (CAS baselines and tick merge)
/// use HydrateAsync. AttentionReads uses ReadAsync for provider usage evidence;
/// ProviderHoldSeedEvidenceReader uses its sparse form for provider hold seeds. BacklogReads uses
/// RequiresReconstructionAsync for similarity validation and HydrateAsync before suspect replay.
/// EpicReads consumers use only timelines; archive stubs and repository workspace/commit reads use
/// latest dispatch/timeline only, and deserialization checks consume no past entries. Any future
/// history consumer at those seams must use this accessor.
///
/// Step 2b (writing marked documents) may be adopted only after a build containing step 2a has
/// been adopted AND at least one later build has been adopted after it. Activation revert must
/// already fall back to a build containing this reader: App/Orchestration/ConductorContinuitySupervisor
/// RunAsync retains currentBuild on the reverted branch, relaunches it at the launch boundary,
/// and advances currentBuild only after adoption or deliberate stop. Step 1 cannot safely read
/// stripped histories and would remove their table rows on save.
/// Thus a build older than step 2a cannot be the revert target.
///
/// Writer inventory: UpsertGoalRowAsync is called by SaveGoalSnapshotsAsync, WriteSnapshotAsync,
/// TryCasWriteGoalRowAsync, and WriteGoalCreationChangesAsync (GoalCreation). All hold one write transaction
/// spanning the document and rows; all four use this same upsert's prepared document/row write.
/// SaveGoalSnapshotsWithMergeAsync reaches it through TryCasWriteGoalRowAsync. Conductor ticks
/// are covered by the adoption rule above.
/// Offline intent apply (the merge path) and every other goal-saving command line verb use
/// mcg-orchestrator.cmd: the adopted build or a HEAD-freshness-checked checkout build.
/// mcg-orchestrator.sh has only its file-time staleness check.
/// Detached __dispatch-run hosts also save goals: App/Program.cs:23 constructs WorkerBuildRequestHost;
/// its Store calls (:91, :289) reach WorkerBuildInvocationOperations.cs:50-65, TransactGoalStateAsync,
/// TryCasWriteGoalRowAsync and this repository's goal row upsert. They run the launching process's
/// build: Processes/BackgroundDispatchRunner.DispatchHelpers.cs:9-10 resolves AppContext.BaseDirectory's
/// App dll, BackgroundDispatchRunner.cs:368-369 passes it to DispatchHostStartInfoFactory.cs:16-17
/// as dotnet exec's explicit assembly, with no independent older-build selection.
/// Hosts outlive their launcher. DispatchProcessHost.cs:37, :45, :2082-2105 bounds worker runtime
/// by a 60-minute default cap, checked every 15 seconds (MCG_DISPATCH_MAX_RUNTIME_MIN can override it);
/// preparation and final channel/output cleanup are outside that worker runtime bound.
/// Before the first latest-flagged save, the operator must verify no dispatch host older than step 3a
/// adoption remains alive; let those hosts finish or stop them first. Such an old host loads marked
/// latest records as null and saves them inline as null (pre-2a also loses history rows).
/// StateStoreFormatFence checks every repository write acquisition and refuses a recorded format
/// level above the build's level. Level 1 permits history-in-table markers in goal documents.
/// GoalSnapshotArchive copies the marked text,
/// retaining rows; its task-less hot stub cannot be saved. Recompression preserves that text;
/// StateDatabaseOfflineConversion copies the whole database, including history rows.
/// The set-goal-status and requeue-task verbs in scripts/OrchestratorSqliteTools/Program.cs need
/// no build guarantee: both edit an untyped JSON tree, remove no task, and retain history counts,
/// lists and rows. Requeue removes the latest flags described above and clears latest execution,
/// verification, dispatch, process and retry fields, changes status and appends a timeline event only.
/// Protection begins with the first build carrying StateStoreFormatFence. Earlier builds already
/// on disk never check it: running their application dll directly or their checkout/worktree's
/// launcher against a latest-flagged store can still lose flagged latest records on save
/// (pre-2a also loses history rows). The adoption rule remains the protection against those builds.
/// Read-only consoles opened before step 3a show null latest records until restarted.
/// Interactive CLI writers inherit the launcher's build guarantee, checked only at launch.
/// Those started before step 3a must restart before latest-flagged writes begin: their old build
/// loads flagged latest records as null and saves them inline as null.
/// Writer search (src and scripts, excluding generated trees): UpsertGoalRowAsync, INSERT INTO goals,
/// UPDATE goals, INSERT INTO goal_snapshot_archive, UPDATE goal_snapshot_archive, VACUUM INTO.
/// A write that changes the goal document, history or Timeline content must bump the goal row's
/// version in the same transaction. The conductor can reuse a hydrated goal while that version
/// is unchanged. An archive swap may preserve the version because it preserves the content.
/// </summary>
internal sealed partial class GoalTaskHistoryStore
{
    private readonly JsonSerializerOptions options;
    private readonly GoalTaskBuildHistoryStore _buildHistory;

    internal GoalTaskHistoryStore(JsonSerializerOptions options)
    {
        this.options = options;
        _buildHistory = new(options);
    }

    internal const string VerificationProperty = "VerificationHistory";
    internal const string DispatchProperty = "DispatchHistory";
    internal const string MarkerProperty = "HistoryInTable";

    private static readonly ConditionalWeakTable<JsonSerializerOptions, DocumentOptions> SharedDocumentOptions = new();
    private static readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> SharedReadOptions = new();

    [ThreadStatic]
    private static DocumentMarkers? _activeReadMarkers;

    [ThreadStatic]
    private static IReadOnlyDictionary<TaskSnapshot, HistoryCounts>? _activeCounts;

    [ThreadStatic]
    private static int? _activeTimelineCount;

    private static HistoryCounts ActiveCounts(TaskSnapshot task) =>
        (_activeCounts ?? throw new InvalidOperationException("No active goal document serialization."))[task];

    // Test-only observation of task metadata construction, isolated by source options identity.
    internal int TaskShapeModifierRunCount => SharedDocumentOptions.TryGetValue(options, out var shared)
        ? shared.TaskShapeModifierRunCount : 0;

    private sealed class DocumentOptions
    {
        private int _taskShapeModifierRunCount;
        internal int TaskShapeModifierRunCount => Volatile.Read(ref _taskShapeModifierRunCount);
        internal JsonSerializerOptions Options { get; }

        // Customize only the persisted task shape; callers and row writes keep the original lists.
        internal DocumentOptions(JsonSerializerOptions source)
        {
            var resolver = new DefaultJsonTypeInfoResolver();
            resolver.Modifiers.Add(type =>
            {
                if (type.Type == typeof(GoalSnapshot))
                {
                    var timeline = type.Properties.Single(property => property.AttributeProvider is
                        System.Reflection.PropertyInfo { Name: nameof(GoalSnapshot.Timeline) });
                    timeline.ShouldSerialize = (_, _) => _activeTimelineCount is null;
                    var timelineMarker = type.CreateJsonPropertyInfo(typeof(int?), GoalTimelineStore.MarkerProperty);
                    timelineMarker.Get = _ => _activeTimelineCount;
                    timelineMarker.ShouldSerialize = (_, _) => _activeTimelineCount is not null;
                    type.Properties.Add(timelineMarker);
                    return;
                }
                if (type.Type != typeof(TaskSnapshot)) return;
                Interlocked.Increment(ref _taskShapeModifierRunCount);
                foreach (var property in type.Properties.Where(property =>
                    property.AttributeProvider is System.Reflection.PropertyInfo
                        { Name: VerificationProperty or DispatchProperty }).ToArray())
                    type.Properties.Remove(property);
                foreach (var property in type.Properties)
                {
                    if (property.AttributeProvider is not System.Reflection.PropertyInfo member) continue;
                    if (member.Name == nameof(TaskSnapshot.LastVerification))
                        property.ShouldSerialize = (value, _) => !ActiveCounts((TaskSnapshot)value).LatestVerificationInTable;
                    else if (member.Name == nameof(TaskSnapshot.LastDispatch))
                        property.ShouldSerialize = (value, _) => !ActiveCounts((TaskSnapshot)value).LatestDispatchInTable;
                }
                var marker = type.CreateJsonPropertyInfo(typeof(HistoryCounts), MarkerProperty);
                marker.Get = value => ActiveCounts((TaskSnapshot)value);
                type.Properties.Add(marker);
            });
            Options = new(source) { TypeInfoResolver = resolver };
        }
    }

    internal string SerializeDocument(GoalSnapshot goal) => PrepareWrite(goal).Document;

    // One per-save preparation supplies the document decisions and all task history row payloads.
    // Options, resolver and task metadata are shared lazily per source options object. Reference-keyed
    // counts belong to this save, scoped to its synchronous document write on this thread. No output is kept.
    internal PreparedWrite PrepareWrite(GoalSnapshot goal, int? timelineInTable = null,
        GoalSnapshotByteAccounting.Measurement? measurement = null)
    {
        var buildHistory = _buildHistory.PrepareWrite(goal);
        var projectedGoal = buildHistory.ProjectedGoal;
        var rows = new List<HistoryWriteRow>();
        var counts = new Dictionary<TaskSnapshot, HistoryCounts>(ReferenceEqualityComparer.Instance);
        foreach (var task in projectedGoal.Tasks)
        {
            string? AddRows<T>(IReadOnlyList<T>? entries, string kind)
            {
                string? newest = null;
                if (entries is not null)
                    for (var index = 0; index < entries.Count; index++)
                    {
                        newest = JsonSerializer.Serialize(entries[index], options);
                        rows.Add(new(task.Id, kind, index, newest));
                    }
                return newest;
            }
            bool Matches<T>(T? latest, string? newest) where T : class =>
                latest is not null && newest is not null &&
                string.Equals(JsonSerializer.Serialize(latest, options), newest, StringComparison.Ordinal);

            var verification = AddRows(task.VerificationHistory, "verification");
            var dispatch = AddRows(task.DispatchHistory, "dispatch");
            counts[task] = new(task.VerificationHistory?.Count, task.DispatchHistory?.Count,
                Matches(task.LastVerification, verification), Matches(task.LastDispatch, dispatch));
        }
        var documentOptions = SharedDocumentOptions.GetValue(options, static source => new(source)).Options;
        var previousCounts = _activeCounts;
        var previousTimelineCount = _activeTimelineCount;
        string document;
        _activeCounts = counts;
        _activeTimelineCount = timelineInTable;
        try
        {
            if (measurement is null)
            {
                document = GoalSnapshotByteAccounting.Serialize(projectedGoal, documentOptions);
            }
            else
            {
                measurement.Start();
                document = JsonSerializer.Serialize(projectedGoal, documentOptions);
            }
        }
        finally
        {
            _activeCounts = previousCounts;
            _activeTimelineCount = previousTimelineCount;
        }
        return new(goal.Id, document, JsonSerializer.Serialize(rows, options), buildHistory);
    }

    internal sealed record PreparedWrite(string GoalId, string Document, string RowsJson,
        GoalTaskBuildHistoryStore.PreparedWrite BuildHistory);

    [StoreSchemaSource("orchestrator-state", "src/Mcg.AgentOrchestrator.Execution/Persistence/GoalTaskHistoryStore.cs")]
    internal static void CreateSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS goal_task_history (
                goal_id TEXT NOT NULL,
                task_id TEXT NOT NULL,
                kind TEXT NOT NULL CHECK (kind IN ('verification', 'dispatch')),
                sequence INTEGER NOT NULL CHECK (sequence >= 0),
                entry_json TEXT NOT NULL,
                PRIMARY KEY (goal_id, task_id, kind, sequence)
            )
            """;
        command.ExecuteNonQuery();
    }

    // All callers already hold the goal write transaction. Never begin or commit a separate transaction here.
    internal async Task<bool> WriteAsync(SqliteConnection connection, PreparedWrite prepared, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CreateSchema(connection); // Existing stores do not rerun migration 1.
        // Keep entry JSON as a string in the batch so SQLite preserves its exact serialized bytes.
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$goal", prepared.GoalId);
        command.Parameters.AddWithValue("$rows", prepared.RowsJson);
        command.CommandText = """
            INSERT INTO goal_task_history (goal_id, task_id, kind, sequence, entry_json)
            SELECT $goal, json_extract(value, '$.TaskId'), json_extract(value, '$.Kind'),
                json_extract(value, '$.Sequence'), json_extract(value, '$.EntryJson')
            FROM json_each($rows) WHERE true
            ON CONFLICT(goal_id, task_id, kind, sequence) DO UPDATE SET entry_json = excluded.entry_json
            WHERE entry_json != excluded.entry_json
            """;
        var changed = await command.ExecuteNonQueryAsync(token);
        // Cap trims, list replacements and removed tasks all remove keys from the same goal-wide set.
        command.CommandText = """
            DELETE FROM goal_task_history WHERE goal_id = $goal
                AND (task_id, kind, sequence) NOT IN (
                    SELECT json_extract(value, '$.TaskId'), json_extract(value, '$.Kind'),
                        json_extract(value, '$.Sequence') FROM json_each($rows)
                )
            """;
        changed += await command.ExecuteNonQueryAsync(token);
        var buildHistoryChanged = await _buildHistory.WriteAsync(connection, prepared.BuildHistory, token);
        return changed > 0 || buildHistoryChanged;
    }

    private sealed record HistoryWriteRow(string TaskId, string Kind, int Sequence, string EntryJson);

    internal sealed record HistoryEntries(JsonElement[] Entries, bool FromTable, bool IsNull = false);
    // Undefined latest elements mean the caller's inline value remains authoritative.
    internal sealed record TaskHistory(HistoryEntries Verification, HistoryEntries Dispatch,
        JsonElement LatestVerification = default, JsonElement LatestDispatch = default);

    internal Task<TaskHistory> ReadAsync(SqliteConnection? connection, string goalId, JsonElement task,
        CancellationToken token = default) => ReadAsync(connection, goalId, task.GetProperty("Id").GetString()!,
            task.TryGetProperty(VerificationProperty, out var verification) ? verification : default,
            task.TryGetProperty(DispatchProperty, out var dispatch) ? dispatch : default,
            task.TryGetProperty(MarkerProperty, out var marker) ? marker : default, token);

    internal async Task<TaskHistory> ReadAsync(SqliteConnection? connection, string goalId, string taskId,
        JsonElement verification, JsonElement dispatch, JsonElement marker, CancellationToken token = default)
    {
        var counts = ReadMarker(marker, goalId, taskId);
        if (counts is not null && connection is null)
            throw HistoryError(goalId, taskId, "both", "no history connection");
        var rows = connection is null ? null : await ReadRowsAsync(connection, goalId, taskId, token,
            counts is null ? null : taskId);
        if (counts is not null) return StrictHistory(rows!, goalId, taskId, counts);
        return new(
            SelectHistory(rows?.GetValueOrDefault((taskId, "verification")), Elements(verification)),
            SelectHistory(rows?.GetValueOrDefault((taskId, "dispatch")), Elements(dispatch)));
    }

    internal sealed record ProjectedHistory(TaskVerificationSnapshot[] Verifications, TaskDispatchSnapshot[] Dispatches,
        bool LatestVerificationInTable, bool LatestDispatchInTable);

    internal async Task<ProjectedHistory?[]> ReadProjectionAsync(SqliteConnection? connection, string goalId,
        IReadOnlyList<(string Id, JsonElement Marker)> tasks, CancellationToken token)
    {
        var markers = tasks.Select(task => ReadMarker(task.Marker, goalId, task.Id)).ToArray();
        var required = Array.FindIndex(markers, marker => marker is not null);
        var result = new ProjectedHistory?[tasks.Count];
        if (required < 0) return result;
        if (connection is null)
            throw HistoryError(goalId, tasks[required].Id, "both", "no history connection");
        // SQLite stores the JSON as UTF-8. Keep those bytes through the sparse
        // reader so the serializer can skip unneeded receipt text without transcoding it.
        var rows = await ReadRowsAsync(connection, goalId, null, token, tasks[required].Id,
            "CAST(entry_json AS BLOB)", reader => reader.GetFieldValue<byte[]>(3));
        for (var index = required; index < tasks.Count; index++)
        {
            if (markers[index] is not { } marker) continue;
            var taskId = tasks[index].Id;
            var verification = ReadStrictEntries(rows.GetValueOrDefault((taskId, "verification")),
                marker.Verification, goalId, taskId, "verification", json => DeserializeEntry<TaskVerificationSnapshot>(json));
            var dispatch = ReadStrictEntries(rows.GetValueOrDefault((taskId, "dispatch")),
                marker.Dispatch, goalId, taskId, "dispatch", json => DeserializeEntry<TaskDispatchSnapshot>(json));
            result[index] = new(verification, dispatch, marker.LatestVerificationInTable, marker.LatestDispatchInTable);
        }
        return result;

        T DeserializeEntry<T>(byte[] json) where T : class =>
            JsonSerializer.Deserialize<T>(json, options) ?? throw new JsonException("History entry must be an object.");
    }

    private sealed record StoredEntry<T>(long Sequence, T Json);

    // SQL projections are repository-owned, never CLI/provider input. Unmarked documents
    // already supply authoritative inline metadata; no table mirror can override it.
    internal async Task<TaskHistory> ReadMetadataAsync<TVerification, TDispatch>(SqliteConnection connection,
        string goalId, string taskId, JsonElement verification, JsonElement dispatch, JsonElement marker,
        Func<string, string> projectVerification, Func<string, string> projectDispatch, CancellationToken token)
    {
        var counts = ReadMarker(marker, goalId, taskId);
        if (counts is null) return new(new(Elements(verification), false), new(Elements(dispatch), false));
        var rows = new Dictionary<(string TaskId, string Kind), List<StoredEntry<string>>>();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT kind, sequence, CASE kind WHEN 'verification' THEN {projectVerification("entry_json")}
                ELSE {projectDispatch("entry_json")} END
                , json_type(entry_json)
            FROM goal_task_history WHERE goal_id = $goal AND task_id = $task ORDER BY kind, sequence
            """;
        command.Parameters.AddWithValue("$goal", goalId);
        command.Parameters.AddWithValue("$task", taskId);
        using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            if (reader.IsDBNull(3) || reader.GetString(3) != "object")
                throw HistoryError(goalId, taskId, reader.GetString(0), "history metadata entry must be an object");
            var key = (taskId, reader.GetString(0));
            if (!rows.TryGetValue(key, out var list)) rows.Add(key, list = []);
            list.Add(new(reader.GetInt64(1), reader.GetString(2)));
        }
        var verifications = StrictEntries<TVerification>(rows.GetValueOrDefault((taskId, "verification")),
            counts.Verification, goalId, taskId, "verification");
        var dispatches = StrictEntries<TDispatch>(rows.GetValueOrDefault((taskId, "dispatch")),
            counts.Dispatch, goalId, taskId, "dispatch");
        return new(verifications, dispatches, counts.LatestVerificationInTable ? verifications.Entries[^1] : default,
            counts.LatestDispatchInTable ? dispatches.Entries[^1] : default);
    }

    internal static string MetadataSql(string expression, IReadOnlyList<string> fields) =>
        "json_object(" + string.Join(",", fields.Select(field =>
        {
            if (field.Length == 0 || field.Any(c => !char.IsAsciiLetterOrDigit(c)))
                throw new ArgumentException("Metadata fields must be literal identifiers.");
            return $"'{field}', json(({expression}) -> '$.{field}')";
        })) + ")";

    private sealed record HistoryCounts(int? Verification, int? Dispatch,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool LatestVerificationInTable,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool LatestDispatchInTable);
    private sealed record HistoryMarker(int? Verification, int? Dispatch,
        bool LatestVerificationInTable, bool LatestDispatchInTable);

    private static InvalidDataException HistoryError(string goalId, string taskId, string kind, string defect,
        Exception? inner = null) => new($"Goal '{goalId}' task '{taskId}' {kind} history: {defect}.", inner);

    private static HistoryMarker? ReadMarker(JsonElement marker, string goalId, string taskId)
    {
        if (marker.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        int? Count(string name)
        {
            if (marker.ValueKind == JsonValueKind.Object && marker.TryGetProperty(name, out var value))
            {
                if (value.ValueKind == JsonValueKind.Null) return null;
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count) && count >= 0)
                    return count;
            }
            throw HistoryError(goalId, taskId, name, "invalid HistoryInTable count");
        }
        bool Flag(string name)
        {
            if (!marker.TryGetProperty(name, out var value)) return false;
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
            throw HistoryError(goalId, taskId, name, "invalid HistoryInTable flag");
        }
        var verification = Count("Verification");
        var dispatch = Count("Dispatch");
        var latestVerification = Flag("LatestVerificationInTable");
        var latestDispatch = Flag("LatestDispatchInTable");
        if (latestVerification && verification is null or 0)
            throw HistoryError(goalId, taskId, "verification", "latest-in-table requires a positive count");
        if (latestDispatch && dispatch is null or 0)
            throw HistoryError(goalId, taskId, "dispatch", "latest-in-table requires a positive count");
        return new(verification, dispatch, latestVerification, latestDispatch);
    }

    private TaskHistory StrictHistory(Dictionary<(string TaskId, string Kind), List<StoredEntry<string>>> rows,
        string goalId, string taskId, HistoryMarker counts)
    {
        var verification = StrictEntries<TaskVerificationSnapshot>(rows.GetValueOrDefault((taskId, "verification")),
            counts.Verification, goalId, taskId, "verification");
        var dispatch = StrictEntries<TaskDispatchSnapshot>(rows.GetValueOrDefault((taskId, "dispatch")),
            counts.Dispatch, goalId, taskId, "dispatch");
        return new(verification, dispatch,
            counts.LatestVerificationInTable ? verification.Entries[^1] : default,
            counts.LatestDispatchInTable ? dispatch.Entries[^1] : default);
    }

    // Metadata readers need no history work for legacy/inline-authoritative tasks.
    internal async Task<JsonElement> ReadLatestDispatchAsync(SqliteConnection connection, string goalId,
        JsonElement task, CancellationToken token = default)
    {
        if (task.TryGetProperty(MarkerProperty, out var marker) &&
            ReadMarker(marker, goalId, task.GetProperty("Id").GetString()!)?.LatestDispatchInTable == true)
            return (await ReadAsync(connection, goalId, task, token)).LatestDispatch;
        return task.TryGetProperty("LastDispatch", out var inline) ? inline : default;
    }

    internal static async Task<bool> HasTableAsync(SqliteConnection connection, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'goal_task_history')";
        return (long)(await command.ExecuteScalarAsync(token))! != 0;
    }

    // Both callers use goals and task aliases; only trusted field names from code reach this fragment.
    internal static string LatestFieldSql(bool hasTable, string kind, string field)
    {
        var property = kind switch
        {
            "verification" => "Verification",
            "dispatch" => "Dispatch",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var inline = $"json_extract(task.value, '$.Last{property}.{field}')";
        return !hasTable ? inline : $"""
            CASE WHEN json_extract(task.value, '$.HistoryInTable.Latest{property}InTable') = 1
              THEN (SELECT json_extract(h.entry_json, '$.{field}') FROM goal_task_history h
                WHERE h.goal_id = goals.id AND h.task_id = json_extract(task.value, '$.Id')
                  AND h.kind = '{kind}' ORDER BY h.sequence DESC LIMIT 1)
              ELSE {inline} END
            """;
    }

    private HistoryEntries StrictEntries<T>(IReadOnlyList<StoredEntry<string>>? rows, int? count,
        string goalId, string taskId, string kind)
        => new(ReadStrictEntries(rows, count, goalId, taskId, kind, ReadElement<T>), true, count is null);

    private JsonElement ReadElement<T>(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            document.RootElement.Deserialize<T>(options) is null)
            throw new JsonException("History entry must be an object.");
        return document.RootElement.Clone();
    }

    private static T[] ReadStrictEntries<TJson, T>(IReadOnlyList<StoredEntry<TJson>>? rows, int? count,
        string goalId, string taskId, string kind, Func<TJson, T> read)
    {
        if ((rows?.Count ?? 0) != (count ?? 0))
            throw HistoryError(goalId, taskId, kind, $"expected {count ?? 0} rows, found {rows?.Count ?? 0}");
        var entries = new T[count ?? 0];
        for (var index = 0; index < entries.Length; index++)
        {
            if (rows![index].Sequence != index)
                throw HistoryError(goalId, taskId, kind, $"sequence gap at {index}");
            try
            {
                entries[index] = read(rows[index].Json);
            }
            catch (JsonException ex)
            {
                throw HistoryError(goalId, taskId, kind, $"unparseable entry at {index}", ex);
            }
        }
        return entries;
    }

    private static Task<Dictionary<(string TaskId, string Kind), List<StoredEntry<string>>>> ReadRowsAsync(
        SqliteConnection connection, string goalId, string? taskId, CancellationToken token,
        string? requiredTaskId = null) =>
        ReadRowsAsync(connection, goalId, taskId, token, requiredTaskId, "entry_json", reader => reader.GetString(3));

    private static async Task<Dictionary<(string TaskId, string Kind), List<StoredEntry<T>>>> ReadRowsAsync<T>(
        SqliteConnection connection, string goalId, string? taskId, CancellationToken token,
        string? requiredTaskId, string entrySql, Func<SqliteDataReader, T> readEntry)
    {
        var rows = new Dictionary<(string TaskId, string Kind), List<StoredEntry<T>>>();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT task_id, kind, sequence, {entrySql} FROM goal_task_history
            WHERE goal_id = $goal
            """ + (taskId is null ? "" : " AND task_id = $task") + " ORDER BY task_id, kind, sequence";
        command.Parameters.AddWithValue("$goal", goalId);
        if (taskId is not null) command.Parameters.AddWithValue("$task", taskId);
        try
        {
            using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var key = (reader.GetString(0), reader.GetString(1));
                if (!rows.TryGetValue(key, out var entries)) rows.Add(key, entries = []);
                entries.Add(new(reader.GetInt64(2), readEntry(reader)));
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 &&
            ex.Message.Contains("no such table: goal_task_history", StringComparison.OrdinalIgnoreCase))
        {
            if (requiredTaskId is not null)
                throw HistoryError(goalId, requiredTaskId, "both", "missing goal_task_history table", ex);
            // Stores written before the mirror was introduced have only inline history.
        }
        return rows;
    }

    private static HistoryEntries SelectHistory(IReadOnlyList<StoredEntry<string>>? rows, JsonElement[] inline)
    {
        if (rows is null || rows.Count == 0 || rows.Count != inline.Length) return new(inline, false);
        for (var index = 0; index < rows.Count; index++)
            if (rows[index].Sequence != index || rows[index].Json != inline[index].GetRawText())
                return new(inline, false);

        // Parse only after equality: corrupt, incomplete and stale mirrors fall back to inline.
        return new(rows.Select(row =>
        {
            using var entry = JsonDocument.Parse(row.Json);
            return entry.RootElement.Clone();
        }).ToArray(), true);
    }

    private static JsonElement[] Elements(JsonElement value) =>
        value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? [] : value.EnumerateArray().ToArray();

    internal GoalSnapshot? DecodeDocument(string documentText, DocumentMarkers markers, long? utf8Bytes = null)
    {
        var previous = _activeReadMarkers;
        _activeReadMarkers = markers;
        try
        {
            return GoalSnapshotByteAccounting.Deserialize<GoalSnapshot>(documentText,
                SharedReadOptions.GetValue(options, CreateReadOptions), utf8Bytes);
        }
        finally
        {
            _activeReadMarkers = previous;
        }
    }

    private static JsonSerializerOptions CreateReadOptions(JsonSerializerOptions source)
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(type =>
        {
            if (type.Type == typeof(GoalSnapshot))
            {
                var marker = type.CreateJsonPropertyInfo(typeof(JsonElement), GoalTimelineStore.MarkerProperty);
                marker.Set = (_, value) => _activeReadMarkers!.Timeline = (JsonElement)value!;
                type.Properties.Add(marker);
            }
            else if (type.Type == typeof(TaskSnapshot))
            {
                var marker = type.CreateJsonPropertyInfo(typeof(JsonElement), MarkerProperty);
                marker.Set = (task, value) => _activeReadMarkers!.SetTask((TaskSnapshot)task, (JsonElement)value!);
                type.Properties.Add(marker);
            }
        });
        return new(source) { TypeInfoResolver = resolver };
    }

    internal sealed class DocumentMarkers
    {
        // Collect by object identity so a superseded Tasks property cannot contribute markers.
        private Dictionary<TaskSnapshot, JsonElement>? _tasks;
        internal JsonElement Timeline { get; set; }

        internal void SetTask(TaskSnapshot task, JsonElement marker) =>
            (_tasks ??= new(ReferenceEqualityComparer.Instance))[task] = marker;

        internal IEnumerable<(string Id, JsonElement Marker)> ForTasks(IReadOnlyList<TaskSnapshot>? tasks)
        {
            if (_tasks is null || tasks is null) yield break;
            foreach (var task in tasks)
                if (task is not null && _tasks.TryGetValue(task, out var marker))
                    yield return (task.Id, marker);
        }
    }

    internal async Task<GoalSnapshot?> HydrateDecodedAsync(SqliteConnection connection, GoalSnapshot? snapshot,
        DocumentMarkers markers, bool hotDocumentWasStub, CancellationToken token,
        Action<GoalSnapshotReadObservation>? readObserver)
    {
        if (snapshot is null || snapshot.IsMetadataOnly) return snapshot;
        if (!hotDocumentWasStub)
            return await ApplyMarkersAsync(connection, snapshot, markers.Timeline, markers.ForTasks(snapshot.Tasks), token);

        var fields = await ReadArchiveMarkersAsync(connection, snapshot.Id, token, readObserver);
        return await ApplyLegacyMarkersAsync(connection, snapshot, fields, token);
    }

    internal async Task<GoalSnapshot?> HydrateAsync(SqliteConnection connection, GoalSnapshot? snapshot, string documentText,
        CancellationToken token = default, Action<GoalSnapshotReadObservation>? readObserver = null)
    {
        // Leave malformed task collections to the existing domain validation.
        if (snapshot is null || snapshot.IsMetadataOnly) return snapshot;
        // Inspect only the marker fields, skipping the already-decoded task graphs. A hydrated
        // archive arrives with the hot stub text, so obtain its original marker-bearing payload.
        var fields = DecodeLegacyMarkers(documentText, snapshot.Id, readObserver);
        if (fields?.IsMetadataOnly == true)
            fields = await ReadArchiveMarkersAsync(connection, snapshot.Id, token, readObserver);
        return await ApplyLegacyMarkersAsync(connection, snapshot, fields, token);
    }

    private async Task<HistoryDocument?> ReadArchiveMarkersAsync(SqliteConnection connection, string goalId,
        CancellationToken token, Action<GoalSnapshotReadObservation>? readObserver)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json FROM goal_snapshot_archive WHERE goal_id = $id";
        command.Parameters.AddWithValue("$id", goalId);
        var documentText = GoalSnapshotArchiveCodec.Decode(await command.ExecuteScalarAsync(token), out var utf8Bytes)
            ?? throw new InvalidDataException($"Missing archive for goal '{goalId}'.");
        return DecodeLegacyMarkers(documentText, goalId, readObserver, utf8Bytes);
    }

    private HistoryDocument? DecodeLegacyMarkers(string documentText, string goalId,
        Action<GoalSnapshotReadObservation>? readObserver, long? utf8Bytes = null)
    {
        readObserver?.Invoke(new("history-marker-decode", goalId, documentText.Length));
        return GoalSnapshotByteAccounting.Deserialize<HistoryDocument>(documentText, options, utf8Bytes);
    }

    private Task<GoalSnapshot> ApplyLegacyMarkersAsync(SqliteConnection connection, GoalSnapshot snapshot,
        HistoryDocument? fields, CancellationToken token) =>
        ApplyMarkersAsync(connection, snapshot, fields?.TimelineInTable ?? default,
            (fields?.Tasks ?? []).OfType<HistoryTask>().Select(task => (task.Id, task.HistoryInTable)), token);

    private async Task<GoalSnapshot> ApplyMarkersAsync(SqliteConnection connection, GoalSnapshot snapshot,
        JsonElement timelineMarker, IEnumerable<(string Id, JsonElement Marker)> taskMarkers, CancellationToken token)
    {
        snapshot = await new GoalTimelineStore(options).ApplyAsync(connection, snapshot, timelineMarker, token);
        if (snapshot.Tasks is null) return snapshot;
        var markers = new Dictionary<string, HistoryMarker>();
        foreach (var (taskId, marker) in taskMarkers)
            if (ReadMarker(marker, snapshot.Id, taskId) is { } counts)
                markers[taskId] = counts;
        var rows = await ReadRowsAsync(connection, snapshot.Id, null, token, markers.Keys.FirstOrDefault());
        var tasks = new List<TaskSnapshot>();
        foreach (var task in snapshot.Tasks)
        {
            var history = markers.TryGetValue(task.Id, out var counts)
                ? StrictHistory(rows, snapshot.Id, task.Id, counts)
                : new TaskHistory(
                SelectHistory(rows.GetValueOrDefault((task.Id, "verification")),
                    Elements(JsonSerializer.SerializeToElement(task.VerificationHistory, options))),
                SelectHistory(rows.GetValueOrDefault((task.Id, "dispatch")),
                    Elements(JsonSerializer.SerializeToElement(task.DispatchHistory, options))));
            tasks.Add(task with
            {
                VerificationHistory = history.Verification.FromTable
                    ? history.Verification.IsNull ? null : history.Verification.Entries.Select(entry => entry.Deserialize<TaskVerificationSnapshot>(options)!).ToArray()
                    : task.VerificationHistory,
                DispatchHistory = history.Dispatch.FromTable
                    ? history.Dispatch.IsNull ? null : history.Dispatch.Entries.Select(entry => entry.Deserialize<TaskDispatchSnapshot>(options)!).ToArray()
                    : task.DispatchHistory,
                LastVerification = history.LatestVerification.ValueKind != JsonValueKind.Undefined
                    ? history.LatestVerification.Deserialize<TaskVerificationSnapshot>(options) : task.LastVerification,
                LastDispatch = history.LatestDispatch.ValueKind != JsonValueKind.Undefined
                    ? history.LatestDispatch.Deserialize<TaskDispatchSnapshot>(options) : task.LastDispatch
            });
        }
        return await _buildHistory.HydrateAsync(connection, snapshot with { Tasks = tasks }, token);
    }

    private sealed record HistoryDocument(bool IsMetadataOnly, IReadOnlyList<HistoryTask?>? Tasks,
        JsonElement TimelineInTable);
    private sealed record HistoryTask(string Id, JsonElement HistoryInTable);

    // A test diagnostic compares physical rows as well as accessor results, including surplus/removed-task rows.
    internal async Task<bool> IsConsistentAsync(SqliteConnection connection, string goalId, string goalJson,
        CancellationToken token = default)
    {
        using var document = JsonDocument.Parse(goalJson);
        var expected = new List<(string Task, string Kind, int Sequence, string Json)>();
        foreach (var task in document.RootElement.GetProperty("Tasks").EnumerateArray())
        {
            var history = await ReadAsync(connection, goalId, task, token);
            foreach (var (kind, entries) in new[]
                { ("verification", history.Verification.Entries), ("dispatch", history.Dispatch.Entries) })
                expected.AddRange(entries.Select((entry, index) =>
                    (task.GetProperty("Id").GetString()!, kind, index, entry.GetRawText())));
        }
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT task_id, kind, sequence, entry_json FROM goal_task_history WHERE goal_id = $goal ORDER BY task_id, kind, sequence";
        command.Parameters.AddWithValue("$goal", goalId);
        using var reader = await command.ExecuteReaderAsync(token);
        var actual = new List<(string Task, string Kind, int Sequence, string Json)>();
        while (await reader.ReadAsync(token)) actual.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3)));
        return expected.OrderBy(row => row.Task, StringComparer.Ordinal).ThenBy(row => row.Kind, StringComparer.Ordinal)
            .ThenBy(row => row.Sequence).SequenceEqual(actual);
    }

    internal static bool HasUsableCachedHistories(ProviderUsageTaskEvidence task) =>
        task.DispatchHistory is not null && task.VerificationHistory is not null;

    internal delegate bool InvalidVerification(ref Utf8JsonReader reader);

    // Streaming inline adapter for the backlog shape probe. Preserve its allocation-free legacy path.
    internal static bool TryReadInlineShape(ref Utf8JsonReader reader, InvalidVerification invalidVerification,
        out bool requiresReconstruction)
    {
        requiresReconstruction = false;
        var verification = reader.ValueTextEquals(VerificationProperty);
        if (!verification && !reader.ValueTextEquals(DispatchProperty)) return false;
        reader.Read();
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            SkipInlineValue(ref reader);
            return true;
        }
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            requiresReconstruction |= verification ? invalidVerification(ref reader) : reader.TokenType == JsonTokenType.Null;
            SkipInlineValue(ref reader);
        }
        return true;
    }

    private static void SkipInlineValue(ref Utf8JsonReader reader)
    {
        if (!reader.TrySkip()) throw new JsonException("Incomplete similarity projection value.");
    }

    internal async Task<bool> HasRowsAsync(SqliteConnection connection, string goalId, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM goal_task_history WHERE goal_id = $goal)";
        command.Parameters.AddWithValue("$goal", goalId);
        try { return (long)(await command.ExecuteScalarAsync(token))! != 0; }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 &&
            ex.Message.Contains("no such table: goal_task_history", StringComparison.OrdinalIgnoreCase)) { return false; }
    }

    internal async Task<bool> RequiresReconstructionAsync(SqliteConnection connection, string goalId, Stream stream,
        InvalidVerification invalidVerification, CancellationToken token)
    {
        using var document = await GoalSnapshotByteAccounting.ParseAsync(stream, token);
        var invalid = false;
        foreach (var task in document.RootElement.GetProperty("Tasks").EnumerateArray())
        {
            if (task.ValueKind == JsonValueKind.Null) continue; // The existing shape probe owns null-task validation.
            var history = await ReadAsync(connection, goalId, task, token);
            invalid |= history.Dispatch.Entries.Any(entry => entry.ValueKind == JsonValueKind.Null);
            foreach (var entry in history.Verification.Entries)
                invalid |= InvalidEntry(entry, invalidVerification);
        }
        return invalid;
    }

    private static bool InvalidEntry(JsonElement entry, InvalidVerification invalidVerification)
    {
        var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(entry.GetRawText()));
        reader.Read();
        return invalidVerification(ref reader);
    }
}
