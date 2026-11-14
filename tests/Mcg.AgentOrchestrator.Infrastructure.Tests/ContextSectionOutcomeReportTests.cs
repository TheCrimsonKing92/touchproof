using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all goals, history, usage and writers are local in-memory fixtures.
public sealed class ContextSectionOutcomeReportTests
{
    [Fact]
    public void Build_MixedOutcomes_ReportsCountsSharesAndRecordedStatistics()
    {
        var report = ContextSectionOutcomeReport.Build(Fixture());
        Assert.Equal(8, report.DispatchCount);
        Assert.Equal(1, report.UnresolvedDispatchCount);
        Assert.Equal(new[] { "alpha", "beta", "common" }, report.Rows.Select(row => row.Section));
        var alpha = report.Rows[0];
        Assert.Equal("Developer", alpha.Role);
        Assert.Equal(4, alpha.DispatchCount);
        Assert.Equal(0, alpha.LandedCount);
        Assert.Equal(0, alpha.FailedCount);
        Assert.Equal(4, alpha.RetriedCount);
        Assert.Equal(0d, alpha.LandedShareWithSection);
        Assert.Equal(4, alpha.WithoutSectionDispatchCount);
        Assert.Equal(2, alpha.WithoutSectionLandedCount);
        Assert.Equal(0.5, alpha.LandedShareWithoutSection);
        Assert.Equal(4, alpha.ReportedInputCount);
        Assert.Equal(new ContextUsageStat(40000, 70000), alpha.InputUsage);
        Assert.Equal(new ContextUsageStat(400, 700), alpha.SectionBytes);
        Assert.Equal(0, alpha.MissingUsageCount);
        Assert.Empty(alpha.MissingUsageReasons);

        var beta = report.Rows[1];
        Assert.Equal(2, beta.DispatchCount);
        Assert.Equal(2, beta.LandedCount);
        Assert.Equal(0, beta.FailedCount);
        Assert.Equal(0, beta.RetriedCount);
        Assert.Equal(1d, beta.LandedShareWithSection);
        Assert.Equal(6, beta.WithoutSectionDispatchCount);
        Assert.Equal(0, beta.WithoutSectionLandedCount);
        Assert.Equal(0d, beta.LandedShareWithoutSection);
        Assert.Equal(1, beta.ReportedInputCount);
        Assert.Equal(new ContextUsageStat(20000, 20000), beta.InputUsage);
        Assert.Equal(new ContextUsageStat(300, 400), beta.SectionBytes);
        Assert.Equal(1, beta.MissingUsageCount);
        Assert.Equal(1, Assert.Single(beta.MissingUsageReasons).Value);
        Assert.Equal("provider-omitted", Assert.Single(beta.MissingUsageReasons).Key);
        Assert.Equal(1, beta.UnresolvedCount);

        var common = report.Rows[2];
        Assert.Equal(8, common.DispatchCount);
        Assert.Equal(2, common.LandedCount);
        Assert.Equal(2, common.FailedCount);
        Assert.Equal(4, common.RetriedCount);
        Assert.Equal(0.25, common.LandedShareWithSection);
        Assert.Equal(0, common.WithoutSectionDispatchCount);
        Assert.Equal(0, common.WithoutSectionLandedCount);
        Assert.Null(common.LandedShareWithoutSection);
        Assert.Equal(new ContextUsageStat(40000, 70000), common.InputUsage);
        Assert.Equal(new ContextUsageStat(10, 10), common.SectionBytes);
    }

    [Fact]
    public void Build_ReceiptlessSuccessor_RetriesEarlierReceiptWithoutAddingPopulation()
    {
        var goal = GoalWith(WorkTaskStatus.Completed, Receipt(50000, "alpha", 500), null);
        var report = ContextSectionOutcomeReport.Build([goal]);
        Assert.Equal(1, report.DispatchCount);
        var row = Assert.Single(report.Rows, row => row.Section == "alpha");
        Assert.Equal(1, row.RetriedCount);
        Assert.Equal(0, row.LandedCount);
        Assert.Equal(0, row.WithoutSectionDispatchCount);
        Assert.Null(row.LandedShareWithoutSection);
    }

