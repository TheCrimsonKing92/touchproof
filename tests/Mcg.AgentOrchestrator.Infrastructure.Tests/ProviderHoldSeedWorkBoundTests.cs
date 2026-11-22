using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe. Each case owns its SQLite store and kernel.
public sealed class ProviderHoldSeedWorkBoundTests
{
    [Fact]
    public async Task Seed_PersistedHistory_PreservesSchedulesWithinFixedWorkBound()
    {
        using var fixture = new ProviderHoldSeedFixture();
        await fixture.PopulateAsync();
        Assert.Equal(3200, fixture.CountDispatchGoals());
        var full = await fixture.Repository.ListModelFitHistoryAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3200, full.Select(row => row.GoalId).Distinct().Count());
        Assert.Equal(new[] { 200, 200, 200, 100, 100 }, ProviderHoldSeedFixture.Rules.Select(
            rule => full.Count(row => row.OutcomeRule == rule)));

        var kernel = new AgentOrchestratorKernel();
        var result = Seed(fixture, kernel);
        Assert.Equal(800, result.Selection.Sources.Count);
        Assert.Contains(ProviderHoldSeedFixture.Id(0), result.Selection.Sources);
        Assert.Equal(ProviderHoldSeedFixture.At, full.Single(row => row.GoalId == ProviderHoldSeedFixture.Id(0)).Timestamp);
        Assert.Equal(new[] { 800, 801, 802 }.Select(ProviderHoldSeedFixture.Id),
            result.Selection.Dependencies.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(ProviderHoldSeedFixture.Id(803), result.Selection.Dependencies);
        Assert.Equal(803, result.Index.IndexedGoals);
        Assert.Equal(8, result.Index.Evaluations.Values.Sum(entry => entry.Count));
        Assert.InRange(result.Index.Evaluations.Values.Sum(entry => entry.ObservationVisits), 1, 3212);

        var keys = new[]
        {
            "anthropic::c:/profiles/old", "anthropic::c:/profiles/auth",
            "anthropic::c:/profiles/models|model=claude-sonnet-4-6",
            "anthropic::c:/profiles/models|model=claude-opus-4-6",
            "anthropic::claude-api:c:/profiles/old", "anthropic::c:/profiles/cleared",
            "anthropic::c:/profiles/superseded", "anthropic::c:/profiles/held"
        };
        Assert.Equal(keys.Order(StringComparer.Ordinal), result.Index.Evaluations.Keys.Order(StringComparer.Ordinal));
        var expectedSources = new[] { 0, 199, 299, 399, 499, -1, 699, 799 };
        for (var keyIndex = 0; keyIndex < keys.Length; keyIndex++)
        {
            var evaluation = result.Index.Evaluations[keys[keyIndex]];
            Assert.Equal(1, evaluation.Count);
            var source = expectedSources[keyIndex];
            if (source < 0)
            {
                Assert.Null(evaluation.Schedule);
                continue;
            }

            var schedule = Assert.IsType<ProviderBudgetHoldSchedule>(evaluation.Schedule);
            Assert.Equal(ProviderHoldSeedFixture.Id(source), schedule.Hold.SourceGoalId.Value);
            Assert.Equal(ProviderHoldSeedFixture.Id(source), schedule.Hold.SourceTaskId.Value);
            Assert.Equal($"receipt-{source}", schedule.Hold.EvidenceReceipt);
            Assert.Equal(keyIndex == 1 ? ProviderFailureKind.AuthenticationRequired : ProviderFailureKind.BudgetExhausted,
                schedule.Reason);
            Assert.Equal(keyIndex == 6 ? 1 : 100, schedule.Attempt);
            Assert.Equal(ProviderHoldSeedFixture.At.AddMinutes(keyIndex == 0 ? 0 : source)
                .AddMinutes(keyIndex == 6 ? 30 : 360), schedule.NextProbeAt);
        }

        Assert.Contains(kernel.ProviderHoldHistory.Retained, goal => goal.Id.Value == ProviderHoldSeedFixture.Id(0));
        Assert.Contains(kernel.ProviderHoldHistory.Retained, goal => goal.Id.Value == ProviderHoldSeedFixture.Id(801));
        Assert.DoesNotContain(kernel.ProviderHoldHistory.Retained, goal => goal.Id.Value == ProviderHoldSeedFixture.Id(599));
        Assert.DoesNotContain(kernel.ProviderHoldHistory.Retained, goal => goal.Id.Value == ProviderHoldSeedFixture.Id(698));
        Assert.Equal(result.Index.Evaluations.Values.Where(entry => entry.Schedule is not null)
                .Select(entry => entry.Schedule!.Hold.SourceGoalId.Value).Order(StringComparer.Ordinal),
            ProviderUsageHoldView.Read(kernel.ProviderHoldScope, ProviderHoldSeedFixture.At)
                .Select(row => row.Hold.SourceGoalId.Value).Order(StringComparer.Ordinal));
        var after = await fixture.Repository.ListModelFitHistoryAsync(TestContext.Current.CancellationToken);
        Assert.Equal(full.OrderBy(row => row.GoalId, StringComparer.Ordinal),
            after.OrderBy(row => row.GoalId, StringComparer.Ordinal));
        Assert.Equal(3200, fixture.CountDispatchGoals());
    }

