using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each test owns its SQLite workspace; console capture uses the async-local router.
public sealed class OwnerQuestionHoldAttentionViewTests
{
    private static readonly DateTimeOffset Started = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private const string ItemId = "11111111111111111111111111111111";
    private const string Question = "Which runtime should we use?";
    private const string Recommendation = "Use Windows";

    [Theory]
    [InlineData("attention list", true)]
    [InlineData("attention list", false)]
    [InlineData("attention show", true)]
    [InlineData("attention show", false)]
    [InlineData("attention show selected", true)]
    [InlineData("attention show selected --all", true)]
    public async Task TextRoutes_PrintHoldOnlyGoal(string command, bool supportsProjection)
    {
        using var fixture = new Fixture();
        await fixture.Save();
        Assert.Empty(await fixture.Repository.ListOpenHumanInputRequestsAsync());
        Assert.Empty(await fixture.Collaboration.GetAttentionQueueAsync());
        var queries = new Queries(fixture.Repository, supportsProjection);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var args = command.Replace("selected", fixture.Goal.Id.Value, StringComparison.Ordinal).Split(' ');

        var output = AsyncLocalConsoleRouter.Capture(() => Assert.True(CliAttentionQueryCommand.TryExecute(
            args, queries, fixture.Workspace, new InMemoryModelProviderRegistry([]),
            ref agents, ref profiles, ref currentGoal)));

        Assert.Contains($"[author-owner-question] goal={fixture.Goal.Id.Value[..8]} item={ItemId}", output);
        Assert.Contains($"question: {Question}", output);
        Assert.Contains($"recommendation: {Recommendation}", output);
        Assert.Contains($"started={Started.ToString("u", CultureInfo.InvariantCulture)} held=", output);
        Assert.Contains($"answer: attention answer {fixture.Goal.Id.Value[..8]} {ItemId} --text-file <path>", output);
        Assert.DoesNotContain("No open attention items", output);
        Assert.Equal(new[] { fixture.Goal.Id }, queries.LoadCalls[^1]);
        Assert.Equal(supportsProjection || command.Contains("selected", StringComparison.Ordinal) ? 1 : 2, queries.LoadCalls.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JsonRoutes_ProjectFullAuthorFields(bool show)
    {
        using var fixture = new Fixture();
        await fixture.Save();
        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            if (show)
                CliAttentionShowJsonQueryCommand.Execute(["attention", "show", fixture.Goal.Id.Value, "--json"], fixture.Repository, fixture.Workspace);
            else
                CliAttentionJsonQueryCommand.Execute(["attention", "list", "--json"], fixture.Repository, fixture.Workspace);
        });
        using var json = JsonDocument.Parse(output);
        var row = Assert.Single(json.RootElement.GetProperty("payload").GetProperty("ownerQuestionHolds").EnumerateArray());
        Assert.Equal(fixture.Goal.Id.Value, row.GetProperty("goalId").GetString());
        Assert.Equal(ItemId, row.GetProperty("itemId").GetString());
        Assert.Equal(Recommendation, row.GetProperty("recommendation").GetString());
        Assert.Equal(Question, row.GetProperty("question").GetString());
        Assert.Equal(Started.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), row.GetProperty("startedAt").GetString());
    }

    [Theory]
    [InlineData(-60, "0")]
    [InlineData(5400, "5400")]
    public void TextView_UsesHoldStartAndClampsElapsedTime(int elapsedSeconds, string expectedSeconds)
    {
        using var fixture = new Fixture();
        using var output = new StringWriter();
        Assert.True(OwnerQuestionHoldAttentionView.Print([fixture.Goal], Started.AddSeconds(elapsedSeconds), output));
        Assert.Contains($"started={Started.ToString("u", CultureInfo.InvariantCulture)} held={expectedSeconds}s", output.ToString());
    }

    [Fact]
    public async Task Selection_EmptyProjectionDoesNotFallBackToLoadedHold()
    {
        using var fixture = new Fixture();
        await fixture.Save();
        var projection = Assert.IsType<AttentionReadProjection>(await fixture.Repository.ReadAttentionProjectionAsync());
        Assert.Single(projection.OwnerQuestions);
        var queries = new Queries(fixture.Repository, true);
        var selected = AttentionReadGoalSelection.ReadPostLandingDutyGoalIds(queries,
            await fixture.Repository.ListGoalMetadataAsync(), projection with { OwnerQuestions = [] });
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<GoalId>>(selected));
        Assert.Empty(queries.LoadCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Routes_ReconciledClarificationRemovesHold(bool supportsProjection)
    {
        using var fixture = new Fixture();
        var item = await fixture.Collaboration.RaiseAsync(CollaborationItemType.Clarification,
            fixture.Goal.Id.Value, "Runtime", Question, $"spec-clarification:{fixture.Goal.Id.Value}:runtime");
        fixture.Kernel.ClearGoalHold(fixture.Goal.Id);
        fixture.RaiseHold(item.Id);
        await fixture.Save();

        foreach (var command in new[] { "attention list", "attention show", "attention show selected" })
            Assert.Contains($"item={item.Id}", fixture.ReadText(command, supportsProjection));
        foreach (var show in new[] { false, true })
        {
            using var before = fixture.ReadJson(show);
            var row = Assert.Single(before.RootElement.GetProperty("payload").GetProperty("ownerQuestionHolds").EnumerateArray());
            Assert.Equal(item.Id, row.GetProperty("itemId").GetString());
        }

        Assert.Equal(1, await fixture.Collaboration.ResolveOpenForGoalRaisedAtOrBeforeAsync(
            fixture.Goal.Id.Value, "Use Windows", DateTimeOffset.MaxValue));
        var resolved = Assert.Single(await fixture.Collaboration.ListAsync(fixture.Goal.Id.Value));
        Assert.Equal(item.Id, resolved.Id);
        Assert.True(CollaborationItemLifecycle.IsTerminal(resolved.Status));
        Assert.NotNull(fixture.Goal.CurrentHold);
        var changed = new HashSet<GoalId>();
        var conduct = new ConductEventLogWriter(fixture.Workspace.ConductEventsLogPath, utcNow: () => Started);
        AuthorOwnerQuestionHoldReconciler.Reconcile(fixture.Kernel, fixture.Collaboration, conduct, null, changed);
        Assert.Equal(fixture.Goal.Id, Assert.Single(changed));
        Assert.Null(fixture.Goal.CurrentHold);
        await fixture.Save();
        Assert.Empty(await fixture.Repository.ListOpenHumanInputRequestsAsync());
        Assert.Empty(await fixture.Collaboration.GetAttentionQueueAsync());

        foreach (var command in new[] { "attention list", "attention show", "attention show selected" })
        {
            var output = fixture.ReadText(command, supportsProjection);
            Assert.DoesNotContain(item.Id, output);
            Assert.DoesNotContain(Question, output);
            Assert.DoesNotContain(Recommendation, output);
            Assert.DoesNotContain("held=", output);
            Assert.DoesNotContain("answer:", output);
            if (command.EndsWith("selected", StringComparison.Ordinal))
                Assert.Contains($"No open attention items for goal {fixture.Goal.Id.Value}.", output);
            else
                Assert.DoesNotContain(fixture.Goal.Id.Value[..8], output);
        }
        foreach (var show in new[] { false, true })
        {
            using var after = fixture.ReadJson(show);
            Assert.Empty(after.RootElement.GetProperty("payload").GetProperty("ownerQuestionHolds").EnumerateArray());
        }
    }

    [Theory]
    [InlineData("no-hold", true)]
    [InlineData("no-hold", false)]
    [InlineData("completed", true)]
    [InlineData("completed", false)]
    [InlineData("malformed", true)]
    [InlineData("malformed", false)]
    public async Task Routes_NegativeControlsNeverInventItemId(string control, bool supportsProjection)
    {
        const string malformed = "author-owner-question missing item payload";
        using var fixture = new Fixture(control == "completed" ? GoalStatus.Completed : GoalStatus.Active);
        if (control == "no-hold") fixture.Kernel.ClearGoalHold(fixture.Goal.Id);
        if (control == "malformed")
        {
            fixture.Kernel.ClearGoalHold(fixture.Goal.Id);
            fixture.Kernel.ObserveGoalHold(fixture.Goal.Id, "author-owner-question", malformed,
                Started, TimeSpan.MaxValue, "malformed-author-hold");
        }
        if (control == "completed")
        {
            Assert.Equal(GoalStatus.Completed, fixture.Goal.Status);
            Assert.Equal("author-owner-question", Assert.IsType<GoalHoldState>(fixture.Goal.CurrentHold).State);
            Assert.Contains(ItemId, fixture.Goal.CurrentHold!.Blocker);
        }
        else if (control == "no-hold") Assert.Null(fixture.Goal.CurrentHold);
        else Assert.Equal(malformed, fixture.Goal.CurrentHold!.Blocker);
        await fixture.Save();
        Assert.Empty(await fixture.Repository.ListOpenHumanInputRequestsAsync());
        Assert.Empty(await fixture.Collaboration.GetAttentionQueueAsync());

        foreach (var command in new[] { "attention list", "attention show", "attention show selected" })
        {
            var output = fixture.ReadText(command, supportsProjection);
            Assert.DoesNotContain(ItemId, output);
            Assert.DoesNotContain(" item=", output);
            Assert.DoesNotContain("recommendation:", output);
            if (control == "malformed")
            {
                Assert.Contains($"[author-owner-question] goal={fixture.Goal.Id.Value[..8]}", output);
                Assert.Contains($"question: {malformed}", output);
                Assert.Contains($"answer: attention show {fixture.Goal.Id.Value[..8]}", output);
                Assert.DoesNotContain("No open attention items", output);
            }
            else
            {
                Assert.DoesNotContain("[author-owner-question]", output);
                if (command.EndsWith("selected", StringComparison.Ordinal))
                    Assert.Contains($"No open attention items for goal {fixture.Goal.Id.Value}.", output);
                else
                    Assert.DoesNotContain(fixture.Goal.Id.Value[..8], output);
            }
        }
        foreach (var show in new[] { false, true })
        {
            using var json = fixture.ReadJson(show);
            var rows = json.RootElement.GetProperty("payload").GetProperty("ownerQuestionHolds").EnumerateArray();
            if (control != "malformed") Assert.Empty(rows);
            else
            {
                var row = Assert.Single(rows);
                Assert.Equal(fixture.Goal.Id.Value, row.GetProperty("goalId").GetString());
                Assert.Equal(malformed, row.GetProperty("question").GetString());
                Assert.Equal(JsonValueKind.Null, row.GetProperty("itemId").ValueKind);
                Assert.Equal(JsonValueKind.Null, row.GetProperty("recommendation").ValueKind);
            }
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("owner-hold-attention-");
        internal OrchestratorWorkspace Workspace { get; }
        internal CollaborationItemStore Collaboration { get; }
        internal SqliteOrchestratorStateRepository Repository { get; }
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal Goal Goal { get; }

        internal Fixture(GoalStatus status = GoalStatus.Active)
        {
            Workspace = OrchestratorWorkspace.ForDirectory(_directory.FullName);
            Collaboration = CollaborationItemStore.ForDirectory(Workspace.OrchestratorDirectory);
            StateDbMigrations.EnsureUpToDate(Workspace.SqliteStatePath);
            Repository = new SqliteOrchestratorStateRepository(Workspace.SqliteStatePath);
            Goal = Kernel.CreateGoal("Owner hold only", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            Kernel.ActivateGoal(Goal.Id, AgentCatalog.Default().Agents);
            RaiseHold(ItemId);
            if (status != GoalStatus.Active)
            {
                var snapshot = Kernel.ExportSnapshot();
                Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
                {
                    Goals = snapshot.Goals.Select(goal => goal with { Status = status }).ToArray()
                });
                Goal = Assert.Single(Kernel.Goals);
            }
        }

        internal void RaiseHold(string itemId) =>
            Kernel.ObserveGoalHold(Goal.Id, "author-owner-question",
                $"author-owner-question item=Clarification:{itemId} reason=model-ask-owner question={Question} recommendation={Recommendation}",
                Started, TimeSpan.MaxValue, "author-hold-fixture");

        internal string ReadText(string command, bool supportsProjection)
        {
            var queries = new Queries(Repository, supportsProjection);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var args = command.Replace("selected", Goal.Id.Value, StringComparison.Ordinal).Split(' ');
            return AsyncLocalConsoleRouter.Capture(() => Assert.True(CliAttentionQueryCommand.TryExecute(
                args, queries, Workspace, new InMemoryModelProviderRegistry([]),
                ref agents, ref profiles, ref currentGoal)));
        }

        internal JsonDocument ReadJson(bool show) => JsonDocument.Parse(AsyncLocalConsoleRouter.Capture(() =>
        {
            if (show)
                CliAttentionShowJsonQueryCommand.Execute(["attention", "show", Goal.Id.Value, "--json"], Repository, Workspace);
            else
                CliAttentionJsonQueryCommand.Execute(["attention", "list", "--json"], Repository, Workspace);
        }));

        internal Task Save() => Repository.SaveAsync(Kernel);
        public void Dispose() => _directory.Delete(recursive: true);
    }

    private sealed class Queries(IOrchestratorStateQueries inner, bool supportsProjection) : IOrchestratorStateQueries
    {
        internal List<GoalId[]> LoadCalls { get; } = [];
        public Task<AttentionReadProjection?> ReadAttentionProjectionAsync(CancellationToken cancellationToken = default) =>
            supportsProjection ? inner.ReadAttentionProjectionAsync(cancellationToken) : Task.FromResult<AttentionReadProjection?>(null);
        public Task<AttentionReadProjection?> ReadAttentionProjectionAsync(AttentionProjectionReadOptions options, CancellationToken cancellationToken) =>
            supportsProjection ? inner.ReadAttentionProjectionAsync(options, cancellationToken) : Task.FromResult<AttentionReadProjection?>(null);
        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default) =>
            inner.ListGoalMetadataAsync(cancellationToken);
        public Task<IReadOnlyList<HumanInputRequestSnapshot>> ListOpenHumanInputRequestsAsync(CancellationToken cancellationToken = default) =>
            inner.ListOpenHumanInputRequestsAsync(cancellationToken);
        public Task<IReadOnlyList<GoalId>> ListGateTestObligationGoalIdsAsync(IReadOnlyCollection<GoalId>? goalIds = null,
            CancellationToken cancellationToken = default) => inner.ListGateTestObligationGoalIdsAsync(goalIds, cancellationToken);
        public Task<AgentOrchestratorKernel> LoadGoalsAsync(IReadOnlyCollection<GoalId> goalIds, CancellationToken cancellationToken = default)
        {
            LoadCalls.Add(goalIds.ToArray());
            return inner.LoadGoalsAsync(goalIds, cancellationToken);
        }
    }
}
