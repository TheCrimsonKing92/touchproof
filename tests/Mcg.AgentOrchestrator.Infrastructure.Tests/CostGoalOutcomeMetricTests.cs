using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: pure parsing or an owned workspace/database; model and git are fake.
public sealed class CostGoalOutcomeMetricTests
{
    private const string ValidLine = "metric: selection-milliseconds; direction: decrease; target: 20%; window: 5 gates";

    [Theory]
    [InlineData("", "no well-formed ## Outcome metric line")]
    [InlineData("## Outcome metric\nmetric: unknown; direction: decrease; target: 20%; window: 5 gates", "unknown metric 'unknown'")]
    [InlineData("## Outcome metric\nmetric: process-launches; direction: decrease; window: 5 gates", "has no target")]
    [InlineData("## Outcome metric\nmetric: process-launches; direction: decrease; target: ; window: 5 gates", "has no target")]
    public void Check_MissingOrInvalidDeclaration_NamesDefect(string markdown, string detail)
    {
        var check = CostGoalOutcomeMetric.Check(markdown, true);
        Assert.Equal("cost-goal-outcome-metric", check.Name);
        Assert.False(check.Passed);
        Assert.Contains(detail, check.Detail);
        Assert.Contains("direction: decrease|increase", check.Detail);
    }

    [Theory]
    [InlineData("selection-milliseconds")]
    [InlineData("executed-class-count")]
    [InlineData("gate-test-cpu-seconds")]
    [InlineData("full-gate-rate")]
    [InlineData("process-launches")]
    [InlineData("worker-turns-per-session")]
    [InlineData("worker-input-volume-per-session")]
    public void Check_ClosedMetricName_ParsesAndPasses(string name)
    {
        var markdown = "## Outcome metric\n" + ValidLine.Replace("selection-milliseconds", name);
        Assert.True(CostGoalOutcomeMetric.Check(markdown, true).Passed);
        var parsed = CostGoalOutcomeMetric.Parse(markdown);
        Assert.Empty(parsed.Defects);
        Assert.Equal(new OutcomeMetricLine(name, "decrease", "20%", 5), Assert.Single(parsed.Metrics));
    }

    [Theory]
    [InlineData("worker-input-tokens-per-session")]
    [InlineData("worker-turns")]
    [InlineData("Worker-Turns-Per-Session")]
    public void Check_UnlistedWorkerMetric_RejectsAndListsAllowedNames(string name)
    {
        var markdown = "## Outcome metric\n" + ValidLine.Replace("selection-milliseconds", name);
        var check = CostGoalOutcomeMetric.Check(markdown, true);

        Assert.False(check.Passed);
        Assert.Contains($"unknown metric '{name}'", check.Detail);
        Assert.Contains("worker-turns-per-session, worker-input-volume-per-session", check.Detail);
    }

    [Theory]
    [InlineData("direction: sideways", "direction")]
    [InlineData("target: twenty", "target")]
    [InlineData("window: 0 gates", "window")]
    [InlineData("window: -2 gates", "window")]
    [InlineData("window: 1.5 gates", "window")]
    [InlineData("window: 999999999999 gates", "window")]
    public void Check_MalformedValue_RejectsDeclaration(string replacement, string field)
    {
        var original = field switch
        {
            "direction" => "direction: decrease", "target" => "target: 20%", _ => "window: 5 gates"
        };
        var check = CostGoalOutcomeMetric.Check("## Outcome metric\n" + ValidLine.Replace(original, replacement), true);
        Assert.False(check.Passed);
        Assert.Contains(field, check.Detail);
    }

    [Theory]
    [InlineData("; target: 1")]
    [InlineData("; typo: ignored")]
    public void Check_DuplicateOrExtraField_Fails(string suffix)
    {
        var check = CostGoalOutcomeMetric.Check("## Outcome metric\n" + ValidLine + suffix, true);
        Assert.False(check.Passed);
        Assert.Contains("malformed or unexpected fields", check.Detail);
    }

    [Fact]
    public void Check_OneBadLineAmongValidLines_Fails()
    {
        var check = CostGoalOutcomeMetric.Check("## Outcome metric\n" + ValidLine + "\n" +
            ValidLine.Replace("selection-milliseconds", "unrecorded"), true);
        Assert.False(check.Passed);
        Assert.Contains("unknown metric 'unrecorded'", check.Detail);
    }

    [Theory]
    [InlineData("Expected effect: fewer gates", true)]
    [InlineData("Intro\r\nExpected effect: fewer gates", true)]
    [InlineData(" Expected effect: fewer gates", false)]
    [InlineData("**Expected effect**: fewer gates", false)]
    [InlineData("## Expected effect", false)]
    [InlineData("expected effect: fewer gates", false)]
    [InlineData("An ordinary brief", false)]
    public void Check_TextSignal_UsesLiteralLinePrefix(string markdown, bool cost)
    {
        Assert.Equal(cost, CostGoalOutcomeMetric.IsCostBrief(markdown, false));
        Assert.Equal(!cost, CostGoalOutcomeMetric.Check(markdown, false).Passed);
    }

