using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using SQLitePCL;

// Parallel-safe: the diagnostic hook filters by the private database path.
public sealed class GoalTaskHistoryReadSnapshotTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitBetweenDocumentAndHistoryReadKeepsOneSnapshot(bool stateRead)
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        fixture.Execute("PRAGMA journal_mode=WAL");
        var goal = GoalTaskHistoryTestFixture.Snapshot(entries: 2);
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        var revised = goal with { Tasks = goal.Tasks.Select(task => task with
            { VerificationHistory = task.VerificationHistory!.Take(1).ToArray() }).ToArray() };
        var wrote = false;
        Exception? writeFailure = null;
        using var trace = new HistoryReadTrace(fixture.DatabasePath, () =>
        {
            if (wrote) return;
            wrote = true;
            try
            {
                Task.Run(() => fixture.Repository.SaveGoalSnapshotsAsync([revised]))
                    .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            }
            catch (TimeoutException ex) { writeFailure = new TimeoutException("History-changing save did not commit during the paused history read.", ex); }
            catch (Exception ex) { writeFailure = ex; }
        });
        var captured = stateRead
            ? await fixture.Repository.TransactGoalStateWithOutboxAsync("history-read-snapshot", new(goal.Id), (state, _) =>
                Task.FromResult((false, state, state!.Goal, (IReadOnlyList<OrchestratorStateOutboxMessage>)[])))
            : await fixture.Repository.LoadGoalAsync(new(goal.Id));
        Assert.True(wrote);
        Assert.Null(writeFailure);
        Assert.Equal(GoalTaskHistoryTestFixture.Json(goal), GoalTaskHistoryTestFixture.Json(captured));
        Assert.Equal(GoalTaskHistoryTestFixture.Json(revised), GoalTaskHistoryTestFixture.Json(
            await fixture.Repository.LoadGoalAsync(new(goal.Id))));
    }

    private sealed class HistoryReadTrace : IDisposable
    {
        private readonly string databasePath;
        private readonly delegate_trace callback;
        private readonly HashSet<sqlite3> handles = [];

        internal HistoryReadTrace(string path, Action onRead)
        {
            databasePath = Path.GetFullPath(path);
            callback = (_, statement) =>
            {
                if (statement.utf8_to_string().TrimStart().StartsWith(
                    "SELECT task_id, kind, sequence, entry_json FROM goal_task_history", StringComparison.Ordinal))
                    onRead();
            };
            StateDbConnectionFactory.ConnectionOpenedForDiagnostics += ObserveConnection;
        }

        private void ObserveConnection(SqliteConnection connection, string path)
        {
            if (!Path.GetFullPath(path).Equals(databasePath, StringComparison.OrdinalIgnoreCase)) return;
            lock (handles)
                if (handles.Add(connection.Handle)) raw.sqlite3_trace(connection.Handle, callback, null);
        }

        public void Dispose() => StateDbConnectionFactory.ConnectionOpenedForDiagnostics -= ObserveConnection;
    }
}
