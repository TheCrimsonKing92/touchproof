using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

public sealed class HumanInputRetractionMultilineBriefTests
{
    [Fact]
    public void SupersededAnswerIsRemovedAcrossAdjacentLinesInBothHeaderForms()
    {
        const string staleFirstLine = "stale-alpha-q7";
        const string staleSecondLine = "stale-bravo-q7";
        const string currentFirstLine = "current-charlie-q7";
        const string currentSecondLine = "current-delta-q7";
        const string unrelatedLine = "unrelated-echo-q7";
        const string retractedAnswer = staleFirstLine + "\n" + staleSecondLine;
        const string currentAnswer = currentFirstLine + "\n" + currentSecondLine;

        // Parallel-safe: each case uses a private journal directory and an injected clock.
        var workspace = Directory.CreateTempSubdirectory("mcg-multiline-retraction-");
        try
        {
            var contextDirectory = workspace.CreateSubdirectory("context").FullName;
            var journalDirectory = workspace.CreateSubdirectory(Path.Combine(".orchestrator", "goal-operations"));
            var clock = new FakeClock();
            var kernel = new AgentOrchestratorKernel(clock);
            var developer = new TaskSpec(TaskId.New(), "Fix acceptance failure. " + new string('x', 400), AgentRole.Developer);
            var goal = kernel.CreateGoal("Recover failed acceptance", [developer]);
            kernel.ActivateGoal(goal.Id, DefaultAgents());

            var request = kernel.RequestHumanInput(goal.Id, developer.Id, "Which answer should govern?");
            kernel.SubmitHumanInput(request.Id, retractedAnswer);
            Assert.Equal(retractedAnswer, request.AuthoritativeAnswer?.Text);

            // Exceed the receipt cap so the journal tail is split into individual segment lines.
            var detail = string.Join(Environment.NewLine,
                new string('p', 520), unrelatedLine, staleFirstLine, staleSecondLine, currentFirstLine, currentSecondLine);
            SharedJsonlFile.AppendLine(Path.Combine(journalDirectory.FullName, $"{goal.Id.Value}.jsonl"),
                JsonSerializer.Serialize(new { operation = "conductor:acceptance", status = "Failed", at = clock.UtcNow, detail }));
            kernel.RecordAcceptanceFailure(goal.Id, ["first check " + new string('a', 720), "second check " + new string('b', 720)]);
            clock.Advance();
            kernel.RetryTask(goal.Id, developer.Id, "Fix the acceptance failure.");

            TaskBriefSegment BuildHeader() => Assert.Single(kernel.BuildTaskBriefSource(
                goal.Id, developer.Id, workingDirectory: workspace.FullName, contextDirectory: contextDirectory).Segments,
                segment => segment.Lines.Contains("## ACCEPTANCE FAILURE - FIX FIRST"));

            var before = BuildHeader();
            Assert.NotNull(before.CollapsedLines);
            AssertAdjacentLines(before.Lines, staleFirstLine, staleSecondLine);
            AssertAdjacentLines(before.CollapsedLines, staleFirstLine, staleSecondLine);

            clock.Advance();
            kernel.SupersedeHumanInput(goal.Id, request.Id, currentAnswer, HumanInputAnswerOrigin.Operator);

            var after = BuildHeader();
            Assert.NotNull(after.CollapsedLines);
            AssertRetractedAndPreserved(after.Lines);
            AssertRetractedAndPreserved(after.CollapsedLines);
        }
        finally
        {
            workspace.Delete(recursive: true);
        }

        static void AssertRetractedAndPreserved(IReadOnlyList<string> lines)
        {
            Assert.DoesNotContain(lines, line => line.Contains(staleFirstLine, StringComparison.Ordinal));
            Assert.DoesNotContain(lines, line => line.Contains(staleSecondLine, StringComparison.Ordinal));
            Assert.DoesNotContain(retractedAnswer, string.Join('\n', lines), StringComparison.Ordinal);
            Assert.Contains(unrelatedLine, lines);
            AssertAdjacentLines(lines, currentFirstLine, currentSecondLine);
        }
    }

    private static void AssertAdjacentLines(IReadOnlyList<string> lines, string first, string second)
    {
        Assert.Contains(lines.Zip(lines.Skip(1)), pair => pair.First == first && pair.Second == second);
    }
}