    [Theory]
    [InlineData("", "79801150abcdef", true)]
    [InlineData("", "12345678abcdef", false)]
    [InlineData("cost", null, true)]
    [InlineData("other,CoSt\tmore", null, true)]
    [InlineData("costly pre-cost", null, false)]
    [InlineData("", null, false)]
    public void Classification_BacklogSignals_UseDirectEpicAndCompleteTag(
        string tags, string? epicId, bool cost)
    {
        var instant = DateTimeOffset.Parse("2026-11-17T00:00:00Z");
        var item = new BacklogItem("item", "title", "body", BacklogItemStatus.Open, instant, instant, null, Tags: tags);
        var flag = CostGoalOutcomeMetric.IsCostBacklogItem(item, epicId);
        Assert.Equal(cost, flag);
        Assert.Equal(!cost, CostGoalOutcomeMetric.Check("Ordinary brief", flag).Passed);
    }

    [Fact]
    public void Check_FencedOrOtherSectionExample_DoesNotCount()
    {
        foreach (var markdown in new[]
        {
            "## Outcome metric\n```text\n" + ValidLine + "\n```",
            "## Outcome metric\n## Scope\n" + ValidLine,
            "```md\n## Outcome metric\n" + ValidLine + "\n```"
        })
            Assert.False(CostGoalOutcomeMetric.Check(markdown, true).Passed);
    }

    [Fact]
    public void Parse_ListAndBacktickDeclarationWithCrLf_ReturnsTypedValues()
    {
        var parsed = CostGoalOutcomeMetric.Parse("## OUTCOME METRIC\r\n- `metric: process-launches; direction: increase; target: +1.5; window: 1 gate`\r\n## Scope\r\n");
        Assert.Empty(parsed.Defects);
        Assert.Equal(new OutcomeMetricLine("process-launches", "increase", "+1.5", 1), Assert.Single(parsed.Metrics));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AuthorPaths_CostTagOrEpicWithoutMetric_KeepStoresUnchanged(bool revision, bool costEpic)
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        byte[]? portfolioBefore = null;
        if (costEpic)
        {
            const string epicId = "79801150abcdef0123456789abcdef01";
            var portfolio = new PortfolioStore(fixture.Workspace.PortfolioStorePath);
            var epic = await portfolio.AddEpicAsync("Cost goals");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = fixture.Workspace.PortfolioStorePath, Pooling = false
            }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE epics SET id = $id WHERE id = $original";
                command.Parameters.AddWithValue("$id", epicId);
                command.Parameters.AddWithValue("$original", epic.Id);
                Assert.Equal(1, command.ExecuteNonQuery());
                // Make accidental setup observable even with a current schema version.
                // Membership reads do not require this optional lookup index.
                command.CommandText = "DROP INDEX idx_epics_title";
                command.ExecuteNonQuery();
            }
            await portfolio.AssignBacklogItemToEpicAsync(fixture.Item.Id, epicId);
            Assert.Equal(epicId, (await PortfolioStore.OpenReadOnly(fixture.Workspace.PortfolioStorePath)
                .GetBacklogMembershipAsync(fixture.Item.Id))!.EpicId);
            portfolioBefore = File.ReadAllBytes(fixture.Workspace.PortfolioStorePath);
        }
        else
            await fixture.Store.UpdateAsync(fixture.Item.Id, new BacklogItemUpdate(Tags: "cost"));
        var databaseBefore = File.ReadAllBytes(fixture.Workspace.BacklogStorePath);
        var sourceItem = AuthorBriefDraftBacklog.Read(fixture.Workspace.BacklogStorePath, fixture.Item.Id);
        if (costEpic) Assert.False(BoardFillReadyItemSelector.HasTag(sourceItem, "cost"));
        else Assert.Equal("cost", sourceItem.Tags);
        WorkerProcessRunResult Draft() => new(0, JsonSerializer.Serialize(new
        {
            kind = "draft", markdown = CliAuthorDraftCommandTests.ValidMarkdown
        }), "");
        IReadOnlyList<AuthorBriefDraftCheck> checks;
        if (revision)
        {
            var revise = ConductorBoardFillHost.CreateRevisionSeam(fixture.Workspace,
                (_, _) => Task.FromResult(Draft()), fixture.Repository);
            var result = await revise(new(fixture.Item.Id, CliAuthorDraftCommandTests.Fixture.MainSha,
                "Correct the draft."), CancellationToken.None);
            checks = result.Checks;
        }
        else
        {
            var outcome = AuthorBriefDraftService.Run(fixture.Item.Id, fixture.Workspace,
                new((_, _) => Task.FromResult(Draft()), fixture.Repository), fixture.Output, fixture.Error);
            Assert.Equal(1, outcome.ExitCode);
            checks = outcome.Checks;
        }
        var check = Assert.Single(checks, check => check.Name == CostGoalOutcomeMetric.CheckName);
        Assert.False(check.Passed);
        Assert.Contains("no well-formed ## Outcome metric line", check.Detail);
        Assert.All(checks.Where(check => check.Name != CostGoalOutcomeMetric.CheckName), check => Assert.True(check.Passed, check.Detail));
        Assert.Equal(databaseBefore, File.ReadAllBytes(fixture.Workspace.BacklogStorePath));
        if (portfolioBefore is not null)
            Assert.Equal(portfolioBefore, File.ReadAllBytes(fixture.Workspace.PortfolioStorePath));
    }
}
