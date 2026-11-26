using Mcg.AgentOrchestrator.Core;

public sealed class HumanInputRetractionMultilineBriefTests
{
    [Fact]
    public void SupersededAnswerIsRemovedAcrossAdjacentLinesInBothHeaderForms()
    {
        const string staleFirstKey = "stale-alpha-q7";
        const string staleSecondKey = "stale-bravo-q7";
        const string currentFirstKey = "current-charlie-q7";
        const string currentSecondKey = "current-delta-q7";
        const string unrelatedKey = "unrelated-echo-q7";

        // Parallel-safe: the context directory and clock are private to this test.
        var workspace = Directory.CreateTempSubdirectory("mcg-multiline-retraction-");
        try
        {
            // Repository entries render as separate header lines in ordinal key order.
            var worktrees = new[] { staleFirstKey, staleSecondKey, currentFirstKey, currentSecondKey, unrelatedKey }
                .ToDictionary(key => key, key => Path.Combine(workspace.FullName, key));
            var staleFirstLine = $"- {staleFirstKey}: {worktrees[staleFirstKey]}";
            var staleSecondLine = $"- {staleSecondKey}: {worktrees[staleSecondKey]}";
            var currentFirstLine = $"- {currentFirstKey}: {worktrees[currentFirstKey]}";
            var currentSecondLine = $"- {currentSecondKey}: {worktrees[currentSecondKey]}";
            var unrelatedLine = $"- {unrelatedKey}: {worktrees[unrelatedKey]}";
            var retractedAnswer = staleFirstLine + "\n" + staleSecondLine;
            var currentAnswer = currentFirstLine + "\n" + currentSecondLine;

            var clock = new FakeClock();
            var kernel = new AgentOrchestratorKernel(clock);
            // A description longer than the title and its context pointer enables the fallback.
            var developer = new TaskSpec(TaskId.New(), "Update repository notes. " + new string('x', 260), AgentRole.Developer);
            var goal = kernel.CreateGoal("Update repository notes", [developer]);
            kernel.ActivateGoal(goal.Id, DefaultAgents());

            var request = kernel.RequestHumanInput(goal.Id, developer.Id, "Which answer should govern?");
            kernel.SubmitHumanInput(request.Id, retractedAnswer);
            Assert.Equal(retractedAnswer, request.AuthoritativeAnswer?.Text);

            TaskBriefSegment BuildHeader() => Assert.Single(kernel.BuildTaskBriefSource(
                goal.Id, developer.Id, workingDirectory: workspace.FullName, contextDirectory: workspace.FullName,
                repositoryWorktrees: worktrees).Segments,
                segment => segment.Lines.Contains("# Agent Task Brief"));

            var before = BuildHeader();
            Assert.NotNull(before.CollapsedLines);
            AssertAdjacentLines(before.Lines, staleFirstLine, staleSecondLine);
            AssertAdjacentLines(before.CollapsedLines, staleFirstLine, staleSecondLine);

            clock.Advance();
            kernel.SupersedeHumanInput(goal.Id, request.Id, currentAnswer, HumanInputAnswerOrigin.Operator);
            Assert.Equal(currentAnswer, request.AuthoritativeAnswer?.Text);

            var after = BuildHeader();
            Assert.NotNull(after.CollapsedLines);
            AssertRetractedAndPreserved(after.Lines);
            AssertRetractedAndPreserved(after.CollapsedLines);

            void AssertRetractedAndPreserved(IReadOnlyList<string> lines)
            {
                Assert.DoesNotContain(lines, line => line.Contains(staleFirstKey, StringComparison.Ordinal));
                Assert.DoesNotContain(lines, line => line.Contains(staleSecondKey, StringComparison.Ordinal));
                Assert.DoesNotContain(retractedAnswer, string.Join('\n', lines), StringComparison.Ordinal);
                Assert.Contains(unrelatedLine, lines);
                AssertAdjacentLines(lines, currentFirstLine, currentSecondLine);
            }
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    private static void AssertAdjacentLines(IReadOnlyList<string> lines, string first, string second)
    {
        Assert.Contains(lines.Zip(lines.Skip(1)), pair => pair.First == first && pair.Second == second);
    }
}
