using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its Git repository, SQLite state and conduct log.
public sealed class WorkerUsageOutcomeTests
{
    private const string InputLine = "metric: worker-input-volume-per-session; role: Developer; direction: decrease; target: 10%; window: 20 sessions";
    private const string TurnsLine = "metric: worker-turns-per-session; role: Developer; direction: decrease; target: 10; window: 2 sessions";
    private static readonly DateTimeOffset LandedAt = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AdoptedAt = LandedAt.AddMinutes(30);

    [Fact]
    public void ParsesRoleAndSessions()
    {
        var brief = Brief(InputLine);
        Assert.True(CostGoalOutcomeMetric.Check(brief, costBacklogItem: true).Passed);
        var parsed = CostGoalOutcomeMetric.Parse(brief);
        Assert.Empty(parsed.Defects);
        Assert.Equal(new OutcomeMetricLine("worker-input-volume-per-session", "decrease", "10%", 20, Role: AgentRole.Developer),
            Assert.Single(parsed.Metrics));

        Assert.Equal(AgentRole.Developer, Assert.Single(CostGoalOutcomeMetric.Parse(
            Brief(InputLine.Replace("Developer", "developer"))).Metrics).Role);
        var legacy = Brief(InputLine.Replace("; role: Developer", "").Replace("20 sessions", "5 gates"));
        Assert.Equal(new OutcomeMetricLine("worker-input-volume-per-session", "decrease", "10%", 5),
            Assert.Single(CostGoalOutcomeMetric.Parse(legacy).Metrics));

        AssertInvalid("metric: selection-milliseconds; role: Developer; direction: decrease; target: 10%; window: 5 gates",
            "malformed or unexpected fields");
        AssertInvalid("metric: tick-step-milliseconds:board-fill; role: Developer; direction: decrease; target: 10%; window: 5 ticks",
            "malformed or unexpected fields");
    }

    [Theory]
    [InlineData("worker-input-volume-per-session", "Operator", "2 sessions", "invalid role")]
    [InlineData("worker-input-volume-per-session", "3", "2 sessions", "invalid role")]
    [InlineData("worker-input-volume-per-session", "", "2 sessions", "invalid role")]
    [InlineData("worker-input-volume-per-session", "Developer; role: Tester", "2 sessions", "malformed or unexpected fields")]
    [InlineData("worker-turns-per-session", "Developer", "0 sessions", "invalid window")]
    [InlineData("selection-milliseconds", null, "2 sessions", "invalid window")]
    [InlineData("tick-step-milliseconds:board-fill", null, "2 sessions", "invalid window")]
    public void RejectsInvalidWorkerSyntax(string metric, string? role, string window, string defect) =>
        AssertInvalid($"metric: {metric}; direction: decrease; target: 10%; window: {window}" +
            (role is null ? "" : $"; role: {role}"), defect);

    [Fact]
    public async Task SweepMeasuresDeveloperInput()
    {
        using var fixture = new Fixture();
        fixture.Dispatch(-7, input: 900);
        fixture.Dispatch(-5, input: 40);
        fixture.Dispatch(-2, input: 1234, receipt: Receipt(input: 0));
        fixture.Dispatch(-1, input: 4000, role: AgentRole.Tester);
        fixture.Dispatch(1, input: 800);
        fixture.Dispatch(3, input: 1234, receipt: Receipt(input: 24));
        fixture.Dispatch(5, input: 8);
        fixture.Dispatch(6, input: 3000, receipt: Receipt());
        fixture.Dispatch(7, input: 9000, role: AgentRole.Tester);
        fixture.Dispatch(60, input: 9900);
        await fixture.SaveDispatchesAsync();

        var goal = fixture.File(InputLine.Replace("20 sessions", "2 sessions"));
        fixture.Run(AdoptedAt.AddMinutes(1));

        var record = fixture.Read(goal);
        var result = Assert.Single(record.FinalVerdicts);
        Assert.Equal(AgentRole.Developer, result.Line.Role);
        Assert.Equal(2, result.Line.Window);
        Assert.Equal(CostGoalOutcomeState.Met, result.State);
        Assert.Equal(20d, result.Before);
        Assert.Equal(16d, result.After);
        Assert.Equal(2, result.BeforeSampleSize);
        Assert.Equal(2, result.AfterSampleSize);
        Assert.Equal(0, Assert.Single(record.EmittedLines));
    }

