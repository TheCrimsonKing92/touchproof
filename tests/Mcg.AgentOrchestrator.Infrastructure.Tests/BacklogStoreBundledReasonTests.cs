using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: the migration and both reads use only this test's SQLite file.
public sealed class BacklogStoreBundledReasonTests
{
    [Fact]
    public async Task OpensVersionTwoStoreAndReadsExistingItemWithEmptyReason()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "backlog.db");
            var store = new BacklogStore(path);
            var item = await store.AddAsync("Existing item", "Original body", "source-goal");
            await store.UpdateAsync(item.Id, new(Priority: "Critical", Tags: "original"));
            var before = await store.GetByExactIdAsync(item.Id);
            Assert.NotNull(before);
            var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false };
            using (var database = new SqliteConnection(connectionString.ToString()))
            {
                database.Open();
                BoardFillSnapshotTestDatabase.RemoveSnapshotProof(database);
                using var command = database.CreateCommand();
                command.CommandText = """
                    ALTER TABLE backlog DROP COLUMN bundled_reason;
                    UPDATE store_schema_versions SET version = 2 WHERE store_name = 'backlog';
                    """;
                Assert.Equal(1, command.ExecuteNonQuery());
            }

            _ = new BacklogStore(path);
            var reopened = BacklogStore.OpenReadOnly(path);
            var listed = Assert.Single(await reopened.ListAsync());
            var shown = await reopened.GetByIdPrefixAsync(item.Id[..8]);

            Assert.NotNull(shown);
            Assert.Equal("", listed.BundledReason);
            Assert.Equal("", shown.BundledReason);
            var expected = JsonSerializer.Serialize(before);
            Assert.Equal(expected, JsonSerializer.Serialize(listed));
            Assert.Equal(expected, JsonSerializer.Serialize(shown));

            var upgradedBytes = await File.ReadAllBytesAsync(path);
            BacklogStore.Setup(path);
            Assert.Equal(upgradedBytes, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }
}