    [Theory]
    [InlineData(WorkTaskStatus.Pending)]
    [InlineData(WorkTaskStatus.Assigned)]
    [InlineData(WorkTaskStatus.Running)]
    [InlineData(WorkTaskStatus.WaitingForHuman)]
    public void Build_NonterminalLatest_ExcludesOutcomeSharesAndStatistics(WorkTaskStatus status)
    {
        var goal = GoalWith(status, Receipt(100, "alpha", 10), Receipt(999999, "pending-only", 999999));
        var report = ContextSectionOutcomeReport.Build([goal]);
        Assert.Equal(1, report.DispatchCount);
        Assert.Equal(1, report.UnresolvedDispatchCount);
        Assert.DoesNotContain(report.Rows, row => row.Section == "pending-only");
        var alpha = Assert.Single(report.Rows, row => row.Section == "alpha");
        Assert.Equal(1, alpha.RetriedCount);
        Assert.Equal(0, alpha.WithoutSectionDispatchCount);
        Assert.Equal(new ContextUsageStat(100, 100), alpha.InputUsage);
        var common = Assert.Single(report.Rows, row => row.Section == "common");
        Assert.Equal(1, common.DispatchCount);
        Assert.Equal(1, common.UnresolvedCount);
        Assert.Equal(new ContextUsageStat(10, 10), common.SectionBytes);
        Assert.Empty(ContextSectionOutcomeReport.Build([GoalWith(status, Receipt(999, "only", 999))]).Rows);
    }

    [Theory]
    [InlineData(WorkTaskStatus.Completed, 1, 0)]
    [InlineData(WorkTaskStatus.Failed, 0, 1)]
    [InlineData(WorkTaskStatus.Cancelled, 0, 1)]
    public void Build_TerminalLatest_MapsLandedAndFailed(WorkTaskStatus status, int landed, int failed)
    {
        var row = Assert.Single(ContextSectionOutcomeReport.Build([
            GoalWith(status, Receipt(0, "alpha", 0))]).Rows, row => row.Section == "alpha");
        Assert.Equal(landed, row.LandedCount);
        Assert.Equal(failed, row.FailedCount);
        Assert.Equal(0, row.RetriedCount);
        Assert.Equal(new ContextUsageStat(0, 0), row.InputUsage);
    }

    [Fact]
    public void Build_DuplicateAndCaseDistinctSections_CountsOnceAndSumsBytes()
    {
        var receipt = Receipt(100, "alpha", 100);
        receipt = receipt with { Sections = [.. receipt.Sections, Section("alpha", 200), Section("Alpha", 50)] };
        var report = ContextSectionOutcomeReport.Build([GoalWith(WorkTaskStatus.Completed, receipt)]);
        Assert.Equal(new[] { "Alpha", "alpha", "common" }, report.Rows.Select(row => row.Section));
        var alpha = report.Rows[1];
        Assert.Equal(1, alpha.DispatchCount);
        Assert.Equal(1, alpha.ReportedInputCount);
        Assert.Equal(new ContextUsageStat(300, 300), alpha.SectionBytes);
    }

    [Fact]
    public void Build_EmptySectionsAndMissingUsage_KeepComparisonAndByteSamples()
    {
        var empty = Receipt(10, "unused", 10) with { Sections = [] };
        var report = ContextSectionOutcomeReport.Build([
            GoalWith(WorkTaskStatus.Completed, Receipt(null, "alpha", 80)),
            GoalWith(WorkTaskStatus.Completed, empty)]);
        var alpha = Assert.Single(report.Rows, row => row.Section == "alpha");
        Assert.Equal(1, alpha.LandedCount);
        Assert.Equal(1d, alpha.LandedShareWithSection);
        Assert.Equal(1, alpha.WithoutSectionDispatchCount);
        Assert.Equal(1d, alpha.LandedShareWithoutSection);
        Assert.Equal(0, alpha.ReportedInputCount);
        Assert.Equal(new ContextUsageStat(null, null), alpha.InputUsage);
        Assert.Equal(new ContextUsageStat(80, 80), alpha.SectionBytes);
        Assert.Equal(1, alpha.MissingUsageCount);
    }

    [Fact]
    public void Build_RoleAndSinceFilters_PreserveFullHistoryAndInclusiveOffset()
    {
        var goal = GoalWith(WorkTaskStatus.Completed,
            Receipt(1, "before", 1), Receipt(2, "alpha", 2), null);
        var since = ContextUsageTestFixture.Start.AddMinutes(1).ToOffset(TimeSpan.FromHours(-5));
        var report = ContextSectionOutcomeReport.Build([goal], since, "developer");
        Assert.Equal(since, report.Since);
        Assert.Equal("developer", report.Role);
        Assert.Equal(1, report.DispatchCount);
        Assert.DoesNotContain(report.Rows, row => row.Section == "before");
        Assert.Equal(1, Assert.Single(report.Rows, row => row.Section == "alpha").RetriedCount);
        Assert.Empty(ContextSectionOutcomeReport.Build([goal], role: "unknown").Rows);
        Assert.Empty(ContextSectionOutcomeReport.Build([goal], ContextUsageTestFixture.Start.AddDays(1)).Rows);
        Assert.Empty(ContextSectionOutcomeReport.Build([]).Rows);
    }