    [Fact]
    public async Task Seed_MoreUnrelatedDispatches_LeavesIndexAndBindingWorkUnchanged()
    {
        using var fixture = new ProviderHoldSeedFixture();
        await fixture.PopulateAsync();
        var before = Seed(fixture, new());
        await fixture.AddUnrelatedAsync(3200);
        Assert.Equal(6400, fixture.CountDispatchGoals());
        var after = Seed(fixture, new());

        Assert.Equal(before.Selection.RowsRead, after.Selection.RowsRead);
        Assert.Equal(before.EvidenceGoals, after.EvidenceGoals);
        Assert.Equal(before.Index.IndexedGoals, after.Index.IndexedGoals);
        Assert.Equal(before.Index.RequiredGoals.Order(StringComparer.Ordinal), after.Index.RequiredGoals.Order(StringComparer.Ordinal));
        Assert.Equal(before.Index.Evaluations.OrderBy(entry => entry.Key), after.Index.Evaluations.OrderBy(entry => entry.Key));
    }

    [Fact]
    public async Task Seed_CrossGoalReset_KeepsMarkerWithoutPromotingItsGoalToSource()
    {
        using var fixture = new ProviderHoldSeedFixture();
        await fixture.PopulateResetCaseAsync();
        var kernel = new AgentOrchestratorKernel();
        var seeded = Seed(fixture, kernel);
        Assert.Equal(new[] { ProviderHoldSeedFixture.Id(799) }, seeded.Selection.Sources);
        Assert.Empty(seeded.Selection.Dependencies);
        Assert.Equal(2, seeded.EvidenceGoals);
        var schedule = Assert.IsType<ProviderBudgetHoldSchedule>(
            Assert.Single(seeded.Index.Evaluations).Value.Schedule);
        Assert.Equal("fixture-reset", schedule.ResetIntentId);
        Assert.Equal(ProviderHoldSeedFixture.At.AddDays(2), schedule.NextProbeAt);
        Assert.Equal(new[] { 799, 805 }.Select(ProviderHoldSeedFixture.Id),
            kernel.ProviderHoldHistory.Retained.Select(goal => goal.Id.Value));
    }

    private static ProviderHoldHistoryStartupSeed.Result Seed(ProviderHoldSeedFixture fixture, AgentOrchestratorKernel kernel)
    {
        var diagnostics = new List<string>();
        var result = ProviderHoldHistoryStartupSeed.Apply(kernel, fixture.Repository, diagnostics.Add);
        Assert.True(result is not null, string.Join(Environment.NewLine, diagnostics));
        return result!;
    }
}

internal sealed class ProviderHoldSeedFixture : IDisposable
{
    internal static readonly DateTimeOffset At = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
    internal static readonly string[] Rules =
    [
        "provider-budget-exhausted", "provider-authentication", "subscription-limit",
        "provider-rate-limit", "recoverable-subscription-limit"
    ];
    private static readonly string[] Credentials =
    [
        "old", "auth", "models", "models", "old", "cleared", "superseded", "held"
    ];
    private readonly string _root = Directory.CreateTempSubdirectory("provider-seed-").FullName;
    internal string Source { get; }
    internal string Scratch => Path.Combine(_root, "scratch");
    internal string Database { get; }
    internal SqliteOrchestratorStateRepository Repository { get; }

    internal ProviderHoldSeedFixture()
    {
        Source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        Database = Path.Combine(Source, "state.db");
        StateDbMigrations.EnsureUpToDate(Database);
        Repository = new(Database);
    }

