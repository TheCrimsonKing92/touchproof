using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns its temporary workspace/database; model and git are fake.
public sealed class AuthorBriefDraftMeasurementOnlyCheckTests
{
    private const string CheckName = "measurement-only-draft";
    private const string BuildItems = "1. Add a stopwatch and write one timer entry per pass.\n" +
        "2. Add a counter and emit one census line per pass.";
    private static string InstrumentationDraft => CliAuthorDraftCommandTests.ValidMarkdown
        .Replace("Implement the requested slice.", BuildItems);

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Run_DraftKindAndTag_ApplyMeasurementOnlyPredicate(
        bool measureTagged, bool hasFix, bool expectedPassed)
    {
        var markdown = InstrumentationDraft + "\n";
        if (hasFix) markdown = markdown.Replace(BuildItems, BuildItems + "\n3. Cache the resolved head between passes.");

        var check = AuthorBriefDraftMeasurementOnlyCheck.Run(markdown, measureTagged);

        Assert.Equal(CheckName, check.Name);
        Assert.Equal(expectedPassed, check.Passed);
        if (!expectedPassed)
        {
            Assert.Contains("Propose the fix the measurement would inform", check.Detail);
            Assert.Contains("kind stale with staleKind refused", check.Detail);
            Assert.Contains("needs a fix design", check.Detail);
        }
    }

    [Theory]
    [InlineData("timer")]
    [InlineData("stopwatch")]
    [InlineData("counter")]
    [InlineData("census")]
    [InlineData("attribute")]
    [InlineData("attribution")]
    [InlineData("instrument")]
    [InlineData("measure")]
    public void Run_InstrumentationWord_FailsUntagged(string word)
    {
        var check = AuthorBriefDraftMeasurementOnlyCheck.Run("## What to build\n1. " + word.ToUpperInvariant(), false);
        Assert.False(check.Passed);
    }

    [Theory]
    [InlineData("fix")]
    [InlineData("reduce")]
    [InlineData("skip")]
    [InlineData("avoid")]
    [InlineData("cache")]
    [InlineData("remove")]
    [InlineData("replace")]
    [InlineData("batch")]
    [InlineData("defer")]
    [InlineData("cap")]
    [InlineData("bound")]
    public void Run_BehaviourChangeWord_PassesUntagged(string word)
    {
        var check = AuthorBriefDraftMeasurementOnlyCheck.Run(
            "## What to build\n1. Add a timer.\n2. Add a counter and " + word.ToUpperInvariant(), false);
        Assert.True(check.Passed);
    }

    [Theory]
    [InlineData("## Scope\n1. Add a timer.", true)]
    [InlineData("## What to build\nAdd a timer.", true)]
    [InlineData("## What to build elsewhere\n1. Add a timer.", true)]
    [InlineData("### wHaT tO BuIlD:\r\n1) Add a timer.\r\n2) Add a counter.", false)]
    [InlineData("## What to build ##\n1. Add a timer.", false)]
    [InlineData("## What to build\n1. Add a timer.\n## Scope\n2. Fix it.", false)]
    [InlineData("### What to build\n1. Add a timer.\n# Scope\n2. Fix it.", false)]
    [InlineData("## What to build\n1. Add a timer.\n##\n2. Fix it.", false)]
    [InlineData("## What to build\n1. Add a timer.\n### Details\nFix it.", true)]
    [InlineData("## What to build\n1. Add a timer.\n2. Write a report.", true)]
    [InlineData("## What to build\n1. Add a timer.\n   1. Write a report.\n2. Add a counter.", false)]
    [InlineData("## What to build\n1. Add a timer.\n   1. Fix it.\n2. Add a counter.", true)]
    [InlineData("## What to build\n1. Add a timer.\n   Fix it.", true)]
    [InlineData("## What to build\n1. Add a measurement.", true)]
    [InlineData("## What to build\n1. Add timers and counters.", true)]
    [InlineData("## What to build\n1. Add a timer without a prefix or suffix.", false)]
    [InlineData("## What to build\n1. Add a timer; do not fix it.", true)]
    [InlineData("## What to build\n1. Add a `timer`.", false)]
    [InlineData("```md\n## What to build\n1. Add a timer.\n```", true)]
    [InlineData("## What to build\n~~~md\n1. Add a timer.\n~~~", true)]
    [InlineData("```md\n## What to build\n1. Fix it.\n```\n## What to build\n1. Add a timer.", false)]
    [InlineData("## What to build\n1. Add a timer.\n   ```md\n## Scope\n2. Write a report.\n   ```\n2. Add a counter.", false)]
    [InlineData("## What to build\n1. Add diagnostic output.\n   ~~~text\n   timer\n   ~~~", false)]
    [InlineData("## What to build\n1. Add a timer.\n   ```text\n   fix\n   ```", true)]
    [InlineData("## What to build\n1. Add a timer.\n   > Fix it.", true)]
    public void Run_MarkdownShapeAndVocabulary_UseOnlyOuterBuildItems(string markdown, bool passed)
    {
        Assert.Equal(passed, AuthorBriefDraftMeasurementOnlyCheck.Run(markdown, false).Passed);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("measure", true)]
    [InlineData("other,MeAsUrE\tmore", true)]
    [InlineData("measured measurement premeasure", false)]
    public async Task Service_TagState_RecordsCheckAndRevisionFailures(string tags, bool passed)
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        await fixture.Store.UpdateAsync(fixture.Item.Id, new BacklogItemUpdate(Tags: tags));
        var databaseBefore = File.ReadAllBytes(fixture.Workspace.BacklogStorePath);
        var prompts = new List<string>();

