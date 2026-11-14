using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: the command fixture owns its files, stores and process seam.
public sealed class AuthorBriefDraftPromptTestsHouseConventions
{
    private const string HouseCriterion = "The Developer reports `tests: deferred - ` followed, directly after the hyphen and comma-separated, by every test class it touched or added. Developer owns; Acceptance executes. TEST-VERIFIABLE.";
    private const string FormerTesterSentence = "The Tester's evidence_request runs them.";

    [Fact]
    public void Command_prompt_requires_house_form_and_first_slice_scope()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        Assert.Equal(0, fixture.Run(JsonSerializer.Serialize(new
        {
            kind = "draft", markdown = CliAuthorDraftCommandTests.ValidMarkdown
        })));
        var request = Assert.IsType<WorkerProcessRunRequest>(fixture.Request);
        var prompt = request.StandardInput!.ReplaceLineEndings("\n");
        Assert.DoesNotContain("Planner output format", prompt);
        Assert.DoesNotContain("place the following Planner format section", prompt);
        Assert.DoesNotContain("maps to <subject>", prompt);
        Assert.Contains("Number acceptance criteria `1.`, `2.` and so on, with no bulleted criteria and no nested bullets.", prompt);
        Assert.Contains("The last two acceptance criteria are the following house Developer deferred-tests criterion, copied character for character, followed by one single-sentence Reviewer criterion:\n" +
            HouseCriterion, prompt);
        Assert.DoesNotContain(FormerTesterSentence, prompt);
        Assert.Contains("Keep What to build to at most four numbered items.", prompt);
        Assert.Contains("When the backlog item needs more, draft only the first slice and list the remaining slices under Scope in a line beginning `Follow-up slices:`.", prompt);
        Assert.DoesNotContain("numbered or bulleted", prompt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Toolchain.Dotnet)]
    public void Render_HouseStack_PreservesDeferredTestsCriterion(Toolchain? stack)
    {
        var item = new BacklogItem("item", "Draft title", "Draft body", BacklogItemStatus.Open,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null);
        var prompt = AuthorBriefDraftPrompt.Render(item, ConductorLessonSelection.Empty,
            CliAuthorDraftCommandTests.Fixture.MainSha, stack);

        Assert.Contains(HouseCriterion, prompt);
        Assert.DoesNotContain(FormerTesterSentence, prompt);
    }
}
