using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;

public sealed class ContextReworkUsageReportTests
{
    private const string GoalA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string GoalB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string GoalC = "cccccccccccccccccccccccccccccccc";
    private static readonly DateTimeOffset Start = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] Window = ["--since", "2026-09-15T07:00:00-05:00", "--until", "2026-09-15T12:10:00Z"];

    [Fact]
    public void GroupsWindowedRetriesAndCountsMultiClassUsageOnceInOverallTotals()
    {
        var goals = FixtureGoals();
        using var output = new StringWriter();
        using var error = new StringWriter();
        string[] args = ["context-usage", "--rework", .. Window, "--top", "1"];
        CliCommandHelp.ThrowIfInvalidFlags(args);
        var exit = CliContextUsageCommand.Run([.. args, "--json"], () => goals, output, error,
            since =>
            {
                Assert.Equal(Start, since);
                return (goals, new(1, 2, 1));
            });

        Assert.True(exit == 0, error.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        var report = json.RootElement;
        Assert.Equal(1, report.GetProperty("top").GetInt32());
        AssertTotals(report.GetProperty("overall"), 7, 300, 60, 30);
        var coverage = report.GetProperty("coverage");
        Assert.Equal(1, coverage.GetProperty("live").GetInt32());
        Assert.Equal(2, coverage.GetProperty("archivedHydrated").GetInt32());
        Assert.Equal(1, coverage.GetProperty("archivedUnavailable").GetInt32());
        Assert.Contains(report.GetProperty("notes").EnumerateArray(),
            note => note.GetString()!.Contains("rows are not additive", StringComparison.Ordinal));

        Assert.Collection(report.GetProperty("rows").EnumerateArray(),
            correctness =>
            {
                AssertRow(correctness, "Developer", "NewSourceFinding", "reviewer:correctness", 4, 190, 38, 19);
                Assert.Equal(2, correctness.GetProperty("goalsAffected").GetInt32());
                Assert.Equal(95d, correctness.GetProperty("medianInput").GetDouble());
                Assert.Equal(2, correctness.GetProperty("medianGoalCount").GetInt32());
                Assert.Collection(correctness.GetProperty("examples").EnumerateArray(),
                    pair => AssertExample(pair, GoalA, "dev-a"),
                    pair => AssertExample(pair, GoalB, "dev-b"),
                    pair => AssertExample(pair, GoalA, "dev-z"));
            },
            quality => AssertRow(quality, "Developer", "NewSourceFinding", "reviewer:code-quality", 1, 70, 14, 7),
            specific => AssertRow(specific, "Developer", "NewSourceFinding",
                "reviewer-class:dispositive-misclassification", 1, 50, 10, 5),
            planner => AssertRow(planner, "Planner", "ProviderInterruption", "unclassified", 1, 30, 6, 3),
            tester => AssertRow(tester, "Tester", "NewTestFinding", "reviewer:test-coverage", 1, 30, 6, 3));

        output.GetStringBuilder().Clear();
        exit = CliContextUsageCommand.Run(args, () => goals, output, error);
        Assert.True(exit == 0, error.ToString());
        var text = output.ToString();
        Assert.Contains("Overall: attempts=7 input=300 cached=60 output=30", text);
        Assert.Contains("Coverage: live=3 archived-hydrated=0 archived-unavailable=0", text);
        Assert.Contains("rows are not additive", text);
        Assert.Contains("Developer | NewSourceFinding | reviewer:correctness | attempts=4", text);
        Assert.DoesNotContain("reviewer:code-quality", text);
    }

    [Fact]
    public void RejectsInvalidReworkOptionsBeforeReadingGoals()
    {
        const string usage = CliCommandHelp.ContextUsageUsage;
        const string reversedWindow = "--since must precede --until.";
        (string[] Flags, string Message)[] invalid =
        [
            (["--sections", "--rework"], usage),
            (["--top", "1"], usage),
            (["--sections", "--until", Window[3]], usage),
            (["--since", Window[3], "--until", Window[3]], reversedWindow),
            (["--since", Window[3], "--until", Window[1]], reversedWindow),
            (["--until", "2026-09-15T12:10:00"], usage),
            (["--rework", "--top", "0"], usage),
            (["--rework", "--until", "2026-09-15T12:10:00"], usage),
            (["--rework", "--since", Window[3], "--until", Window[3]], reversedWindow)
        ];
        foreach (var (flags, message) in invalid)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var loaded = false;
            var exit = CliContextUsageCommand.Run(["context-usage", .. flags], () =>
            {
                loaded = true;
                return [];
            }, output, error);
            Assert.Equal(1, exit);
            Assert.False(loaded);
            Assert.Equal(string.Empty, output.ToString());
            Assert.Equal($"Error: {message}{Environment.NewLine}", error.ToString());
        }
    }

    private static Goal[] FixtureGoals() =>
        AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
        [
            new(GoalC, "C", GoalStatus.Completed,
            [
                RetriedTask("dev-c", AgentRole.Developer, RetryCause.NewSourceFinding,
                    (-1, 900, ["specific"]), (4, 50, ["specific"]), (10, 900, ["specific"])),
                Reviewer(Finding("specific", FindingCategory.TestCoverage) with
                {
                    ClassId = FindingClass.DispositiveMisclassification
                })
            ], []),
            new(GoalB, "B", GoalStatus.Completed,
            [
                RetriedTask("dev-b", AgentRole.Developer, RetryCause.NewSourceFinding, (1, 50, ["correct"])),
                RetriedTask("plan-b", AgentRole.Planner, RetryCause.ProviderInterruption, (2, 30, ["correct"])),
                RetriedTask("test-b", AgentRole.Tester, RetryCause.NewTestFinding, (3, 30, ["coverage"])),
                Reviewer(Finding("correct", FindingCategory.Correctness), Finding("coverage", FindingCategory.TestCoverage))
            ], []),
            new(GoalA, "A", GoalStatus.Completed,
            [
                RetriedTask("dev-z", AgentRole.Developer, RetryCause.NewSourceFinding, (3, 10, ["correct"])),
                RetriedTask("dev-a", AgentRole.Developer, RetryCause.NewSourceFinding,
                    (0, 60, ["correct"]), (2, 70, ["correct", "same-class", "quality"])),
                Reviewer(Finding("correct", FindingCategory.Correctness), Finding("same-class", FindingCategory.Correctness),
                    Finding("quality", FindingCategory.CodeQuality))
            ], [])
        ], [])).Goals.ToArray();

    private static TaskSnapshot RetriedTask(string id, AgentRole role, RetryCause cause,
        params (int Minute, long Input, string[] Findings)[] retries)
    {
        // B's first dispatch falls inside the window. Its recorded usage still belongs to the first pass.
        var first = id.EndsWith("-b", StringComparison.Ordinal) ? 0 : -5;
        return Task(id, role) with
        {
            DispatchHistory = [Dispatch(first, 1_000), .. retries.Select(retry => Dispatch(retry.Minute, retry.Input))],
            RetryAdmissionHistory = retries.Select(retry => new RetryAdmissionReceipt(
                $"receipt-{retry.Minute}", cause, new(1, "fixture"), RetryAdmissionDecision.Allowed,
                RetryAdmissionRoute.SameRole, PaidRouteClassification.Unknown,
                Start.AddMinutes(retry.Minute), Start.AddMinutes(retry.Minute).AddSeconds(-1),
                StableFindingIds: retry.Findings)).ToArray()
        };
    }

    private static TaskSnapshot Reviewer(params ReviewFinding[] findings) => Task("review", AgentRole.Reviewer) with
    {
        VerificationHistory = [new("review", "root", 0, "", "", Start.AddMinutes(-3), MergedReviewFindings: findings)]
    };

    private static TaskSnapshot Task(string id, AgentRole role) =>
        new(id, id, role, WorkTaskStatus.Completed, null, null, null, [], null, null);

    private static ReviewFinding Finding(string id, FindingCategory category) =>
        new(id, ReviewFindingState.Open, new("src/Foo.cs", "Method"), "recorded finding", Category: category);

    private static TaskDispatchSnapshot Dispatch(int minute, long input) =>
        new("worker", "command", "root", Start.AddMinutes(minute),
            ContextPackageReceipt: ContextUsageTestFixture.Receipt(input, input / 5, input / 10, 0));

    private static void AssertRow(JsonElement row, string role, string cause, string findingClass,
        int attempts, long input, long cached, long output)
    {
        Assert.Equal(role, row.GetProperty("role").GetString());
        Assert.Equal(cause, row.GetProperty("retryCause").GetString());
        Assert.Equal(findingClass, row.GetProperty("findingClass").GetString());
        AssertTotals(row, attempts, input, cached, output);
    }

    private static void AssertTotals(JsonElement row, int attempts, long input, long cached, long output)
    {
        Assert.Equal(attempts, row.GetProperty("attempts").GetInt32());
        Assert.Equal(input, row.GetProperty("input").GetInt64());
        Assert.Equal(cached, row.GetProperty("cached").GetInt64());
        Assert.Equal(output, row.GetProperty("output").GetInt64());
        Assert.Equal(0, row.GetProperty("missingUsage").GetInt32());
    }

    private static void AssertExample(JsonElement pair, string goal, string task)
    {
        Assert.Equal(goal, pair.GetProperty("goalId").GetString());
        Assert.Equal(task, pair.GetProperty("taskId").GetString());
    }
}
