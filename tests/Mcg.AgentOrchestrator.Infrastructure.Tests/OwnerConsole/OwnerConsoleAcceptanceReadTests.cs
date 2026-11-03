using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each fixture owns its temporary database, schema setup and nonpooled connections.
public sealed class OwnerConsoleAcceptanceReadTests
{
    private static readonly GoalId Selected = new("selected-goal");
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(1);
    private static readonly string Candidate = new('a', 40);
    private static readonly string Main = new('b', 40);
    private static readonly string Other = new('c', 40);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SelectedGoal_ManyUnrelatedEvents_UsesGoalIndexAndReturnsOutcome()
    {
        using var fixture = new Fixture();
        var expected = Entry("selected-failure", At, "failed");
        fixture.Append(expected);
        const int unrelatedCount = 8192;
        // Payload identities deliberately match: only the indexed event goal excludes these newer passes.
        fixture.Execute("""
            WITH RECURSIVE numbers(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM numbers WHERE n < $count)
            INSERT INTO run_events(event_id, occurred_at, event_type, goal_id, payload_json)
            SELECT 'unrelated-' || n, $at, $type, 'unrelated-goal-' || n, $payload FROM numbers
            """, ("$count", unrelatedCount), ("$at", At.AddDays(1).ToString("O")),
            ("$type", RunEventTypes.GoalOperation), ("$payload", Payload(Entry("unrelated-pass", At.AddDays(1), "passed"))));

        using (var session = new SqliteReadSession(fixture.StorePath))
        {
            using var population = session.Connection.CreateCommand();
            population.CommandText = """
                SELECT count(*), count(DISTINCT goal_id), sum(goal_id = $goal)
                FROM run_events WHERE event_type = $type
                """;
            population.Parameters.AddWithValue("$goal", Selected.Value);
            population.Parameters.AddWithValue("$type", RunEventTypes.GoalOperation);
            using (var rows = population.ExecuteReader())
            {
                Assert.True(rows.Read());
                Assert.Equal(unrelatedCount + 1, rows.GetInt64(0));
                Assert.Equal(unrelatedCount + 1, rows.GetInt64(1));
                Assert.Equal(1, rows.GetInt64(2));
            }

            // Explain the production factory's command, including its actual parameter bindings.
            using var command = GoalOperationJournal.CreateAcceptanceOutcomeCommand(
                session.Connection, Selected, Candidate, Main);
            command.CommandText = "EXPLAIN QUERY PLAN " + command.CommandText;
            using var reader = command.ExecuteReader();
            var plan = new List<string>();
            while (reader.Read()) plan.Add(reader.GetString(3));
            var access = Assert.Single(plan.Where(detail => detail.Contains("run_events", StringComparison.OrdinalIgnoreCase)));
            Assert.True(access.Contains("SEARCH run_events", StringComparison.OrdinalIgnoreCase) &&
                access.Contains("ix_run_events_goal_seq", StringComparison.Ordinal) &&
                access.Contains("(goal_id=?)", StringComparison.OrdinalIgnoreCase),
                "Expected goal-bounded access through ix_run_events_goal_seq; actual: " + access);
        }

        Assert.Equal(expected, await fixture.ReadAsync());
    }

    [Fact]
    public async Task NewestOutcome_OccurrenceThenSequence_PreservesIdentityIsolation()
    {
        using var fixture = new Fixture();
        var newest = Entry("newest", At.AddHours(2), "failed") with
        {
            BranchHeadSha = " " + Candidate.ToUpperInvariant() + " ",
            MainHeadSha = " " + Main.ToUpperInvariant() + " "
        };
        fixture.Append(newest);
        // Inserted later, but the event's occurrence time wins over sequence and payload time.
        fixture.Append(Entry("older-event", At.AddDays(1), "passed"), occurredAt: At);
        fixture.Append(Entry("other-type", At.AddDays(2), "passed"), eventType: "other.event");
        fixture.Append(Entry("other-candidate", At.AddDays(2), "passed") with { BranchHeadSha = Other });
        fixture.Append(Entry("other-main", At.AddDays(2), "passed") with { MainHeadSha = Other });
        fixture.Append(Entry("no-outcome", At.AddDays(2), " "));
        Assert.Equal(newest, await fixture.ReadAsync());

        var tieWinner = Entry("same-time-later-sequence", newest.At, "aborted:state-guard");
        fixture.Append(tieWinner);
        Assert.Equal(tieWinner, await fixture.ReadAsync());
    }

    [Fact]
    public async Task MissingOutcome_SupportedStore_ReturnsNullWithoutChangingDatabase()
    {
        using var fixture = new Fixture();
        fixture.Append(Entry("other-candidate", At, "passed") with { BranchHeadSha = Other });
        fixture.Append(Entry("other-main", At, "passed") with { MainHeadSha = Other });
        fixture.Append(Entry("no-outcome", At, null));
        var before = SHA256.HashData(File.ReadAllBytes(fixture.StorePath));

        Assert.Null(await fixture.ReadAsync());

        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(fixture.StorePath)));
    }

    [Fact]
    public async Task SelectedOutcome_PayloadGoalConflicts_RejectsInsteadOfOlderPass()
    {
        using var fixture = new Fixture();
        fixture.Append(Entry("older-pass", At, "passed"));
        fixture.Append(Entry("conflicting-goal", At.AddMinutes(1), "failed") with { GoalId = new("other-goal") });

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ReadAsync());
    }

    [Fact]
    public async Task MissingStore_ReadFailsWithoutCreatingDatabase()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "missing.db");
            await Assert.ThrowsAsync<SqliteException>(() => GoalOperationJournal.ReadNewestAcceptanceOutcomeAsync(
                path, Selected, Candidate, Main, Token));
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public async Task CancelledRead_MissingStore_PropagatesBeforeOpeningDatabase()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GoalOperationJournal.ReadNewestAcceptanceOutcomeAsync(
                Path.Combine(root, "missing.db"), Selected, Candidate, Main, cancelled.Token));
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    private static GoalOperationJournalEntry Entry(string key, DateTimeOffset at, string? outcome) =>
        new(key, Selected, "acceptance", GoalOperationStatus.Completed, at, "full gate", Candidate, Main, outcome);

    private static string Payload(GoalOperationJournalEntry entry)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter());
        return JsonSerializer.Serialize(entry, options);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = SharedTestSupport.CreateTempDirectory();
        public string StorePath { get; }

        public Fixture()
        {
            StorePath = Path.Combine(root, "run-events.db");
            RunEventStoreSetup.Setup(StorePath);
        }

        public Task<GoalOperationJournalEntry?> ReadAsync() =>
            GoalOperationJournal.ReadNewestAcceptanceOutcomeAsync(StorePath, Selected, Candidate, Main, Token);

        public void Append(GoalOperationJournalEntry entry, DateTimeOffset? occurredAt = null, string? eventType = null) =>
            Execute("""
                INSERT INTO run_events(event_id, occurred_at, event_type, goal_id, payload_json)
                VALUES ($id, $at, $type, $goal, $payload)
                """, ("$id", entry.IdempotencyKey), ("$at", (occurredAt ?? entry.At).ToString("O")),
                ("$type", eventType ?? RunEventTypes.GoalOperation), ("$goal", Selected.Value), ("$payload", Payload(entry)));

        public void Execute(string sql, params (string Name, object Value)[] bindings)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = StorePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var binding in bindings) command.Parameters.AddWithValue(binding.Name, binding.Value);
            command.ExecuteNonQuery();
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(root);
    }
}
