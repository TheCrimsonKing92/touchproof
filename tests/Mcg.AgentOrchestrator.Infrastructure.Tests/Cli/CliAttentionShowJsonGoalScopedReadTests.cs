using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: each fixture owns its SQLite stores; console capture uses AsyncLocal routing.
public sealed class CliAttentionShowJsonGoalScopedReadTests : CliTaskQueryTestSupport
{
    private const string Target = "abcdef01aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Other = "eeee0001aaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 20, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GoalScopedShow_ManyGoalStore_ReadsOnlyMatchedGoalAndMatchesReference(bool explicitGoal)
    {
        using var fixture = await Fixture.CreateAsync();
        var expected = fixture.Reference("ABCDEF01");
        var queries = new CountingQueries(fixture.Queries);
        var store = new CountingStore(fixture.Store);
        var args = ShowArgs("ABCDEF01", explicitGoal);

        var actual = CaptureConsole(() => CliAttentionShowJsonQueryCommand.Execute(
            args, queries, fixture.Workspace, new FixedTime(), store)).TrimEnd('\r', '\n');

        Assert.Equal(new[] { Target }, Assert.Single(queries.Loads));
        Assert.Equal(1, queries.MetadataReads);
        Assert.Equal(0, store.WholeStoreReads);
        Assert.Equal(0, store.QueueReads);
        Assert.NotEmpty(store.GoalReads);
        Assert.All(store.GoalReads, id => Assert.Equal(Target, id));
        Assert.Equal(expected, actual);
        using var document = JsonDocument.Parse(actual);
        var payload = document.RootElement.GetProperty("payload");
        Assert.Equal(Target, payload.GetProperty("goalId").GetString());
        Assert.Single(payload.GetProperty("waits").EnumerateArray());
        Assert.Single(payload.GetProperty("amendmentRevisionPending").EnumerateArray());
        var items = payload.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(new[] { "Decision", "Clarification", "Verify" },
            items.Select(item => item.GetProperty("type").GetString()));
        Assert.All(items, item => Assert.Equal(Target, item.GetProperty("goalId").GetString()));
        Assert.Equal($"spec-clarification:{Target}:read-contract",
            items[1].GetProperty("clarificationId").GetString());
        Assert.False(payload.TryGetProperty("decisions", out _));
        Assert.False(payload.TryGetProperty("hiddenByReason", out _));

        // The reference hydrates the whole board, independently of the optimized metadata selection.
        foreach (var prefix in new[] { "missing", "eeee" })
        {
            var errorQueries = new CountingQueries(fixture.Queries);
            var errorStore = new CountingStore(fixture.Store);
            var referenceError = Record.Exception(() => fixture.Reference(prefix));
            var actualError = Record.Exception(() => CaptureConsole(() =>
                CliAttentionShowJsonQueryCommand.Execute(ShowArgs(prefix, explicitGoal), errorQueries,
                    fixture.Workspace, new FixedTime(), errorStore)));
            Assert.NotNull(referenceError);
            Assert.NotNull(actualError);
            Assert.Equal(referenceError.GetType(), actualError.GetType());
            Assert.Equal(referenceError.Message, actualError.Message);
            if (prefix == "missing")
            {
                Assert.IsType<KeyNotFoundException>(actualError);
                Assert.Equal("No goal found matching prefix 'missing'.", actualError.Message);
            }
            else
            {
                Assert.IsType<InvalidOperationException>(actualError);
                Assert.Equal("Goal prefix 'eeee' is ambiguous (2 matches).", actualError.Message);
            }
            Assert.Empty(errorQueries.Loads);
            Assert.Empty(errorStore.GoalReads);
            Assert.Equal(0, errorStore.WholeStoreReads);
            Assert.Equal(0, errorStore.QueueReads);
        }
    }