    [Fact]
    public void Build_RecordedChronology_UsesTimestampsThenHistoryOrderForTies()
    {
        var original = GoalWith(WorkTaskStatus.Completed,
            Receipt(100, "alpha", 100), Receipt(200, "beta", 200));
        var snapshot = original.Tasks.Single().ToSnapshot();
        var history = snapshot.DispatchHistory!.ToArray();
        var first = history[0];
        var second = history[1];
        // Restore preserves persisted history order; LastDispatch is omitted to avoid replacing it.
        var reordered = TaskSpec.FromSnapshot(snapshot with { DispatchHistory = [second, first], LastDispatch = null });
        var report = ContextSectionOutcomeReport.Build([new Goal(GoalId.New(), "Restored chronology", [reordered])]);
        Assert.Equal(1, Assert.Single(report.Rows, row => row.Section == "alpha").RetriedCount);
        Assert.Equal(1, Assert.Single(report.Rows, row => row.Section == "beta").LandedCount);

        var tied = TaskSpec.FromSnapshot(snapshot with
        {
            DispatchHistory = [second with { DispatchedAt = first.DispatchedAt }, first], LastDispatch = null
        });
        report = ContextSectionOutcomeReport.Build([new Goal(GoalId.New(), "Tied chronology", [tied])]);
        Assert.Equal(1, Assert.Single(report.Rows, row => row.Section == "beta").RetriedCount);
        Assert.Equal(1, Assert.Single(report.Rows, row => row.Section == "alpha").LandedCount);
    }

    [Fact]
    public void Build_MultipleRolesAndModels_PoolsModelsAndIsolatesComparisonsByRole()
    {
        var developer = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
        developer.RecordDispatch(new("worker", "command", "C:\\work", ContextUsageTestFixture.Start,
            ModelName: "first-model", ContextPackageReceipt: Receipt(100, "alpha", 100)));
        developer.RecordDispatch(new("worker", "command", "C:\\work", ContextUsageTestFixture.Start.AddMinutes(1),
            ModelName: "second-model", ContextPackageReceipt: Receipt(300, "alpha", 300)));
        developer.SetStatus(WorkTaskStatus.Completed);
        var tester = new TaskSpec(TaskId.New(), "Test", AgentRole.Tester);
        tester.RecordDispatch(new("worker", "command", "C:\\work", ContextUsageTestFixture.Start,
            ContextPackageReceipt: Receipt(900, "beta", 900)));
        tester.SetStatus(WorkTaskStatus.Failed);
        var goal = new Goal(GoalId.New(), "Separate role populations", [tester, developer]);
        var report = ContextSectionOutcomeReport.Build([goal]);
        Assert.Equal(new[] { "Developer/alpha", "Developer/common", "Tester/beta", "Tester/common" },
            report.Rows.Select(row => $"{row.Role}/{row.Section}"));
        var alpha = report.Rows[0];
        Assert.Equal(2, alpha.DispatchCount);
        Assert.Equal(0.5, alpha.LandedShareWithSection);
        Assert.Equal(0, alpha.WithoutSectionDispatchCount);
        Assert.Equal(new ContextUsageStat(200, 300), alpha.InputUsage);
        Assert.Equal(1, report.Rows[2].FailedCount);
        Assert.Equal(0, report.Rows[2].WithoutSectionDispatchCount);
        Assert.Equal(2, ContextSectionOutcomeReport.Build([goal], role: "tEsTeR").Rows.Count);
    }

