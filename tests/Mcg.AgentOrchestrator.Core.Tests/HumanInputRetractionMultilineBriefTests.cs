using Mcg.AgentOrchestrator.Core;
using System.Text.Json;

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

    [Theory]
    [InlineData("\n", false)]
    [InlineData("\r\n", false)]
    [InlineData("\r\n", true)]
    public void SupersedingAnswerPreservesConvergencePathCoordinatesAfterMultilineFinding(
        string newline, bool retractAfterPaths)
    {
        const string staleAnswer = "obsolete finding detail";
        const string retainedDetail = "Retain the finding continuation.";
        var paths = new[] { "src/A.cs", "src/B.cs" };
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var reviewer = new TaskSpec(TaskId.New(), "Review convergence", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Review changed paths", [reviewer]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var request = kernel.RequestHumanInput(goal.Id, reviewer.Id, "Which detail should govern?");
        kernel.SubmitHumanInput(request.Id, staleAnswer);

        var description = staleAnswer + newline + retainedDetail;
        kernel.RecordTaskVerification(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review", "C:\\fixture", 1,
            $$"""
            WORKER_RESULT:
            findings: [{"stable_id":"scope","state":"open","severity":"blocking","location":{"file":"src/A.cs","region":"A.Run"},"description":{{JsonSerializer.Serialize(description)}}}]
            touched_anchors: []
            END_WORKER_RESULT
            """, string.Empty, clock.UtcNow,
            MergedReviewFindings: [new ReviewFinding("scope", ReviewFindingState.Open,
                new ReviewFindingLocation("src/A.cs", "A.Run"), description)], WorkerResultPresent: true));

        TaskBriefSegment BuildConvergence() => Assert.Single(kernel.BuildTaskBriefSource(
            goal.Id, reviewer.Id, reviewerScopeChangedFiles: paths).Segments,
            segment => segment.ConvergenceChangedFiles is not null);

        var before = BuildConvergence();
        var beforeFinding = Assert.Single(before.Lines, line => line.EndsWith(description, StringComparison.Ordinal));

        clock.Advance();
        kernel.SupersedeHumanInput(goal.Id, request.Id, "Current finding detail", HumanInputAnswerOrigin.Operator);
        if (retractAfterPaths)
        {
            var beforePaths = before.ConvergenceChangedFiles!;
            var trailingLines = before.Lines.Skip(beforePaths.FirstListLineIndex + beforePaths.ListLineCount).Take(2).ToArray();
            Assert.Equal(2, trailingLines.Length);
            var trailingRequest = kernel.RequestHumanInput(goal.Id, reviewer.Id, "Which closing guidance should govern?");
            kernel.SubmitHumanInput(trailingRequest.Id, string.Join('\n', trailingLines));
            clock.Advance();
            kernel.SupersedeHumanInput(goal.Id, trailingRequest.Id, "Current closing guidance", HumanInputAnswerOrigin.Operator);
        }

        var after = BuildConvergence();
        var pathList = after.ConvergenceChangedFiles!;
        Assert.Equal(paths.Select(path => $"- {path}"),
            after.Lines.Skip(pathList.FirstListLineIndex).Take(pathList.ListLineCount));
        Assert.DoesNotContain(after.Lines, line => line.Contains(staleAnswer, StringComparison.Ordinal));
        Assert.Contains(beforeFinding.Replace(staleAnswer, string.Empty, StringComparison.Ordinal), after.Lines);
    }

    private static void AssertAdjacentLines(IReadOnlyList<string> lines, string first, string second)
    {
        Assert.Contains(lines.Zip(lines.Skip(1)), pair => pair.First == first && pair.Second == second);
    }
}
