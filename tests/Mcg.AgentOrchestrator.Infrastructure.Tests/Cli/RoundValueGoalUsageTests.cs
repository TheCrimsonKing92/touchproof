using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: snapshot-only state, fixed dates, isolated roots and AsyncLocal console capture.
public sealed class RoundValueGoalUsageTests : CliTaskQueryTestSupport
{
    private const string GoalA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string GoalB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string GoalC = "cccccccccccccccccccccccccccccccc";
    private static readonly DateTimeOffset Since = DateTimeOffset.Parse("2026-09-24T00:00:00Z");
    private static readonly DateTimeOffset Until = Since.AddDays(2);
    private static readonly string[] WindowArgs =
        ["round-value", "--since", "2026-09-24T00:00:00Z", "--until", "2026-09-26T00:00:00Z"];

    [Fact]
    public void Build_DeveloperAndReviewerRounds_SumsPairsAndCountsRetriesWithinEachTask()
    {
        var kernel = Kernel(Goal(GoalA, GoalStatus.Completed,
            Task("dev", AgentRole.Developer, "m-dev", Reported(100, 60, 10)),
            Task("review", AgentRole.Reviewer, "m-rev", Reported(200, 120, 20), Reported(300, 180, 30))));
        var slice = Build(kernel);
        var row = Assert.Single(slice.Rows);
        Assert.Equal(GoalA, row.GoalId);
        Assert.Equal("landed", row.Outcome);
        Assert.Equal((3, 1, 2, 0), (row.Dispatches, row.RetryRounds, row.ReviewRounds, row.UnknownDispatches));
        Assert.Equal((600L, 360L, 60L), (row.InputTokens, row.CachedInputTokens, row.OutputTokens));
        Assert.Equal(new RoundValueGoalUsageRoleModel("Developer", "m-dev", 1, 0, 100, 60, 10), row.RoleModels[0]);
        Assert.Equal(new RoundValueGoalUsageRoleModel("Reviewer", "m-rev", 2, 0, 500, 300, 50), row.RoleModels[1]);
        Assert.Equal(2, row.RoleModels.Count);
        Assert.Equal(new RoundValueGoalUsageFigures(600, 360, 60, 3, 1, 2, 0), slice.Landed.Sums);
        Assert.Equal(new RoundValueGoalUsageMeans(600, 360, 60, 3, 1, 2, 0), slice.Landed.Means);

        WithRepository(kernel, (repository, root) =>
        {
            var text = Run(repository, root, [.. WindowArgs, "--by-goal"]);
            Assert.Contains($"{GoalA} | landed | 3 | 1 | 2 | 0 | 600 | 360 | 60", text);
            using var json = JsonDocument.Parse(Run(repository, root, [.. WindowArgs, "--by-goal", "--json"]));
            var report = json.RootElement;
            Assert.Equal(["since", "until", "goal", "rows", "landed"], report.EnumerateObject().Select(p => p.Name));
            Assert.Equal(Since, report.GetProperty("since").GetDateTimeOffset());
            Assert.Equal(Until, report.GetProperty("until").GetDateTimeOffset());
            Assert.Equal(JsonValueKind.Null, report.GetProperty("goal").ValueKind);
            var jsonRow = Assert.Single(report.GetProperty("rows").EnumerateArray());
            Assert.Equal("landed", jsonRow.GetProperty("outcome").GetString());
            Assert.Equal(3, jsonRow.GetProperty("dispatches").GetInt32());
            Assert.Equal(1, jsonRow.GetProperty("retryRounds").GetInt32());
            Assert.Equal(2, jsonRow.GetProperty("reviewRounds").GetInt32());
            AssertTokens(jsonRow, 600, 360, 60);
            var pairs = jsonRow.GetProperty("roleModels").EnumerateArray().ToArray();
            Assert.Equal(2, pairs.Length);
            Assert.Equal("Reviewer", pairs[1].GetProperty("role").GetString());
            Assert.Equal("m-rev", pairs[1].GetProperty("model").GetString());
            AssertTokens(pairs[1], 500, 300, 50);
            Assert.Contains("Reviewer | m-rev | 2 | 0 | 500 | 300 | 50",
                Run(repository, root, [.. WindowArgs, "--by-goal", "--goal", "aaaa"]));
            Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify([.. WindowArgs, "--by-goal"]));
        });
    }

    [Fact]
    public void Build_UnknownAndReportedSamePair_KeepsPartialTotalsAndUnknownCount()
    {
        var kernel = Kernel(Goal(GoalA, GoalStatus.Completed,
            Task("dev", AgentRole.Developer, "model", Unknown(), Reported(7, 3, 2))));
        var row = Assert.Single(Build(kernel).Rows);
        Assert.Equal((2, 1), (row.Dispatches, row.UnknownDispatches));
        Assert.Equal((7L, 3L, 2L), (row.InputTokens, row.CachedInputTokens, row.OutputTokens));
        Assert.Equal(new RoundValueGoalUsageRoleModel("Developer", "model", 2, 1, 7, 3, 2),
            Assert.Single(row.RoleModels));
        WithRepository(kernel, (repository, root) =>
        {
            using var json = JsonDocument.Parse(Run(repository, root, [.. WindowArgs, "--by-goal", "--json"]));
            var jsonRow = Assert.Single(json.RootElement.GetProperty("rows").EnumerateArray());
            Assert.Equal(1, jsonRow.GetProperty("unknownDispatches").GetInt32());
            var pair = Assert.Single(jsonRow.GetProperty("roleModels").EnumerateArray());
            Assert.Equal(1, pair.GetProperty("unknownDispatches").GetInt32());
            AssertTokens(pair, 7, 3, 2);
        });
    }

    [Fact]
    public void Build_AllUsageUnknown_PreservesNullsInGoalPairWindowAndMeans()
    {
        var kernel = Kernel(Goal(GoalA, GoalStatus.Completed,
            Task("dev", AgentRole.Developer, "model", Unknown(), Unknown())));
        var slice = Build(kernel);
        var row = Assert.Single(slice.Rows);
        Assert.Equal(2, row.UnknownDispatches);
        Assert.Null(row.InputTokens);
        Assert.Null(row.CachedInputTokens);
        Assert.Null(row.OutputTokens);
        Assert.Equal(new RoundValueGoalUsageRoleModel("Developer", "model", 2, 2, null, null, null),
            Assert.Single(row.RoleModels));
        Assert.Equal(0, slice.Landed.EligibleGoals);
        Assert.Equal(new RoundValueGoalUsageFigures(null, null, null, 2, 1, 0, 2), slice.Landed.Sums);
        Assert.Equal(new RoundValueGoalUsageMeans(null, null, null, null, null, null, null), slice.Landed.Means);
        WithRepository(kernel, (repository, root) =>
        {
            Assert.Contains($"{GoalA} | landed | 2 | 1 | 0 | 2 | unknown | unknown | unknown",
                Run(repository, root, [.. WindowArgs, "--by-goal"]));
            Assert.Contains("Developer | model | 2 | 2 | unknown | unknown | unknown",
                Run(repository, root, [.. WindowArgs, "--by-goal", "--goal", "aaaa"]));
            using var json = JsonDocument.Parse(Run(repository, root, [.. WindowArgs, "--by-goal", "--json"]));
            var jsonRow = Assert.Single(json.RootElement.GetProperty("rows").EnumerateArray());
            AssertTokens(jsonRow, null, null, null);
            AssertTokens(Assert.Single(jsonRow.GetProperty("roleModels").EnumerateArray()), null, null, null);
            AssertTokens(json.RootElement.GetProperty("landed").GetProperty("sums"), null, null, null);
            Assert.All(json.RootElement.GetProperty("landed").GetProperty("means").EnumerateObject(),
                p => Assert.Equal(JsonValueKind.Null, p.Value.ValueKind));
        });
    }

    [Fact]
    public void Build_PartialUsage_SumsComponentsButDoesNotQualifyGoalForMeans()
    {
        var partial = new DispatchProviderUsage(ProviderUsageValue.Reported(5),
            ProviderUsageValue.Unknown("fixture"), ProviderUsageValue.Reported(0));
        var kernel = Kernel(Goal(GoalA, GoalStatus.Completed, Task("dev", AgentRole.Developer, null, partial)));
        var slice = Build(kernel);
        Assert.Equal(new RoundValueGoalUsageFigures(5, null, 0, 1, 0, 0, 1), slice.Landed.Sums);
        Assert.Equal(0, slice.Landed.EligibleGoals);
        Assert.Null(slice.Landed.Means.InputTokens);
        Assert.Null(slice.Landed.Means.Dispatches);
        WithRepository(kernel, (repository, root) =>
        {
            using var json = JsonDocument.Parse(Run(repository, root, [.. WindowArgs, "--by-goal", "--json"]));
            var row = Assert.Single(json.RootElement.GetProperty("rows").EnumerateArray());
            AssertTokens(row, 5, null, 0);
            Assert.Equal(JsonValueKind.Null, Assert.Single(row.GetProperty("roleModels").EnumerateArray())
                .GetProperty("model").ValueKind);
            Assert.Contains("Developer | none | 1 | 1 | 5 | unknown | 0",
                Run(repository, root, [.. WindowArgs, "--by-goal", "--goal", "aaaa"]));
        });
    }

    [Fact]
    public void Build_TwoLandedAndOneLost_SumsAndAveragesOnlyLandedGoals()
    {
        var kernel = Kernel(
            Goal(GoalC, GoalStatus.Failed, Task("lost", AgentRole.Developer, "model", Reported(900, 800, 700))),
            Goal(GoalB, GoalStatus.Completed, Task("dev-b", AgentRole.Developer, "model", Reported(201, 101, 21))),
            Goal(GoalA, GoalStatus.Completed, Task("dev-a", AgentRole.Developer, "model", Reported(100, 60, 10))));
        var slice = Build(kernel);
        Assert.Equal([GoalA, GoalB, GoalC], slice.Rows.Select(r => r.GoalId));
        Assert.Equal("lost", slice.Rows[2].Outcome);
        Assert.Equal((2, 2), (slice.Landed.LandedGoals, slice.Landed.EligibleGoals));
        Assert.Equal(new RoundValueGoalUsageFigures(301, 161, 31, 2, 0, 0, 0), slice.Landed.Sums);
        Assert.Equal(new RoundValueGoalUsageMeans(150.5, 80.5, 15.5, 1, 0, 0, 0), slice.Landed.Means);
        var pair = Assert.Single(slice.Landed.RoleModels);
        Assert.Equal(slice.Landed.Sums, pair.Sums);
        Assert.Equal(slice.Landed.Means, pair.Means);
        WithRepository(kernel, (repository, root) =>
        {
            Assert.Contains("Usage per landed goal | eligible=2 | Input=150.5 | Cached input=80.5 | Output=15.5",
                Run(repository, root, [.. WindowArgs, "--by-goal"]));
            using var json = JsonDocument.Parse(Run(repository, root, [.. WindowArgs, "--by-goal", "--json"]));
            var landed = json.RootElement.GetProperty("landed");
            Assert.Equal(2, landed.GetProperty("landedGoals").GetInt32());
            Assert.Equal(2, landed.GetProperty("eligibleGoals").GetInt32());
            AssertTokens(landed.GetProperty("sums"), 301, 161, 31);
            Assert.Equal(150.5, landed.GetProperty("means").GetProperty("inputTokens").GetDouble());
            AssertTokens(Assert.Single(landed.GetProperty("roleModels").EnumerateArray()).GetProperty("sums"), 301, 161, 31);
        });
    }

    [Fact]
    public void Build_NoLandedGoalHasReportedUsage_ShowsUnknownMeansDespiteLostGoalUsage()
    {
        var kernel = Kernel(
            Goal(GoalA, GoalStatus.Completed, Task("a", AgentRole.Developer, "model", Unknown())),
            Goal(GoalB, GoalStatus.Completed, Task("b", AgentRole.Developer, "model", Unknown())),
            Goal(GoalC, GoalStatus.Failed, Task("c", AgentRole.Developer, "model", Reported(999, 99, 9))));
        var landed = Build(kernel).Landed;
        Assert.Equal((2, 0), (landed.LandedGoals, landed.EligibleGoals));
        Assert.Equal(new RoundValueGoalUsageFigures(null, null, null, 2, 0, 0, 2), landed.Sums);
        Assert.Equal(new RoundValueGoalUsageMeans(null, null, null, null, null, null, null), landed.Means);
        WithRepository(kernel, (repository, root) => Assert.Contains(
            "Usage per landed goal | eligible=0 | Input=unknown | Cached input=unknown | Output=unknown | Dispatches=unknown",
            Run(repository, root, [.. WindowArgs, "--by-goal"])));
    }

    [Fact]
    public void Build_UnknownLandedGoal_UsesEligibleGoalDenominatorAndRetainsAllLandedCounts()
    {
        var kernel = Kernel(
            Goal(GoalA, GoalStatus.Completed, Task("a", AgentRole.Developer, "model", Reported(0, 0, 0))),
            Goal(GoalB, GoalStatus.Completed, Task("b", AgentRole.Reviewer, "other", Unknown())));
        var landed = Build(kernel).Landed;
        Assert.Equal((2, 1), (landed.LandedGoals, landed.EligibleGoals));
        Assert.Equal(new RoundValueGoalUsageFigures(0, 0, 0, 2, 0, 1, 1), landed.Sums);
        Assert.Equal(new RoundValueGoalUsageMeans(0, 0, 0, 2, 0, 1, 1), landed.Means);
        Assert.Null(landed.RoleModels[1].Means.InputTokens);
        Assert.Equal(1, landed.RoleModels[1].Means.Dispatches);
    }

    [Fact]
    public void ByGoal_SelectedActiveGoal_IgnoresWindowAndLoadsOnlyMatchedGoal()
    {
        var oldTask = Task("active", AgentRole.Developer, "model", Reported(12, 8, 4));
        oldTask = oldTask with { DispatchHistory = [oldTask.DispatchHistory![0] with { DispatchedAt = Since.AddDays(-10) }] };
        var kernel = Kernel(Goal(GoalA, GoalStatus.Active, oldTask),
            Goal(GoalB, GoalStatus.Completed, Task("other", AgentRole.Developer, "other", Reported(900, 800, 700))));
        Assert.Equal(GoalB, Assert.Single(Build(kernel).Rows).GoalId);
        WithRepository(kernel, (repository, root) =>
        {
            var args = new string[] { "round-value", "--goal", "AAAA", "--by-goal", "--since",
                "2026-09-24T00:00:00Z", "--until", "2026-09-26T00:00:00Z" };
            var text = Run(repository, root, args);
            Assert.Contains($"Round value by goal {GoalA} | any state, all dates", text);
            Assert.Contains($"{GoalA} | active | 1 | 0 | 0 | 0 | 12 | 8 | 4", text);
            Assert.Contains("Developer | model | 1 | 0 | 12 | 8 | 4", text);
            Assert.Contains("Landed sums | goals=0 | eligible=0 | Input=unknown", text);
            Assert.Equal([GoalA], repository.RequestedGoalIds);
            using var json = JsonDocument.Parse(Run(repository, root, [.. args, "--json"]));
            Assert.Equal(GoalA, json.RootElement.GetProperty("goal").GetString());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("since").ValueKind);
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("until").ValueKind);
            Assert.Equal(0, json.RootElement.GetProperty("landed").GetProperty("landedGoals").GetInt32());
        });
    }

    [Theory]
    [InlineData("--goal", "missing")]
    [InlineData("--goal", "a")]
    [InlineData("--goal")]
    [InlineData("--goal", "")]
    [InlineData("--goal", " ")]
    [InlineData("--goal", "--json")]
    [InlineData("--by-skill")]
    [InlineData("--by-check")]
    public void ByGoal_InvalidSelectionOrSliceCombination_WritesOnlyUsageAndExitsNonzero(params string[] extra)
    {
        var kernel = Kernel(
            Goal(GoalA, GoalStatus.Active, Task("dev-a", AgentRole.Developer, "model")),
            Goal("aaaabbbbbbbbbbbbbbbbbbbbbbbbbbbb", GoalStatus.Active,
                Task("dev-b", AgentRole.Developer, "model")));
        WithRepository(kernel, (repository, root) =>
        {
            string stdout = "";
            var stderr = AsyncLocalConsoleRouter.CaptureError(() => stdout = CaptureConsole(() =>
            {
                var error = Assert.Throws<CliExitException>(() => Execute(repository, root,
                    [.. WindowArgs, "--by-goal", "--json", .. extra]));
                Assert.Equal(1, error.ExitCode);
            }));
            Assert.Empty(stdout);
            Assert.Equal(CliCommandHelp.RoundValueUsage + Environment.NewLine, stderr);
            Assert.Equal(0, repository.LoadGoalsCount);
        });
    }

    [Fact]
    public void Build_NoDispatches_KeepsUnknownTokensAndNumericZeroCounts()
    {
        var kernel = Kernel(Goal(GoalA, GoalStatus.Active, Task("dev", AgentRole.Developer, "model")));
        var slice = Build(kernel);
        Assert.Empty(slice.Rows);
        Assert.Equal(new RoundValueGoalUsageFigures(null, null, null, 0, 0, 0, 0), slice.Landed.Sums);
        var row = Assert.Single(RoundValueGoalUsageSlice.BuildForGoal(kernel.Goals.Single()).Rows);
        Assert.Equal((0, 0), (row.Dispatches, row.UnknownDispatches));
        Assert.Null(row.InputTokens);
        Assert.Empty(row.RoleModels);
        WithRepository(kernel, (repository, root) => Assert.Contains(
            $"{GoalA} | active | 0 | 0 | 0 | 0 | unknown | unknown | unknown",
            Run(repository, root, [.. WindowArgs, "--by-goal", "--goal", "aaaa"])));
    }

    [Fact]
    public void Build_LastDispatchWindow_SelectsEntireGoalLedgerWithExclusiveUpperBound()
    {
        var task = Task("dev", AgentRole.Developer, "model", Reported(10, 6, 1), Reported(20, 12, 2));
        task = task with { DispatchHistory = [task.DispatchHistory![0] with { DispatchedAt = Since.AddDays(-20) },
            task.DispatchHistory[1] with { DispatchedAt = Since }] };
        var boundary = Task("end", AgentRole.Developer, "model", Reported(999, 99, 9));
        boundary = boundary with { DispatchHistory = [boundary.DispatchHistory![0] with { DispatchedAt = Until }] };
        var slice = Build(Kernel(Goal(GoalA, GoalStatus.Completed, task), Goal(GoalB, GoalStatus.Completed, boundary)));
        Assert.Equal(GoalA, Assert.Single(slice.Rows).GoalId);
        Assert.Equal(new RoundValueGoalUsageFigures(30, 18, 3, 2, 1, 0, 0), slice.Landed.Sums);
        Assert.Throws<ArgumentException>(() => RoundValueGoalUsageSlice.Build([], Until, Since));
    }

    [Fact]
    public void Build_PartialGoalAndEligibleGoals_SumsAllComponentsWithOneDenominator()
    {
        var partial = new DispatchProviderUsage(ProviderUsageValue.Reported(4),
            ProviderUsageValue.Unknown("fixture"), ProviderUsageValue.Reported(0));
        var kernel = Kernel(
            Goal(GoalA, GoalStatus.Completed, Task("a", AgentRole.Developer, "Z", Reported(1, 1, 1))),
            Goal(GoalB, GoalStatus.Completed, Task("b", AgentRole.Developer, "a", Reported(1, 1, 1))),
            Goal(GoalC, GoalStatus.Completed, Task("c", AgentRole.Developer, null, partial)));
        var summary = Build(kernel).Landed;
        Assert.Equal((3, 2), (summary.LandedGoals, summary.EligibleGoals));
        Assert.Equal(new RoundValueGoalUsageFigures(6, 2, 2, 3, 0, 0, 1), summary.Sums);
        Assert.Equal(new RoundValueGoalUsageMeans(3, 1, 1, 1.5, 0, 0, 0.5), summary.Means);
        Assert.Equal(new string?[] { null, "Z", "a" }, summary.RoleModels.Select(p => p.Model));
        Assert.Equal(new RoundValueGoalUsageMeans(2, null, 0, 0.5, 0, 0, 0.5), summary.RoleModels[0].Means);
    }

    [Fact]
    public void RoundValue_WithoutByGoal_RemainsByteIdenticalToExistingText()
    {
        var kernel = Kernel(Goal(GoalA, GoalStatus.Completed,
            Task("dev", AgentRole.Developer, "model", Reported(100, 60, 10))));
        WithRepository(kernel, (repository, root) =>
        {
            var expected = string.Join(Environment.NewLine,
            [
                "Round value [2026-09-24T00:00:00.0000000+00:00, 2026-09-26T00:00:00.0000000+00:00) | cohort = landed or lost goals whose last round is in the window",
                "Day | Landed | Lost | Rounds | Productive | Overhead | Wasted | Rounds per landing | Waste share | Input | Cached input | Output | Usage unreported",
                "2026-09-24 | 1 | 0 | 1 | 1 | 0 | 0 | 1 | 0 | 100 | 60 | 10 | 0",
                "Window | 1 | 0 | 1 | 1 | 0 | 0 | 1 | 0 | 100 | 60 | 10 | 0",
                "Waste cause | Rounds | Share",
                "Pending goals | 0 | rounds=0",
                ""
            ]);
            var baseline = Run(repository, root, WindowArgs);
            Assert.Equal(expected, baseline);
            Run(repository, root, [.. WindowArgs, "--by-goal"]);
            Assert.Equal(baseline, Run(repository, root, WindowArgs));
        });
    }

    private static RoundValueGoalUsageSlice Build(AgentOrchestratorKernel kernel) =>
        RoundValueGoalUsageSlice.Build(kernel.Goals, Since, Until);

    private static DispatchProviderUsage Reported(long input, long cached, long output) =>
        DispatchProviderUsage.From(new ProviderReportedUsage(InputTokens: input, OutputTokens: output, CachedInputTokens: cached), null);

    private static DispatchProviderUsage Unknown() => DispatchProviderUsage.From(null, "fixture");

    private static TaskSnapshot Task(string id, AgentRole role, string? model, params DispatchProviderUsage[] usage) =>
        new(id, id, role, WorkTaskStatus.Completed, null, null, null, [], null, null,
            DispatchHistory: usage.Select((value, i) => new TaskDispatchSnapshot("worker", "command", "root",
                Since.AddHours(i + 1), ModelName: model, ProviderUsage: value)).ToArray());

    private static GoalSnapshot Goal(string id, GoalStatus status, params TaskSnapshot[] tasks) =>
        new(id, "Usage", status, tasks, tasks.SelectMany(task => task.DispatchHistory!.Select(dispatch =>
            new ProgressEventSnapshot(id, task.Id, ProgressKind.TaskCompleted, "done", dispatch.DispatchedAt.AddMinutes(1)))).ToArray());

    private static AgentOrchestratorKernel Kernel(params GoalSnapshot[] goals) =>
        AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(goals, []));

    private static void AssertTokens(JsonElement element, long? input, long? cached, long? output)
    {
        Check("inputTokens", input);
        Check("cachedInputTokens", cached);
        Check("outputTokens", output);
        void Check(string name, long? expected)
        {
            if (expected is { } value) Assert.Equal(value, element.GetProperty(name).GetInt64());
            else Assert.Equal(JsonValueKind.Null, element.GetProperty(name).ValueKind);
        }
    }

    private static string Run(ProbeStateRepository repository, string root, string[] args) =>
        CaptureConsole(() => Execute(repository, root, args));

    private static void Execute(ProbeStateRepository repository, string root, string[] args)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? goal = null;
        Assert.True(CliReadOnlyCommandRunner.TryExecute(args, repository, OrchestratorWorkspace.ForDirectory(root),
            new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref goal, out var changed));
        Assert.False(changed);
    }

    private static void WithRepository(AgentOrchestratorKernel kernel, Action<ProbeStateRepository, string> action)
    {
        var root = CreateTempDirectory();
        try
        {
            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
            action(repository, root);
            Assert.Equal(0, repository.SaveAttempts);
            Assert.Equal(0, repository.MergeSaveAttempts);
            Assert.Equal(0, repository.MutationAttempts);
            Assert.Equal(0, repository.FullLoadAttempts);
            Assert.Equal(0, repository.ListOutboxMessagesCount);
            Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
