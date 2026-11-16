using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its database.
public sealed class GoalTimelineStoreFormatTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstWrite_CreatesMissingTableAndRecordsDeclaredLevel(bool recordedLevelOne)
    {
        using var support = new GoalTimelineTestSupport();
        var goal = GoalTimelineTestSupport.Snapshot();
        support.Fixture.InsertLegacy(goal);
        support.Fixture.Execute("DROP TABLE goal_timeline");
        if (recordedLevelOne)
        {
            using var connection = support.Fixture.Open();
            StoreSchemaVersions.UpgradeToCurrent(connection, StoreSchemaRegistry.StateStoreFormat);
            support.Fixture.Execute("UPDATE store_schema_versions SET version = 1 WHERE store_name = 'orchestrator-state'");
        }
        // Read-only opens do not migrate legacy stores.
        Assert.Equal(GoalTimelineTestSupport.Json(goal), GoalTimelineTestSupport.Json(await support.Repository.LoadGoalAsync(new(goal.Id))));
        using (var connection = support.Fixture.Open()) Assert.False(await GoalTimelineStore.HasTableAsync(connection));
        await support.Repository.SaveGoalSnapshotsAsync([goal]);
        using (var connection = support.Fixture.Open())
        {
            Assert.True(await GoalTimelineStore.HasTableAsync(connection));
            Assert.Equal(2, StoreSchemaRegistry.StateStoreFormat.CurrentVersion);
            Assert.Equal(StoreSchemaRegistry.StateStoreFormat.CurrentVersion,
                StoreSchemaVersions.Read(connection, StoreSchemaRegistry.StateStoreFormat.StoreName));
        }
        Assert.Equal(GoalTimelineTestSupport.Json(goal), GoalTimelineTestSupport.Json(await support.Repository.LoadGoalAsync(new(goal.Id))));
    }

    [Fact]
    public async Task FormatUpgradeRollback_RemovesTableAndStampTogether()
    {
        using var support = new GoalTimelineTestSupport();
        support.Fixture.InsertLegacy(GoalTimelineTestSupport.Snapshot());
        support.Fixture.Execute("DROP TABLE goal_timeline");
        using var connection = support.Fixture.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "BEGIN IMMEDIATE";
        command.ExecuteNonQuery();
        StateStoreFormatFence.Enforce(connection);
        Assert.True(await GoalTimelineStore.HasTableAsync(connection));
        Assert.Equal(2, StoreSchemaVersions.Read(connection, StoreSchemaRegistry.StateStoreFormat.StoreName));
        command.CommandText = "ROLLBACK";
        command.ExecuteNonQuery();
        Assert.False(await GoalTimelineStore.HasTableAsync(connection));
        Assert.Null(StoreSchemaVersions.Read(connection, StoreSchemaRegistry.StateStoreFormat.StoreName));
    }
}