    [Fact]
    public async Task IncludesDispatchAtLanding()
    {
        using var fixture = new Fixture();
        fixture.Dispatch(-2, input: 40);
        fixture.Dispatch(-1, input: 20);
        fixture.Dispatch(0, input: 12);
        fixture.Dispatch(1, input: 4, role: AgentRole.Tester);
        await fixture.SaveDispatchesAsync();

        var goal = fixture.File(InputLine.Replace("20 sessions", "2 sessions").Replace("; role: Developer", ""));
        fixture.Run(AdoptedAt.AddMinutes(1));

        var result = Assert.Single(fixture.Read(goal).FinalVerdicts);
        Assert.Null(result.Line.Role);
        Assert.Equal(CostGoalOutcomeState.Met, result.State);
        Assert.Equal(30d, result.Before);
        Assert.Equal(8d, result.After);
        Assert.Equal(2, result.BeforeSampleSize);
        Assert.Equal(2, result.AfterSampleSize);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ShortTurnsWindowReportsInsufficientSamples(bool shortBaseline)
    {
        using var fixture = new Fixture();
        DispatchSide(-2, -1, shortBaseline);
        DispatchSide(1, 2, !shortBaseline);
        await fixture.SaveDispatchesAsync();

        var goal = fixture.File(TurnsLine);
        var baseline = Assert.Single(fixture.Read(goal).Filing.Baselines);
        Assert.Equal("awaiting-integration", baseline.Reason);
        Assert.Null(baseline.Value);
        Assert.Equal(0, baseline.SampleSize);

        if (!shortBaseline)
        {
            fixture.Run(AdoptedAt.AddMinutes(1));
            var record = fixture.Read(goal);
            Assert.Empty(record.FinalVerdicts);
            var pending = Assert.Single(record.PendingResults);
            Assert.Equal(CostGoalOutcomeState.Pending, pending.State);
            Assert.Equal("insufficient-samples", pending.Reason);
            Assert.Equal(1, pending.AfterSampleSize);
        }

        fixture.Run(AdoptedAt.AddDays(7));

        var result = Assert.Single(fixture.Read(goal).FinalVerdicts);
        Assert.Equal(CostGoalOutcomeState.InsufficientData, result.State);
        Assert.Equal("insufficient-samples", result.Reason);
        Assert.Equal(shortBaseline ? 1 : 2, result.BeforeSampleSize);
        Assert.Equal(shortBaseline ? 2 : 1, result.AfterSampleSize);
        Assert.Equal(shortBaseline ? (double?)null : 2d, result.Before);
        Assert.Equal(shortBaseline ? 2d : (double?)null, result.After);

        void DispatchSide(int first, int second, bool shortWindow)
        {
            fixture.Dispatch(first, turns: shortWindow ? 99 : 4, receipt: shortWindow ? Receipt() : null);
            fixture.Dispatch(second, turns: 99, receipt: Receipt(turns: 0));
        }
    }

    private static string Brief(string line) => $"## Outcome metric\n{line}";

    private static void AssertInvalid(string line, string defect)
    {
        var brief = Brief(line);
        var check = CostGoalOutcomeMetric.Check(brief, costBacklogItem: true);
        Assert.Equal("cost-goal-outcome-metric", check.Name);
        Assert.False(check.Passed);
        Assert.Contains(defect, check.Detail);
        Assert.Empty(CostGoalOutcomeMetric.Parse(brief).Metrics);
    }

    private static WorkerContextPackageReceipt Receipt(long? input = null, long? turns = null) =>
        new("test", [], Usage(input), Usage(null), Usage(null)) { Turns = Usage(turns) };

    private static ProviderUsageValue Usage(long? value) => value is { } reported
        ? ProviderUsageValue.Reported(reported) : ProviderUsageValue.Unknown("provider omitted usage");

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Directory.CreateTempSubdirectory("worker-usage-outcome-").FullName;
        private readonly AgentOrchestratorKernel kernel = new();
        private readonly Goal history;
        private readonly OrchestratorWorkspace workspace;
        private readonly CostGoalOutcomeStore outcomes;
        private readonly CostGoalOutcomeSweep sweep;
        private readonly List<string> failures = [];
        private readonly string landedCommit;

        internal Fixture()
        {
            workspace = OrchestratorWorkspace.ForDirectory(root, root, null,
                new OrchestratorProjectRegistry(Path.Combine(root, "registry"), root));
            outcomes = new(workspace.OrchestratorDirectory);
            sweep = CostGoalOutcomeSweep.CreateDefault(workspace, failures.Add);
            history = kernel.CreateGoal("Fixture dispatch history", [
                new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer),
                new TaskSpec(TaskId.New(), "Test", AgentRole.Tester)]);
            kernel.ActivateGoal(history.Id, AgentCatalog.Default().Agents);

            Git("init", "--quiet");
            var commit = GitCli.RunWithEnvironment(root, new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = "Test",
                ["GIT_AUTHOR_EMAIL"] = "test@example.invalid",
                ["GIT_AUTHOR_DATE"] = "2026-10-15T11:30:00+00:00",
                ["GIT_COMMITTER_NAME"] = "Test",
                ["GIT_COMMITTER_EMAIL"] = "test@example.invalid",
                ["GIT_COMMITTER_DATE"] = "2026-10-15T14:00:00+02:00"
            }, "-c", "commit.gpgsign=false", "-c", "core.hooksPath=.git/no-hooks", "commit", "--allow-empty", "-m", "Landed fixture");
            Assert.True(commit.ExitCode == 0, commit.Error);
            landedCommit = Git("rev-parse", "HEAD");
            new ConductEventLogWriter(workspace.ConductEventsLogPath).Append(
                "loop-handoff", null, $"FLEET_BUILD_ADOPTED commit={landedCommit} adopted=home", AdoptedAt);
        }

        internal void Dispatch(int minute, long? input = null, long? turns = null,
            WorkerContextPackageReceipt? receipt = null, AgentRole role = AgentRole.Developer)
        {
            var task = history.Tasks.Single(task => task.RequiredRole == role);
            var usage = new DispatchProviderUsage(Usage(input), Usage(null), Usage(null)) { Turns = Usage(turns) };
            kernel.RecordTaskDispatch(history.Id, task.Id, new TaskDispatchRecord("worker", "fixture command", root,
                LandedAt.AddMinutes(minute), ProviderSessionId: "reused-provider-handle",
                ContextPackageReceipt: receipt, ProviderUsage: usage), allowPendingRecordedDispatchRefresh: true);
        }

        internal Task SaveDispatchesAsync() => InfrastructureTestSupport.CreateMigratedStateRepository(workspace.SqliteStatePath)
            .SaveAsync(kernel);

        internal GoalId File(string declaration)
        {
            var goal = GoalId.New();
            var filing = CostGoalOutcomeMetric.CaptureAtFiling(null, Brief(declaration), null, workspace, null);
            Assert.NotNull(filing);
            outcomes.RecordFiling(goal, filing with { FiledAt = LandedAt.AddHours(-1) });
            new MultiRepositoryLandingRecordStore(workspace.OrchestratorDirectory).Write(new MultiRepositoryLandingRecord(
                goal, "fixture", new Dictionary<string, string> { ["default"] = landedCommit },
                new Dictionary<string, string>(), ["default"], LandedAt.AddMinutes(15)));
            return goal;
        }

        internal CostGoalOutcomeRecord Read(GoalId goal) => outcomes.Read(goal)!;

        internal void Run(DateTimeOffset evaluatedAt)
        {
            sweep.Run(() => evaluatedAt);
            Assert.Empty(failures);
        }

        private string Git(params string[] args)
        {
            var result = GitCli.RunForRepository(root, args);
            Assert.True(result.ExitCode == 0, result.Error);
            return result.Output.Trim();
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(root);
    }
}
