using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AuthorContinuationPromptTests
{
    [Theory]
    [InlineData("Follow-up slices: alpha; beta")]
    [InlineData("Remaining slices: not recorded")]
    [InlineData(null)]
    public void Render_instructs_the_author_to_continue_from_recorded_deliveries(string? scope)
    {
        const string header = "Recorded slice landing for goal abcdef1234567890. integrateCommit=1234567890abcdef";
        var landingNote = scope is null ? header : header + "\n" + scope;
        var item = BoardFillReadyItemSelectorTests.Item() with
        {
            Notes = [new BacklogNote(DateTimeOffset.UnixEpoch, landingNote)]
        };

        var prompt = AuthorBriefDraftPrompt.Render(item, ConductorLessonSelection.Empty,
            CliAuthorDraftCommandTests.Fixture.MainSha);

        Assert.Contains(JsonSerializer.Serialize(landingNote), prompt);
        Assert.Contains("use the latest applicable remaining-scope record and the history of landed work", prompt);
        Assert.Contains("Draft only the next listed slice that has not already been delivered.", prompt);
        Assert.Contains("When the recorded scope and delivery evidence establish that no slices remain, return kind stale with staleKind delivered and the required commit and path evidence.", prompt);
        Assert.Contains("A note saying `Remaining slices: not recorded`, or a historical landing without a remaining-scope declaration, leaves the scope unknown.", prompt);
    }
}
