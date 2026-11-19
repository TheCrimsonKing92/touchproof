using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: the database and journal paths are unique, and both measurements are scoped to this reload.
public sealed class ConductLoopLiveGoalMetadataReloadTests
{
    [Fact]
    public async Task LoadKernel_WithTerminalHistory_NarrowsReadsAndPreservesDependencyMarks()
    {
        const int terminalCount = 200;
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var database = Path.Combine(root, "state.db");
            StateDbMigrations.EnsureUpToDate(database);
            var repository = new SqliteOrchestratorStateRepository(database);
            var seed = new AgentOrchestratorKernel();
            var agents = AgentCatalog.Default().Agents;
            var terminalGoals = Enumerable.Range(0, terminalCount)
                .Select(index => GoalLifecycleCommands.CreateAndActivateSimpleGoal(seed, agents, $"Terminal {index}"))
                .ToArray();
            var landed = terminalGoals[0];
            var unlanded = terminalGoals[1];
            var requested = terminalGoals[2];
            var unrelated = terminalGoals[3];
            var first = GoalLifecycleCommands.CreateAndActivateSimpleGoal(seed, agents, "Depends on landed");
            var second = GoalLifecycleCommands.CreateAndActivateSimpleGoal(seed, agents, "Depends on unlanded");
            seed.SetGoalDependency(first.Id, landed.Id);
            seed.SetGoalDependency(second.Id, unlanded.Id);

            foreach (var goal in terminalGoals)
                GoalOperationJournal.Completed(root, goal, "test:terminal", "Terminal reload fixture.");
            GoalOperationJournal.RecordLandingIntent(root, landed, "goal/landed", "main", "abc123", "test");
            GoalOperationJournal.RecordLandingIntent(root, requested, "goal/requested", "main", "def456", "test");
            GoalOperationJournal.RecordTerminalDisposition(root, unlanded,
                new GoalTerminalDisposition(GoalTerminalDispositionKind.Retired, "Retired without landing."));

            var terminalIds = terminalGoals.Select(goal => goal.Id.Value).ToHashSet(StringComparer.Ordinal);
            var snapshot = seed.ExportSnapshot();
            await repository.SaveAsync(AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Select(goal => terminalIds.Contains(goal.Id)
                    ? goal with { Status = GoalStatus.Completed }
                    : goal).ToArray()
            }));

            string[] requestedIds = [requested.Id.Value];
            string[] expectedMetadataIds = [first.Id.Value, second.Id.Value, requested.Id.Value];
            var ledger = new ConductorTickStepLedger(() => TimeSpan.Zero);
            AgentOrchestratorKernel loaded;
            TerminalGoalJournalMetadataTiming journalTiming;
            using (ledger.Activate())
            {
                TerminalGoalJournalMetadataCache.BeginMeasurement();
                try
                {
                    loaded = ConductorTickStepLedger.Measure("kernel-reload", () =>
                        CliPersistentStateRunner.LoadConductLoopKernel(repository, requestedIds, root));
                }
                finally
                {
                    journalTiming = TerminalGoalJournalMetadataCache.CompleteMeasurement();
                }
            }

            var metadata = await repository.ListLiveConductLoopGoalMetadataAsync(requestedIds);
            Assert.Equal(expectedMetadataIds.Order(StringComparer.Ordinal),
                metadata.Select(row => row.Id).Order(StringComparer.Ordinal));
            Assert.Equal(expectedMetadataIds.Order(StringComparer.Ordinal),
                loaded.Goals.Select(goal => goal.Id.Value).Order(StringComparer.Ordinal));
            Assert.Contains("metadata_rows=3", ledger.FormatCounters().Split(','));
            Assert.Contains("terminal_journal_stats=2", ledger.FormatCounters().Split(','));
            Assert.Equal(2, journalTiming.JournalsRead);

            // Reconstruct the old full-scan result from the fixture store after the measured reload.
            var fullMetadata = await repository.ListConductLoopGoalMetadataAsync();
            Assert.Equal(terminalCount + 2, fullMetadata.Count);
            var reference = fullMetadata.Where(row => row.Status == nameof(GoalStatus.Completed))
                .ToDictionary(row => new GoalId(row.Id), row =>
                {
                    var journal = GoalOperationJournal.Read(root, new GoalId(row.Id));
                    Assert.NotEmpty(journal.LatestByOperation);
                    var isLanded = GoalOperationJournal.HasDurableLandingIntent(journal);
                    // Every terminal fixture row starts Completed, so the legacy rule can remap it to Retired.
                    var status = !isLanded && GoalOperationJournal.HasRetiredTerminalDisposition(journal)
                        ? "Retired"
                        : row.Status;
                    return (Status: status, IsLanded: isLanded);
                });
            Assert.Equal(terminalCount, reference.Count);
            Assert.Equal((nameof(GoalStatus.Completed), true), reference[landed.Id]);
            Assert.Equal(("Retired", false), reference[unlanded.Id]);
            Assert.True(reference[requested.Id].IsLanded);

            foreach (var dependency in new[] { landed.Id, unlanded.Id })
            {
                Assert.True(loaded.TryGetKnownDependencyGoalStatus(dependency, out var status));
                Assert.Equal(reference[dependency], (status, loaded.IsKnownCompletedDependencyGoal(dependency)));
            }
            Assert.True(loaded.TryGetKnownDependencyGoalStatus(requested.Id, out var requestedStatus));
            Assert.Equal(nameof(GoalStatus.Completed), requestedStatus);
            Assert.False(loaded.IsKnownCompletedDependencyGoal(requested.Id));
            Assert.False(loaded.TryGetKnownDependencyGoalStatus(unrelated.Id, out _));
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }
}