    [Fact]
    public async Task UnscopedShow_ManyGoalStore_LoadsEveryGoalAndReadsOwnerDecisions()
    {
        using var fixture = await Fixture.CreateAsync();
        var metadata = await fixture.Queries.ListGoalMetadataAsync();
        Assert.True(metadata.Count >= 64);
        var expectedDecisions = await OwnerDecisionReader.ReadAsync(
            OwnerQuestionReadModelFactory.ForWorkspace(fixture.Workspace));
        Assert.NotEmpty(expectedDecisions.Entries);
        var queries = new CountingQueries(fixture.Queries);
        var store = new CountingStore(fixture.Store);

        var actual = CaptureConsole(() => CliAttentionShowJsonQueryCommand.Execute(
            ["attention", "show", "--json"], queries, fixture.Workspace, new FixedTime(), store));

        Assert.Equal(metadata.Select(goal => goal.Id), Assert.Single(queries.Loads));
        Assert.Equal(1, queries.MetadataReads);
        Assert.Equal(1, store.WholeStoreReads);
        Assert.Equal(1, store.QueueReads);
        Assert.Empty(store.GoalReads);
        Assert.Equal(fixture.Reference(null), actual.TrimEnd('\r', '\n'));
        using var document = JsonDocument.Parse(actual);
        var payload = document.RootElement.GetProperty("payload");
        var entries = payload.GetProperty("decisions").EnumerateArray().ToArray();
        Assert.Equal(expectedDecisions.Entries.Select(entry => entry.ItemId),
            entries.Select(entry => entry.GetProperty("itemId").GetString()));
        Assert.Equal(expectedDecisions.Entries.Select(entry => entry.Text),
            entries.Select(entry => entry.GetProperty("text").GetString()));
        Assert.Equal(expectedDecisions.HiddenByReason.OrderBy(pair => pair.Key),
            payload.GetProperty("hiddenByReason").EnumerateObject()
                .Select(property => new KeyValuePair<string, int>(property.Name, property.Value.GetInt32()))
                .OrderBy(pair => pair.Key));
    }

    private static string[] ShowArgs(string prefix, bool explicitGoal) => explicitGoal
        ? ["attention", "show", "--goal", prefix, "--json"]
        : ["attention", "show", prefix, "--json"];

    private sealed class FixedTime : TimeProvider, IClock
    {
        public DateTimeOffset UtcNow => Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = CreateTempDirectory();
        public OrchestratorWorkspace Workspace { get; }
        public CollaborationItemStore Store { get; }
        public IOrchestratorStateQueries Queries => SqliteOrchestratorStateRepository.OpenReadOnly(Workspace.SqliteStatePath);

