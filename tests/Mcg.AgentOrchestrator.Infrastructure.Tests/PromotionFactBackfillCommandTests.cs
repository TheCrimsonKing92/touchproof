using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its Git repository, events, writers and SQLite store.
public sealed class PromotionFactBackfillCommandTests
{
    [Fact]
    public async Task DryRunLeavesStoreUnchanged_ApplyKeepsNativeRowAndWritesReconstructedFact()
    {
        using var f = new PromotionFactBackfillFixture();
        var native = f.AddGoal();
        var reconstructed = f.AddGoal();
        await f.Save();
        var original = new GoalPromotionFact(native.Id.Value, f.Candidate, f.Prior, f.Promoted,
            PromotionFactBackfillFixture.At.AddDays(-7).ToUniversalTime(), GoalPromotionFact.SoloProvenance);
        f.Repository.RecordPromotionFacts([original]);
        f.Land(native, f.Prior); // Conflicting event must never overwrite a native fact.
        f.Land(reconstructed);
        var nativeRow = Assert.Single(f.Rows());
        var before = File.ReadAllBytes(f.Workspace.SqliteStatePath);
        var dryRun = f.Run();
        using var dryJson = dryRun.Json;
        Assert.Equal(0, dryRun.Exit);
        Assert.Equal(string.Empty, dryRun.Error);
        Assert.Equal(1, dryJson.RootElement.GetProperty("planned").GetInt32());
        Assert.Equal(1, dryJson.RootElement.GetProperty("alreadyRecorded").GetInt32());
        Assert.Equal(0, dryJson.RootElement.GetProperty("written").GetInt32());
        Assert.False(dryJson.RootElement.GetProperty("applied").GetBoolean());
        Assert.Equal(before, File.ReadAllBytes(f.Workspace.SqliteStatePath));
        Assert.Equal(new[] { nativeRow }, f.Rows());

        var applied = f.Run(apply: true);
        using var applyJson = applied.Json;
        Assert.Equal(0, applied.Exit);
        Assert.Equal(string.Empty, applied.Error);
        Assert.Equal(1, applyJson.RootElement.GetProperty("written").GetInt32());
        Assert.True(applyJson.RootElement.GetProperty("applied").GetBoolean());
        Assert.Contains(nativeRow, f.Rows());
        Assert.Equal(2, f.Rows().Length);
        var metadata = await SqliteOrchestratorStateRepository.OpenReadOnly(f.Workspace.SqliteStatePath).ListGoalMetadataAsync();
        Assert.Equal(original, metadata.Single(g => g.Id == native.Id.Value).PromotionFact);
        var fact = metadata.Single(g => g.Id == reconstructed.Id.Value).PromotionFact;
        Assert.NotNull(fact);
        Assert.Equal("reconstructed:merge-evidence", fact.Provenance);
        Assert.Equal(PromotionFactBackfillFixture.At.ToUniversalTime(), fact.PromotedAt);
        Assert.Equal(f.Promoted, fact.PromotedMainRevision);
    }

    [Fact]
    public async Task SecondApply_PlansNothingAndLeavesFactRowsUnchanged()
    {
        using var f = new PromotionFactBackfillFixture();
        var reconstructable = f.AddGoal();
        var unknown = f.AddGoal();
        var conflict = f.AddGoal();
        await f.Save();
        f.Land(reconstructable);
        f.Land(conflict, f.Prior);
        f.Land(conflict);
        var first = f.Run(apply: true);
        using var firstJson = first.Json;
        Assert.Equal(0, first.Exit);
        Assert.Equal(1, firstJson.RootElement.GetProperty("written").GetInt32());
        Assert.Equal(unknown.Id.Value, firstJson.RootElement.GetProperty("unknownGoals")[0].GetProperty("goalId").GetString());
        var rows = f.Rows();
        var second = f.Run(apply: true);
        using var secondJson = second.Json;
        Assert.Equal(0, second.Exit);
        Assert.Equal(0, secondJson.RootElement.GetProperty("planned").GetInt32());
        Assert.Equal(0, secondJson.RootElement.GetProperty("written").GetInt32());
        Assert.Equal(1, secondJson.RootElement.GetProperty("alreadyRecorded").GetInt32());
        Assert.Equal(firstJson.RootElement.GetProperty("unknownGoals").GetRawText(), secondJson.RootElement.GetProperty("unknownGoals").GetRawText());
        Assert.Equal(firstJson.RootElement.GetProperty("conflictGoals").GetRawText(), secondJson.RootElement.GetProperty("conflictGoals").GetRawText());
        Assert.Equal(rows, f.Rows());
    }

