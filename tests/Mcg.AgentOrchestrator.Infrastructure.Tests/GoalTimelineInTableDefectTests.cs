using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its database.
public sealed class GoalTimelineInTableDefectTests
{
    [Theory]
    [InlineData("fewer", false)]
    [InlineData("more", false)]
    [InlineData("gap", false)]
    [InlineData("negative", false)]
    [InlineData("fraction", false)]
    [InlineData("text", false)]
    [InlineData("entry", false)]
    [InlineData("non-object", false)]
    [InlineData("invalid-date", false)]
    [InlineData("invalid-payload", false)]
    [InlineData("invalid-message", false)]
    [InlineData("missing-table", false)]
    [InlineData("fewer", true)]
    [InlineData("more", true)]
    [InlineData("gap", true)]
    public async Task MarkedDefect_AllHydratingAndSparsePaths_ThrowWithGoalIdentity(string defect, bool archived)
    {
        using var support = new GoalTimelineTestSupport();
        var goal = GoalTimelineTestSupport.Snapshot();
        await support.SeedAsync(goal);
        if (archived)
            Assert.Equal(1, await support.Repository.ArchiveTerminalGoalSnapshotsAsync(GoalTimelineTestSupport.At.AddDays(1)));
        support.Fixture.Execute(defect switch
        {
            "fewer" => "DELETE FROM goal_timeline WHERE sequence = 2",
            "more" => "INSERT INTO goal_timeline SELECT goal_id, 3, entry_json FROM goal_timeline WHERE sequence = 2",
            "gap" => "UPDATE goal_timeline SET sequence = 3 WHERE sequence = 2",
            "negative" => "UPDATE goals SET snapshot_json = json_set(snapshot_json, '$.TimelineInTable', -1)",
            "fraction" => "UPDATE goals SET snapshot_json = json_set(snapshot_json, '$.TimelineInTable', 1.5)",
            "text" => "UPDATE goals SET snapshot_json = json_set(snapshot_json, '$.TimelineInTable', '3')",
            "entry" => "UPDATE goal_timeline SET entry_json = '{' WHERE sequence = 1",
            "non-object" => "UPDATE goal_timeline SET entry_json = 'null' WHERE sequence = 1",
            "invalid-date" => "UPDATE goal_timeline SET entry_json = json_set(entry_json, '$.Kind', 'GoalPolicyDecision', '$.OccurredAt', 'invalid date') WHERE sequence = 1",
            "invalid-payload" => "UPDATE goal_timeline SET entry_json = json_set(entry_json, '$.OperatorGates', 1) WHERE sequence = 1",
            "invalid-message" => "UPDATE goal_timeline SET entry_json = json_set(entry_json, '$.Message', 1) WHERE sequence = 1",
            _ => "DROP TABLE goal_timeline"
        });
        foreach (var path in GoalTimelineTestSupport.Paths(archived))
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => support.ReadAsync(path, goal));
            Assert.Contains(goal.Id, error.Message);
        }
        var attention = await Assert.ThrowsAsync<InvalidDataException>(() => support.Repository.ReadAttentionProjectionAsync());
        Assert.Contains(goal.Id, attention.Message);
        using var connection = support.Fixture.Open();
        var provider = await Assert.ThrowsAsync<InvalidDataException>(() => ProviderHoldSeedEvidenceReader.ReadAsync(
            connection, [new(goal.Id)], GoalTimelineTestSupport.Options, default));
        Assert.Contains(goal.Id, provider.Message);
    }
}