    [Fact]
    public void Cli_SectionsText_ReportsOutcomesSharesAndUnavailableComparison()
    {
        CliCommandHelp.ThrowIfInvalidFlags(["context-usage", "--sections"]);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CliContextUsageCommand.Run(["context-usage", "--sections"], Fixture, output, error));
        Assert.Empty(error.ToString());
        Assert.Contains("Context sections: dispatches=8 rows=3 unresolved=1", output.ToString());
        Assert.Contains("alpha | dispatches=4 landed=0 failed=0 retried=4", output.ToString());
        Assert.Contains("landed-with=0.0 % (0/4) landed-without=50.0 % (2/4)", output.ToString());
        Assert.Contains("landed-without=n/a (0/0)", output.ToString());
        Assert.Contains("input=median=20000,p90=20000 bytes=median=300,p90=400", output.ToString());
        Assert.Contains("missing-usage=1 reasons=provider-omitted=1 unresolved=1", output.ToString());
    }

    [Fact]
    public void Cli_SectionsJson_ComposesFlagsAndUsesCamelCaseAndNullShares()
    {
        string[] args = ["context-usage", "--json", "--role", "developer", "--sections",
            "--since", "2026-09-15T07:00:00-05:00"];
        CliCommandHelp.ThrowIfInvalidFlags(args);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CliContextUsageCommand.Run(args, Fixture, output, error));
        Assert.Empty(error.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(8, json.RootElement.GetProperty("dispatchCount").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("unresolvedDispatchCount").GetInt32());
        var rows = json.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(3, rows.Length);
        Assert.Equal(0.5, rows[0].GetProperty("landedShareWithoutSection").GetDouble());
        Assert.Equal(20000, rows[1].GetProperty("inputUsage").GetProperty("p90").GetInt64());
        Assert.Equal(400, rows[1].GetProperty("sectionBytes").GetProperty("p90").GetInt64());
        Assert.Equal(JsonValueKind.Null, rows[2].GetProperty("landedShareWithoutSection").ValueKind);
        Assert.All(rows, row => Assert.All(row.EnumerateObject(), property =>
            Assert.True(char.IsLower(property.Name[0]))));
    }

    [Fact]
    public void Cli_DefaultSelection_PreservesTextAndJsonExactly()
    {
        var goal = GoalWith(WorkTaskStatus.Completed, Receipt(20000, "alpha", 200));
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CliContextUsageCommand.Run(["context-usage"], () => [goal], output, error));
        Assert.Equal("Context usage: dispatches=1 rows=1" + Environment.NewLine +
            "Developer | test-model | dispatches=1 reported-input=1 | input=median=20000,p90=20000 " +
            "cached=median=0,p90=0 uncached=median=20000,p90=20000 output=median=0,p90=0 " +
            "harness-overhead=median=unknown,p90=unknown | missing-usage=0 reasons=none" + Environment.NewLine,
            output.ToString());
        output.GetStringBuilder().Clear();
        Assert.Equal(0, CliContextUsageCommand.Run(["context-usage", "--json"], () => [goal], output, error));
        Assert.Equal("{\"since\":null,\"role\":null,\"rows\":[{\"role\":\"Developer\",\"model\":\"test-model\"," +
            "\"dispatchCount\":1,\"reportedInputCount\":1,\"input\":{\"median\":20000,\"p90\":20000}," +
            "\"cached\":{\"median\":0,\"p90\":0},\"uncached\":{\"median\":20000,\"p90\":20000}," +
            "\"output\":{\"median\":0,\"p90\":0},\"harnessOverhead\":{\"median\":null,\"p90\":null}," +
            "\"missingUsageCount\":0,\"missingUsageReasons\":{}}]}" + Environment.NewLine, output.ToString());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public void Cli_MalformedSectionsFlags_RejectsBeforeLoadingGoals()
    {
        var loads = 0;
        using var output = new StringWriter();
        using var error = new StringWriter();
        foreach (string[] args in new string[][] {
            ["context-usage", "--sections", "--bogus"],
            ["context-usage", "--sections", "--since", "2026-09-15"],
            ["context-usage", "--sections", "--role"] })
            Assert.Equal(1, CliContextUsageCommand.Run(args, () => { loads++; return Fixture(); }, output, error));
        Assert.Equal(0, loads);
        Assert.Empty(output.ToString());
        Assert.Contains(CliCommandHelp.ContextUsageUsage, error.ToString());
    }

    private static IReadOnlyCollection<Goal> Fixture() => [
        GoalWith(WorkTaskStatus.Completed, Receipt(10000, "alpha", 100),
            Receipt(30000, "alpha", 300), Receipt(20000, "beta", 200)),
        GoalWith(WorkTaskStatus.Failed, Receipt(40000)),
        GoalWith(WorkTaskStatus.Completed, Receipt(null, "beta", 400)),
        GoalWith(WorkTaskStatus.Completed, Receipt(50000, "alpha", 500), null),
        GoalWith(WorkTaskStatus.Running, Receipt(70000, "alpha", 700), Receipt(999999, "beta", 999999)),
        GoalWith(WorkTaskStatus.Cancelled, Receipt(60000))];

    private static Goal GoalWith(WorkTaskStatus status, params WorkerContextPackageReceipt?[] receipts)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement section report", AgentRole.Developer);
        var goal = kernel.CreateGoal("Measure section outcomes", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        for (var index = 0; index < receipts.Length; index++)
            ContextUsageTestFixture.Add(kernel, goal, task, ContextUsageTestFixture.Start.AddMinutes(index),
                "test-model", receipts[index]);
        task.SetStatus(status);
        return goal;
    }

    private static WorkerContextPackageReceipt Receipt(long? input, string? section = null, int bytes = 0) =>
        new("test", section is null ? [Section("common", 10)] : [Section("common", 10), Section(section, bytes)],
            input.HasValue ? ProviderUsageValue.Reported(input.Value) : ProviderUsageValue.Unknown("provider-omitted: no input usage"),
            ProviderUsageValue.Reported(0), ProviderUsageValue.Reported(0));

    private static WorkerContextSectionReceipt Section(string identity, int bytes) =>
        new(identity, bytes, bytes, "test-hash", ContextDeliveryMode.InlineFull, 1, [AgentRole.Developer]);
}