    [Fact]
    public async Task SharedRevisionWithDifferentTimesAndSources_WritesSeparateGroups()
    {
        using var f = new PromotionFactBackfillFixture();
        var first = f.AddGoal();
        var second = f.AddGoal();
        var third = f.AddGoal();
        await f.Save();
        f.Land(first);
        f.Land(second, at: PromotionFactBackfillFixture.At.AddHours(1));
        f.Writer().AppendGoalLandedFromAncestry(third.Id, "goal/ancestry", f.Candidate, f.Promoted);
        var applied = f.Run(apply: true);
        using var json = applied.Json;
        Assert.Equal(0, applied.Exit);
        Assert.Equal(3, json.RootElement.GetProperty("written").GetInt32());
        var metadata = await SqliteOrchestratorStateRepository.OpenReadOnly(f.Workspace.SqliteStatePath).ListGoalMetadataAsync();
        Assert.Equal(3, f.Rows().Length);
        Assert.Equal(PromotionFactBackfillFixture.At.AddHours(1).ToUniversalTime(), metadata.Single(g => g.Id == second.Id.Value).PromotionFact!.PromotedAt);
        Assert.Equal("reconstructed:ancestry", metadata.Single(g => g.Id == third.Id.Value).PromotionFact!.Provenance);
    }

    [Fact]
    public async Task UnreadableStoredFact_IsUnknownAndRemainsUntouched()
    {
        using var f = new PromotionFactBackfillFixture();
        var goal = f.AddGoal();
        await f.Save();
        f.Repository.RecordPromotionFacts([new(goal.Id.Value, f.Candidate, f.Prior, f.Promoted,
            PromotionFactBackfillFixture.At.ToUniversalTime(), GoalPromotionFact.SoloProvenance)]);
        f.Execute("UPDATE goal_promotion_facts SET promoted_at = 'invalid'");
        f.Land(goal);
        var rows = f.Rows();
        var result = f.Run(apply: true);
        using var json = result.Json;
        Assert.Equal(0, result.Exit);
        Assert.Equal(0, json.RootElement.GetProperty("planned").GetInt32());
        Assert.Equal("stored-fact-unreadable", json.RootElement.GetProperty("unknownGoals")[0].GetProperty("reason").GetString());
        Assert.Equal(rows, f.Rows());
    }

    [Fact]
    public async Task LegacyStore_DryRunPlansAndApplyRefusesWithoutMigration()
    {
        using var f = new PromotionFactBackfillFixture();
        var goal = f.AddGoal();
        await f.Save();
        f.Land(goal);
        f.Execute("DROP TABLE goal_promotion_facts");
        var before = File.ReadAllBytes(f.Workspace.SqliteStatePath);
        var dry = f.Run();
        using var json = dry.Json;
        Assert.Equal(0, dry.Exit);
        Assert.Equal(1, json.RootElement.GetProperty("planned").GetInt32());
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, CliPromotionFactBackfillCommand.Run(["promotion-facts-backfill", "--apply"],
            f.Workspace.SqliteStatePath, f.Workspace.GoalLifecycleEventsDirectory, f.Root,
            f.Workspace.IntegrationBranch, output, error));
        Assert.Contains("goal_promotion_facts", error.ToString());
        Assert.Equal(before, File.ReadAllBytes(f.Workspace.SqliteStatePath));
        Assert.Empty(f.Query("SELECT name FROM sqlite_schema WHERE name = 'goal_promotion_facts'"));
    }

    [Fact]
    public void Catalog_HelpAndFlags_ExposeBackfill()
    {
        Assert.Contains("promotion-facts-backfill", CliArgumentParser.RecognizedCommands);
        Assert.True(CliPromotionFactBackfillCommand.IsCommand(["PROMOTION-FACTS-BACKFILL"]));
        CliCommandHelp.ThrowIfInvalidFlags(["promotion-facts-backfill", "--apply", "--json"]);
        Assert.True(CliCommandHelp.IsCommandSpecificHelp(["promotion-facts-backfill", "--help"]));
        Assert.Throws<ArgumentException>(() => CliCommandHelp.ThrowIfInvalidFlags(["promotion-facts-backfill", "--bogus"]));
    }

    [Fact]
    public async Task NativeFactRecordedDuringPlanning_PreservesRowAndContinuesOtherGroups()
    {
        using var f = new PromotionFactBackfillFixture();
        var raced = f.AddGoal();
        var other = f.AddGoal();
        await f.Save();
        f.Land(raced);
        f.Land(other, at: PromotionFactBackfillFixture.At.AddHours(1));
        var native = new GoalPromotionFact(raced.Id.Value, f.Candidate, f.Prior, f.Promoted,
            PromotionFactBackfillFixture.At.ToUniversalTime(), GoalPromotionFact.SoloProvenance);
        var injected = false;
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = CliPromotionFactBackfillCommand.Run(["promotion-facts-backfill", "--apply", "--json"],
            f.Workspace.SqliteStatePath, f.Workspace.GoalLifecycleEventsDirectory, f.Root,
            f.Workspace.IntegrationBranch, output, error, (directory, args) =>
            {
                if (!injected) { f.Repository.RecordPromotionFacts([native]); injected = true; }
                return GitCli.Run(directory, args);
            });
        using var json = System.Text.Json.JsonDocument.Parse(output.ToString());
        Assert.True(injected);
        Assert.Equal(1, exit);
        Assert.Contains("Error: promotion", error.ToString());
        Assert.Equal(1, json.RootElement.GetProperty("written").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("failedPromotions").GetArrayLength());
        Assert.Equal(2, f.Rows().Length);
        Assert.Equal(native, await f.Repository.ReadPromotionFactAsync(raced.Id));
        Assert.Equal("reconstructed:merge-evidence", (await f.Repository.ReadPromotionFactAsync(other.Id))!.Provenance);
    }
}