    internal static string Id(int index) => $"{index:x8}{new string('0', 24)}";

    internal Task PopulateAsync() => SaveAsync(Enumerable.Range(0, 3200).Select(index => index switch
    {
        < 800 => MakeHold(index),
        800 => Make(index, At.AddHours(20), Credentials[5], success: true),
        801 => Make(index, At.AddMinutes(698).AddSeconds(30), Credentials[6], success: true),
        802 => Make(index, At.AddHours(21), "ignored", success: true),
        803 => Make(index, At.AddSeconds(-1), Credentials[0], success: true),
        _ => MakeUnrelated(index)
    }));

    internal Task AddUnrelatedAsync(int count) => SaveAsync(Enumerable.Range(3200, count).Select(MakeUnrelated));

    internal Task PopulateResetCaseAsync()
    {
        var goal = MakeUnrelated(805);
        var marker = ProviderBudgetHoldSchedules.FormatReset(new(
            "anthropic::c:/profiles/held", At.AddDays(2), "fixture-reset"));
        goal = goal with { Timeline = [new(goal.Id, goal.Tasks[0].Id, ProgressKind.TaskNote, marker, At.AddDays(1))] };
        return SaveAsync([MakeHold(799), goal]);
    }

    internal int CountDispatchGoals()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Database, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(DISTINCT goals.id) FROM goals
            JOIN goal_task_history dispatch ON dispatch.goal_id = goals.id AND dispatch.kind = 'dispatch'
            """;
        return checked((int)(long)command.ExecuteScalar()!);
    }

    private Task SaveAsync(IEnumerable<GoalSnapshot> snapshots) =>
        Repository.SaveGoalSnapshotsAsync(snapshots.ToArray(), TestContext.Current.CancellationToken);

    private static GoalSnapshot MakeHold(int index)
    {
        var key = index / 100;
        var rule = Rules[key % Rules.Length];
        var (stdout, stderr, model, kind) = key switch
        {
            1 => ("", "ERROR: 401 invalid credentials", "fixture-model", ProviderFailureKind.AuthenticationRequired),
            2 => ("You've hit your sonnet limit", "", "claude-sonnet-4-6", ProviderFailureKind.Unknown),
            3 => ("You've hit your opus limit", "", "claude-opus-4-6", ProviderFailureKind.Unknown),
            _ => ("", "ERROR: weekly usage limit reached", "fixture-model", ProviderFailureKind.BudgetExhausted)
        };
        return Make(index, At.AddMinutes(key == 0 ? 0 : index), Credentials[key], rule: rule, kind: kind,
            stdout: stdout, stderr: stderr, model: model, apiIdentity: key == 4 ? "claude-api:c:/profiles/old" : null);
    }

    private static GoalSnapshot MakeUnrelated(int index) =>
        Make(index, At.AddDays(15).AddSeconds(index), Credentials[7], stderr: "fixture configuration failed");

    private static GoalSnapshot Make(int index, DateTimeOffset completed, string credential, bool success = false,
        string? rule = null, ProviderFailureKind kind = ProviderFailureKind.Unknown, string stdout = "",
        string stderr = "", string model = "fixture-model", string? apiIdentity = null)
    {
        var id = Id(index);
        var started = completed.AddSeconds(-1);
        var dispatch = new TaskDispatchSnapshot("fixture", "fixture", ".", started, ProviderName: "Anthropic",
            ModelName: model, ClaudeCredentialSourceDirectory: $"C:/profiles/{credential}", ClaudeApiHoldIdentity: apiIdentity);
        var receipt = new TaskVerificationSnapshot("fixture", ".", success ? 0 : 1, stdout, stderr, completed,
            StandardErrorPath: $"receipt-{index}", DispatchStartedAt: started, DispatchAttemptAt: started,
            ProviderFailureKind: kind, CompletionVerdictVerifiedSuccess: success);
        var task = new TaskSnapshot(id, "Verify fixture", AgentRole.Tester,
            success ? WorkTaskStatus.Completed : WorkTaskStatus.Failed, "tester",
            null, receipt, [receipt], dispatch, null, DispatchHistory: [dispatch], WorkerBuildInvocations: []);
        var note = rule is null ? "Fixture outcome." : $"CLASSIFIER rule={rule}; outcome_class=environmental;";
        return new(id, "Fixture history", GoalStatus.Completed, [task], [new(id, id, ProgressKind.TaskNote, note, completed)]);
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
}
