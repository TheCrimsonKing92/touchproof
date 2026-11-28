using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class AcceptanceGateSqliteFaultContextTests : GoalAcceptanceVerifierTestBase
{
    [Fact]
    public async Task LockedDecisionStoreNamesFaultingDatabaseAndOperation()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "decision check", "type": "command", "command": "git", "arguments": ["diff", "--check"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        using var ownerResults = AcceptanceOwnerResultsTestRoot.Create();
        try
        {
            var path = Path.Combine(root, "collaboration-items.db");
            _ = CollaborationItemStore.ForDirectory(root);
            var store = CollaborationItemStore.OpenExisting(root);
            Assert.Null(await store.GetDecisionStateAsync("missing-decision"));

            using var locker = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false
            }.ToString());
            locker.Open();
            using var command = locker.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=DELETE";
            Assert.Equal("delete", command.ExecuteScalar());
            command.CommandText = "BEGIN EXCLUSIVE";
            command.ExecuteNonQuery();

            var verifier = new GoalAcceptanceVerifier(async (_, _, token) =>
            {
                await store.GetDecisionStateAsync("missing-decision", token);
                return new GoalAcceptanceVerifier.CommandResult(0, string.Empty);
            });
            var fault = await Assert.ThrowsAsync<AcceptanceGateEngineException>(() =>
                verifier.RunOwnedAsync(root, null, null, null, null, CancellationToken.None,
                    ownerResults.CreateOptions(_ => { })));

            var sqlite = Assert.IsType<SqliteException>(fault.InnerException);
            Assert.Equal(5, sqlite.SqliteErrorCode);
            Assert.Contains("phase=check-execution", fault.Message);
            Assert.Contains("target=decision check", fault.Message);
            Assert.Contains($"database={path}", fault.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("operation=CollaborationItemStore.GetDecisionState", fault.Message);
            Assert.Contains($"SqliteException: {sqlite.Message}", fault.Message);

            var wrapped = AcceptanceGateEngineException.Capture(
                new InvalidOperationException("wrapped\r\nstore fault", new AggregateException(sqlite)),
                new("check-execution", "decision check"));
            Assert.Contains($"database={path}", wrapped.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("operation=CollaborationItemStore.GetDecisionState", wrapped.Message);
            Assert.Contains("InvalidOperationException: wrapped  store fault", wrapped.Message);
            Assert.DoesNotContain('\r', wrapped.Message);
            Assert.DoesNotContain('\n', wrapped.Message);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }
}
