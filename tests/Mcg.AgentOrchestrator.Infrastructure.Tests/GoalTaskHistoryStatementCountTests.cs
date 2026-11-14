using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using SQLitePCL;

// Parallel-safe: each measurement traces only its own private database.
public sealed class GoalTaskHistoryStatementCountTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public async Task SaveAndLoadStatementCountsAreIndependentOfTaskCount(int entries)
    {
        var ten = await MeasureAsync(10, entries);
        var hundred = await MeasureAsync(100, entries);
        Assert.Equal(ten.Save, hundred.Save);
        Assert.Equal(ten.UnchangedSave, hundred.UnchangedSave);
        Assert.Equal(ten.Load, hundred.Load);
    }

    private static async Task<(int Save, int UnchangedSave, int Load)> MeasureAsync(int tasks, int entries)
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.Snapshot(entries: entries, taskCount: tasks);
        var kernel = AgentOrchestratorKernel.FromSnapshot(new([goal], []));
        goal = kernel.ExportGoalSnapshot(new GoalId(goal.Id));
        using var trace = new StatementTrace(fixture.DatabasePath);

        await fixture.Repository.SaveAsync(kernel);
        var save = trace.Take();
        Assert.Contains(save, sql => sql.StartsWith("INSERT INTO goals", StringComparison.Ordinal));
        AssertMirrorWriteWasTraced(save);
        await fixture.AssertMirroredAsync(goal);
        Assert.Equal(tasks * entries * 2, fixture.HistoryRows().Length);
        trace.Take(); // Exclude the assertions' own reads from the next measurement.

        await fixture.Repository.SaveAsync(kernel);
        var unchangedSave = trace.Take();
        AssertMirrorWriteWasTraced(unchangedSave);

        var loaded = await fixture.Repository.LoadAsync();
        var load = trace.Take();
        Assert.Equal(tasks, loaded.Goals.Single().Tasks.Count);
        Assert.Single(load.Where(sql => sql.StartsWith("SELECT task_id, kind, sequence, entry_json FROM goal_task_history",
            StringComparison.Ordinal)));
        await fixture.AssertMirroredAsync(goal);
        Assert.Equal(tasks * entries * 2, fixture.HistoryRows().Length);
        return (save.Length, unchangedSave.Length, load.Length);
    }

    private static void AssertMirrorWriteWasTraced(string[] statements)
    {
        Assert.Single(statements.Where(sql => sql.StartsWith("INSERT INTO goal_task_history", StringComparison.Ordinal)));
        Assert.Single(statements.Where(sql => sql.StartsWith("DELETE FROM goal_task_history", StringComparison.Ordinal)));
    }

    private sealed class StatementTrace : IDisposable
    {
        private readonly string _databasePath;
        private readonly ConcurrentQueue<string> _statements = new();
        private readonly HashSet<sqlite3> _handles = [];
        private readonly delegate_trace _callback;

        internal StatementTrace(string databasePath)
        {
            _databasePath = Path.GetFullPath(databasePath);
            _callback = (_, statement) =>
            {
                var sql = statement.utf8_to_string().TrimStart();
                _statements.Enqueue(sql[..Math.Min(sql.Length, 256)]);
            };
            StateDbConnectionFactory.ConnectionOpenedForDiagnostics += ObserveConnection;
        }

        private void ObserveConnection(SqliteConnection connection, string path)
        {
            if (!Path.GetFullPath(path).Equals(_databasePath, StringComparison.OrdinalIgnoreCase)) return;
            lock (_handles)
                if (_handles.Add(connection.Handle)) raw.sqlite3_trace(connection.Handle, _callback, null);
        }

        internal string[] Take()
        {
            var statements = new List<string>();
            while (_statements.TryDequeue(out var sql)) statements.Add(sql);
            return statements.ToArray();
        }

        public void Dispose() => StateDbConnectionFactory.ConnectionOpenedForDiagnostics -= ObserveConnection;
    }
}
