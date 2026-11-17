using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

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
    public void Check_ClosedMetricName_ParsesAndPasses(string name)
    {
        var markdown = "## Outcome metric\n" + ValidLine.Replace("selection-milliseconds", name);
        Assert.True(CostGoalOutcomeMetric.Check(markdown, true).Passed);
        var parsed = CostGoalOutcomeMetric.Parse(markdown);
        Assert.Empty(parsed.Defects);
        Assert.Equal(new OutcomeMetricLine(name, "decrease", "20%", 5), Assert.Single(parsed.Metrics));
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorPaths_CostTagWithoutMetric_ReturnFailedCheck(bool revision)
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        await fixture.Store.UpdateAsync(fixture.Item.Id, new BacklogItemUpdate(Tags: "cost"));
        var databaseBefore = File.ReadAllBytes(fixture.Workspace.BacklogStorePath);
        Assert.Equal("cost", AuthorBriefDraftBacklog.Read(fixture.Workspace.BacklogStorePath, fixture.Item.Id).Tags);
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
    }
}