        private Fixture()
        {
            Workspace = OrchestratorWorkspace.ForDirectory(_root);
            StateDbMigrations.EnsureUpToDate(Workspace.SqliteStatePath);
            Store = CollaborationItemStore.ForDirectory(Workspace.OrchestratorDirectory);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            try
            {
                var kernel = new AgentOrchestratorKernel(new FixedTime());
                var planner = new TaskSpec(TaskId.New(), "Plan the scoped change", AgentRole.Planner);
                var target = kernel.CreateGoal(new GoalId(Target), "Target goal",
                    [planner, new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
                kernel.ActivateGoal(target.Id, AgentCatalog.Default().Agents);
                foreach (var id in new[] { Other, "eeee0002aaaaaaaaaaaaaaaaaaaaaaaa" }
                    .Concat(Enumerable.Range(1, 61).Select(index => $"{index:x8}bbbbbbbbbbbbbbbbbbbbbbbb")))
                {
                    var goal = kernel.CreateGoal(new GoalId(id), "Unrelated goal " + id);
                    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
                }
                kernel.RequestHumanInput(target.Id, null, "May we proceed with this goal?", kind: HumanWaitKind.Other);
                // An answered amendment without revision holds the waiting planner, alongside a live goal wait.
                var snapshot = kernel.ExportSnapshot();
                var amendment = new HumanInputRequestSnapshot("amendment-request", Target, planner.Id.Value,
                    "Revise criterion 1?", Now.AddMinutes(-2), HumanWaitKind.PlannerBriefAmendment,
                    IsCompleted: true, Answer: "Keep the behavior pending revision", AnsweredAt: Now.AddMinutes(-1),
                    AnswerHistory: [new("amendment-answer", "Keep the behavior pending revision", Now.AddMinutes(-1))],
                    Amendment: new(1, target.AuthoritativeBrief.Version));
                kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
                {
                    Goals = snapshot.Goals.Select(goal => goal.Id != Target ? goal : goal with
                    {
                        Status = GoalStatus.WaitingForHuman,
                        Tasks = goal.Tasks.Select(task => task.Id != planner.Id.Value ? task : task with
                            { Status = WorkTaskStatus.WaitingForHuman }).ToArray()
                    }).ToArray(),
                    HumanInputRequests = snapshot.HumanInputRequests.Append(amendment).ToArray()
                });
                Assert.NotNull(kernel.GetPlannerBriefAmendmentRevisionPending(target.Id));
                await new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath).SaveAsync(kernel);

                await fixture.AddItemAsync(CollaborationItemType.Decision, Other, "Unrelated decision", "other-decision", 0);
                await fixture.AddItemAsync(CollaborationItemType.Clarification, Other, "Unrelated topic",
                    $"spec-clarification:{Other}:read-contract", 1);
                await fixture.AddItemAsync(CollaborationItemType.Decision, null, "Global decision", "global-decision", 2);
                var resolved = await fixture.AddItemAsync(CollaborationItemType.Clarification, Target, "Old topic",
                    $"spec-clarification:{Target}:previous:read-contract", 3);
                Assert.True(await fixture.Store.TryResolveByIdAsync(resolved.Id, "Already answered"));
                await fixture.AddItemAsync(CollaborationItemType.Clarification, Target, "Current topic",
                    $"spec-clarification:{Target}:read-contract", 4);
                var delivered = await fixture.AddItemAsync(CollaborationItemType.Verify, Target, "Verify result", "target-verify", 5);
                Assert.True(await fixture.Store.TryMarkDeliveredAsync(delivered.CorrelationKey!));
                await fixture.AddItemAsync(CollaborationItemType.Decision, Target, "Target decision", "target-decision", 6);
                await fixture.AddItemAsync(CollaborationItemType.Notice, Target, "Notice excluded from attention", "target-notice", 7);
                var sequence = 8;
                foreach (var goal in kernel.Goals.Where(goal => goal.Id.Value != Target && goal.Id.Value != Other))
                    await fixture.AddItemAsync(CollaborationItemType.Decision, goal.Id.Value,
                        "Unrelated decision " + goal.Id.Value, "decision-" + goal.Id.Value, sequence++);
                Assert.Equal(64, (await fixture.Queries.ListGoalMetadataAsync()).Count);
                Assert.Equal(64, (await fixture.Store.ListAsync()).Where(item => item.GoalId is not null)
                    .Select(item => item.GoalId).Distinct().Count());
                Assert.True(File.Exists(Path.Combine(fixture.Workspace.OrchestratorDirectory, "collaboration-items.db")));
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        private async Task<CollaborationItem> AddItemAsync(CollaborationItemType type, string? goalId,
            string subject, string correlationKey, int sequence)
        {
            var item = await Store.RaiseAsync(type, goalId, subject, "Question: " + subject, correlationKey);
            // The store owns its clock. Pin timestamps in this fixture's database to make ordering deterministic.
            await using var connection = new SqliteConnection(
                $"Data Source={Path.Combine(Workspace.OrchestratorDirectory, "collaboration-items.db")};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE collaboration_items SET raised_at = $at WHERE id = $id";
            command.Parameters.AddWithValue("$at", Now.AddMinutes(-20).AddSeconds(sequence).ToString("O"));
            command.Parameters.AddWithValue("$id", item.Id);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
            return item;
        }

        public string Reference(string? prefix)
        {
            // Keep the pre-change whole-board computation independent of the measured command.
            var metadata = Queries.ListGoalMetadataAsync().GetAwaiter().GetResult();
            var kernel = Queries.LoadGoalsAsync(metadata.Select(goal => new GoalId(goal.Id)).ToArray()).GetAwaiter().GetResult();
            var goal = prefix is null ? null : CliCommandHandlers.ResolveAttentionGoal(kernel, prefix);
            var items = Store.GetAttentionQueueAsync().GetAwaiter().GetResult();
            var identityUniverse = Store.ListAsync().GetAwaiter().GetResult()
                .Where(CliCommandHandlers.IsSpecClarification)
                .Where(item => goal is null || string.Equals(item.GoalId, goal.Id.Value, StringComparison.OrdinalIgnoreCase)).ToArray();
            var waits = CliCommandHandlers.HumanWaitsForAttention(kernel, goal?.Id, includeHistory: false);
            items = CliCommandHandlers.CollaborationItemsForAttention(items, kernel, goal?.Id, includeHistory: false);
            var decisions = prefix is null
                ? OwnerDecisionReader.ReadAsync(OwnerQuestionReadModelFactory.ForWorkspace(Workspace)).GetAwaiter().GetResult()
                : null;
            return AttentionShowAgentReadView.Render(goal?.Id.Value, waits, items,
                item => CliCommandHandlers.IsSpecClarification(item)
                    ? CliCommandHandlers.ClarificationId(item, identityUniverse) : null,
                new FixedTime(), kernel.Goals, decisions,
                PlannerBriefAmendmentHoldAttentionView.Holds(kernel,
                    kernel.Goals.Where(candidate => goal is null || candidate.Id == goal.Id)));
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class CountingQueries(IOrchestratorStateQueries inner) : IOrchestratorStateQueries
    {
        public List<string[]> Loads { get; } = [];
        public int MetadataReads { get; private set; }
        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default)
        {
            MetadataReads++;
            return inner.ListGoalMetadataAsync(cancellationToken);
        }
        public Task<AgentOrchestratorKernel> LoadGoalsAsync(IReadOnlyCollection<GoalId> goalIds,
            CancellationToken cancellationToken = default)
        {
            Loads.Add(goalIds.Select(id => id.Value).ToArray());
            return inner.LoadGoalsAsync(goalIds, cancellationToken);
        }
        public Task<AgentOrchestratorKernel> LoadBoardSnapshotAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The JSON command must load goals through the counted seam.");
    }

    private sealed class CountingStore(ICollaborationItemStore inner) : ICollaborationItemStore
    {
        public int WholeStoreReads { get; private set; }
        public int QueueReads { get; private set; }
        public List<string> GoalReads { get; } = [];
        public Task<IReadOnlyList<CollaborationItem>> ListAsync(string? goalId = null,
            CancellationToken cancellationToken = default)
        {
            if (goalId is null) WholeStoreReads++;
            else GoalReads.Add(goalId);
            return inner.ListAsync(goalId, cancellationToken);
        }
        public Task<IReadOnlyList<CollaborationItem>> ListForGoalIdsAsync(IEnumerable<string> goalIds,
            CancellationToken cancellationToken = default)
        {
            var ids = goalIds.ToArray();
            GoalReads.AddRange(ids);
            return inner.ListForGoalIdsAsync(ids, cancellationToken);
        }
        public Task<IReadOnlyList<CollaborationItem>> GetAttentionQueueAsync(CancellationToken cancellationToken = default)
        {
            QueueReads++;
            return inner.GetAttentionQueueAsync(cancellationToken);
        }

        public Task<CollaborationItem> RaiseAsync(CollaborationItemType type, string? goalId, string subject,
            string body, string? correlationKey = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CollaborationItem> RaiseWithActionsAsync(CollaborationItemType type, string? goalId,
            string subject, string body, string correlationKey, IReadOnlyList<CollaborationActionBinding> actions,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryResolveAsync(string correlationKey, string resolution,
            CancellationToken cancellationToken = default, int? briefVersion = null) => throw new NotSupportedException();
        public Task<int> ResolveOpenForGoalAsync(string goalId, string resolution,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryMarkDeliveredAsync(string correlationKey,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CollaborationBoundAction>> ListActionsAsync(string correlationKey,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateRenderedContentHashAsync(IEnumerable<string> correlationKeys, string renderedContentHash,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CollaborationActionApplyResult> TryClaimActionAsync(string correlationKey, int actionIndex,
            string actorId, string interactionId, long? currentGoalStateVersion, DateTimeOffset decidedAt,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CollaborationDecisionAuditEntry> RecordRejectedDecisionAsync(string correlationKey, int? actionIndex,
            string actorId, string interactionId, string reason, DateTimeOffset decidedAt,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CollaborationDecisionAuditEntry>> ListDecisionAuditAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DecisionRequest> RaiseDecisionRequestAsync(DecisionRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DecisionState?> GetDecisionStateAsync(string requestId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DecisionRequest>> ListDecisionRequestsAsync(string? goalId = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<NotificationDelivery> RecordNotificationDeliveryAsync(NotificationDelivery delivery,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<NotificationDelivery>> ListNotificationDeliveriesAsync(string requestId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DecisionReceipt> RecordDecisionAsync(string requestId, string actorId, string channel,
            AuthorizationTier authenticationAssurance, long? expectedGoalStateVersion, DecisionResponse response,
            DateTimeOffset recordedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DecisionReceipt> RecordExpiredDefaultDispositionAsync(string requestId, DateTimeOffset expiredAt,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DecisionEffectApplyResult> TryApplyDecisionEffectAsync(string requestId, string decisionReceiptId,
            DecisionActionRef actionRef, long? currentGoalStateVersion, string result, DateTimeOffset appliedAt,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