        var outcome = AuthorBriefDraftService.Run(fixture.Item.Id, fixture.Workspace, new((request, _) =>
        {
            prompts.Add(request.StandardInput!);
            return Task.FromResult(Draft(InstrumentationDraft));
        }, fixture.Repository), fixture.Output, fixture.Error);

        Assert.Equal(passed ? 0 : 1, outcome.ExitCode);
        Assert.Equal("draft", outcome.Kind);
        Assert.Equal(passed, Assert.Single(outcome.Checks, check => check.Name == CheckName).Passed);
        Assert.All(outcome.Checks.Where(check => check.Name != CheckName), check => Assert.True(check.Passed, check.Detail));
        Assert.Equal(passed ? 1 : 3, prompts.Count);
        Assert.Contains(passed ? "The backlog item is tagged measure." : "The backlog item is not tagged measure.", prompts[0]);
        if (!passed) Assert.All(prompts.Skip(1), prompt => Assert.Contains("Failed measurement-only-draft:", prompt));
        using var receipt = fixture.Receipt();
        var check = Assert.Single(receipt.RootElement.GetProperty("checks").EnumerateArray(),
            entry => entry.GetProperty("name").GetString() == CheckName);
        Assert.Equal(passed, check.GetProperty("passed").GetBoolean());
        var rounds = receipt.RootElement.GetProperty("rounds").EnumerateArray().ToArray();
        Assert.Equal(prompts.Count, rounds.Length);
        Assert.All(rounds, round => Assert.Equal(passed ? Array.Empty<string>() : new[] { CheckName },
            round.GetProperty("failedChecks").EnumerateArray().Select(entry => entry.GetString()).ToArray()));
        Assert.Equal(databaseBefore, File.ReadAllBytes(fixture.Workspace.BacklogStorePath));
        Assert.Empty(fixture.Error.ToString());
    }

    [Fact]
    public void Prompt_UntaggedItem_RequiresFixOrFixDesignRefusal()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var prompt = AuthorBriefDraftPrompt.Render(fixture.Item, ConductorLessonSelection.Empty,
            CliAuthorDraftCommandTests.Fixture.MainSha);

        Assert.Contains("Unless the backlog item is tagged measure, never draft work that only adds timers, counters or census output; " +
            "propose the fix that measurement would inform, or return kind stale with staleKind refused " +
            "and a reason saying the item needs a fix design.", prompt);
        Assert.Contains("The backlog item is not tagged measure.", prompt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerdictRevision_TagState_AppliesSameCheck(bool measureTagged)
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        if (measureTagged) await fixture.Store.UpdateAsync(fixture.Item.Id, new BacklogItemUpdate(Tags: "measure"));
        var prompts = new List<string>();
        var revise = ConductorBoardFillHost.CreateRevisionSeam(fixture.Workspace, (request, _) =>
        {
            prompts.Add(request.StandardInput!);
            return Task.FromResult(Draft(InstrumentationDraft));
        }, fixture.Repository);

        var result = await revise(new(fixture.Item.Id, CliAuthorDraftCommandTests.Fixture.MainSha,
            "Correct the previous draft."), CancellationToken.None);

        Assert.Equal("draft", result.Kind);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(InstrumentationDraft, result.Markdown);
        Assert.Equal(CliAuthorDraftCommandTests.Fixture.MainSha, result.MainHead);
        Assert.Equal(measureTagged, Assert.Single(result.Checks, check => check.Name == CheckName).Passed);
        Assert.All(result.Checks.Where(check => check.Name != CheckName), check => Assert.True(check.Passed, check.Detail));
        Assert.Contains(measureTagged ? "The backlog item is tagged measure." : "The backlog item is not tagged measure.",
            Assert.Single(prompts));
    }

    [Fact]
    public void Run_MeasureTag_BypassesOnlyMeasurementCheck()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var checks = AuthorBriefDraftChecks.Run("## What to build\n1. Add a timer.",
            CliAuthorDraftCommandTests.Fixture.MainSha, fixture.Repository, measureTagged: true);
        Assert.True(Assert.Single(checks, check => check.Name == CheckName).Passed);
        Assert.False(Assert.Single(checks, check => check.Name == "sections").Passed);
        Assert.False(Assert.Single(checks, check => check.Name == "criteria-present").Passed);
    }

    private static WorkerProcessRunResult Draft(string markdown) =>
        new(0, JsonSerializer.Serialize(new { kind = "draft", markdown }), "");
}
