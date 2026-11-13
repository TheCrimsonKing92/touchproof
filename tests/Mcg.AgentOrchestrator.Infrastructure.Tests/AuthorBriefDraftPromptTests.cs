using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AuthorBriefDraftPromptTests
{
    [Fact]
    public void Prompt_defines_unlanded_dependency_premises_and_result_field()
    {
        var item = new BacklogItem("item", "Draft title", "Draft body", BacklogItemStatus.Open,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null);
        var prompt = AuthorBriefDraftPrompt.Render(item, ConductorLessonSelection.Empty,
            CliAuthorDraftCommandTests.Fixture.MainSha);
        var lines = prompt.ReplaceLineEndings("\n").Split('\n');

        Assert.Contains("When the item names a goal it builds on that has not landed, draft against that goal's brief and criteria for the parts it provides.", lines);
        Assert.Contains("Write those premise bullets as provided-by-<goal-prefix>: <claim> without a HEAD citation, and return the goal prefixes in dependsOnGoals (6 to 32 hexadecimal characters each).", lines);
        Assert.Contains("Never return a refused result solely because that dependency has not landed.", lines);
        Assert.Contains("or {\"kind\":\"draft\",\"markdown\":\"<complete brief markdown>\",\"dependsOnGoals\":[\"<goal-prefix>\"]}", lines);
    }

    [Fact]
    public async Task Command_prompt_carries_backlog_annotations_lessons_rules_and_clarification_launcher_settings()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        using var lessons = new ConductorLessonTestStore();
        lessons.Add("brief-lesson", ["author:brief"], rule: "Prove every premise at HEAD");
        lessons.Add("general-lesson", ["author"], rule: "Name the evidence owner");
        lessons.Add("unrelated", ["steward"], rule: "Do not select this lesson");
        File.Copy(lessons.Path, fixture.Workspace.OperatorLessonsStorePath);

        Assert.Equal(0, fixture.Run(JsonSerializer.Serialize(new
        {
            kind = "draft", markdown = CliAuthorDraftCommandTests.ValidMarkdown
        })));
        var request = Assert.IsType<WorkerProcessRunRequest>(fixture.Request);
        var prompt = request.StandardInput!;
        var lines = prompt.ReplaceLineEndings("\n").Split('\n');
        const string numbering = "Number acceptance criteria `1.`, `2.` and so on, with no bulleted criteria and no nested bullets.";
        const string postLandingRule = "A step that happens after the goal lands is never a numbered criterion and is left out of the draft.";
        var numberingIndex = Array.IndexOf(lines, numbering);
        Assert.True(numberingIndex >= 0, "The criterion-numbering guidance must be present.");
        Assert.Equal(1, lines.Count(line => line == postLandingRule));
        Assert.Equal(postLandingRule, lines[numberingIndex + 1]);
        const string partialRule = "A new class goes in a separately named type in its own file, never in a new partial file of an existing class.";
        const string preChangeRule = "A criterion that needs a test to fail against the pre-change code is never assigned to Acceptance, because focused evidence runs only on the candidate; the pre-change half belongs to a Reviewer reading or to a committed negative-control test that runs on the candidate.";
        Assert.Equal(1, lines.Count(line => line == partialRule));
        Assert.Equal(partialRule, lines[numberingIndex + 2]);
        Assert.Equal(1, lines.Count(line => line == preChangeRule));
        Assert.Equal(preChangeRule, lines[numberingIndex + 3]);
        Assert.DoesNotContain(BriefLint.Lint(prompt), finding => finding.Kind == "pre-change-failure-criterion");
        var shapeChecks = AuthorBriefDraftChecks.Run(prompt, CliAuthorDraftCommandTests.Fixture.MainSha, fixture.Repository)
            .Where(check => check.Name is "new-partial-file" or "pre-change-failure-criterion").ToArray();
        Assert.Equal(2, shapeChecks.Length);
        Assert.All(shapeChecks, check => Assert.True(check.Passed, check.Detail));
        Assert.Contains(fixture.Item.Title, prompt);
        Assert.Contains(fixture.Item.Body, prompt);
        Assert.Contains("Inspect the owning seam", prompt);
        Assert.Contains(CliAuthorDraftCommandTests.Fixture.MainSha, prompt);
        foreach (var section in new[] { "Measured premise", "What to build", "Acceptance criteria", "Scope" })
            Assert.Contains("## " + section, prompt);
        Assert.Contains("X owns; Y executes.", prompt);
        Assert.Contains("TEST-VERIFIABLE", prompt);
        Assert.Contains("REAL-WORLD-DEPENDENT", prompt);
        Assert.Contains("docs/role-capability-matrix.md", prompt);
        Assert.Contains("docs/worker-guidance-discipline.md", prompt);
        Assert.Contains("Prove every premise at HEAD", prompt);
        Assert.Contains("Name the evidence owner", prompt);
        Assert.DoesNotContain("Do not select this lesson", prompt);
        Assert.Contains("evidenceReferences", prompt);
        Assert.Contains("path:line", prompt);

        WorkerProcessRunRequest? clarificationRequest = null;
        var round = new ClaudeConductorAuthorModelRound(Path.Combine(fixture.Workspace.OrchestratorDirectory, "oracle-rounds"),
            (input, _) =>
            {
                clarificationRequest = input;
                return Task.FromResult(new WorkerProcessRunResult(0, "{}", ""));
            });
        await round.DispatchAsync(new ConductorAuthorRoundInput(
            new ConductorAuthorItem(OperatorAnswerTargetKind.Clarification, "item", "goal", "question", "brief"),
            "brief", "{}", null), fixture.Workspace.ExecutionDirectory, CancellationToken.None);
        var oracle = Assert.IsType<WorkerProcessRunRequest>(clarificationRequest);
        Assert.Equal(oracle.Command, request.Command);
        Assert.Contains("--permission-mode plan", request.Command);
        Assert.Equal(oracle.Timeout, request.Timeout);
        Assert.Equal(oracle.WorkingDirectory, request.WorkingDirectory);
    }
}
