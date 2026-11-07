using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class DeveloperCompletionStructuralPreflightRetryFeedbackTests
{
    [Xunit.Fact]
    public void TwoCeilingViolations_ReplaceStaleFeedbackInNextDeveloperBrief()
    {
        var executionDirectory = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var (kernel, goal) = SoftwareGoal("Deliver structural pre-check violations");
            var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            foreach (var task in goal.Tasks.TakeWhile(task => task.Id != tester.Id))
            {
                PassVerification(kernel, goal, task);
            }

            const string staleFeedback = "stale earlier-round feedback: rename Foo";
            const string repairedCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string candidateSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            const string oldFinding = "Old rename Foo finding already repaired";
            var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            kernel.RecordDispatchResultCommit(goal.Id, developer.Id, repairedCommit);
            kernel.RecordTaskVerification(goal.Id, reviewer.Id, new TaskVerificationRecord(
                "review fixture", executionDirectory, 1, oldFinding, string.Empty, DateTimeOffset.UtcNow,
                ReviewedCommit: new string('0', 40),
                MergedReviewFindings: [new ReviewFinding("old-rename", ReviewFindingState.Open,
                    new ReviewFindingLocation("renamed.cs", "Foo"), oldFinding, FindingSeverity.Blocking,
                    FindingCategory.Correctness)], FullStandardOutput: oldFinding, FullStandardError: string.Empty));
            kernel.RecordCriterionRetryFeedback(goal.Id, developer.Id, [staleFeedback]);
            Assert.Equal([staleFeedback], kernel.GetTask(goal.Id, developer.Id).CriterionRetryFeedback);

            var worktreePath = GoalWorktrees.WorktreePath(executionDirectory, goal.Id);
            Directory.CreateDirectory(worktreePath);
            File.WriteAllText(Path.Combine(worktreePath, "renamed.cs"), "class CorrectName { }");
            var authorityPath = Path.Combine(worktreePath,
                SourceSizeRatchet.SourcePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(authorityPath)!);
            File.WriteAllLines(authorityPath,
            [
                "new SourceSizeCeiling(\"alpha.cs\", 2)",
                "new SourceSizeCeiling(\"beta.cs\", 4)"
            ]);
            File.WriteAllLines(Path.Combine(worktreePath, "alpha.cs"), Enumerable.Repeat("line", 3));
            File.WriteAllLines(Path.Combine(worktreePath, "beta.cs"), Enumerable.Repeat("line", 6));
            var expectedViolations = SourceSizeRatchetPreflight.BlockingViolationMessages(
                SourceSizeRatchetPreflight.Evaluate(worktreePath));
            Assert.Equal(2, expectedViolations.Count);

            var dispatchedRoles = new List<AgentRole>();
            var notes = new List<string>();
            TaskBriefSource? nextDeveloperBrief = null;
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                dispatchAndStart: candidate =>
                {
                    dispatchedRoles.Add(candidate.Tasks.First(task => task.Status == WorkTaskStatus.Assigned).RequiredRole);
                    nextDeveloperBrief = kernel.BuildTaskBriefSource(goal.Id, developer.Id);
                    kernel.RecordTaskDispatch(goal.Id, developer.Id, new TaskDispatchRecord(
                        "codex-cli", "codex exec", worktreePath, DateTimeOffset.UtcNow, BaseCommit: candidateSha));
                    return DispatchStartOutcome.Started();
                },
                retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
                    kernel.RetryTaskAutomatically(goalId, taskId, message, cause, retryRoundKind: roundKind),
                recordTaskNote: (_, _, message) => notes.Add(message),
                executionDirectory: executionDirectory,
                getPreReviewEvidenceContext: _ => new PreReviewEvidenceContext(candidateSha, [], null, "fixture", true, false));
            driver.ConfigureDeveloperCompletionStructuralPreflightRetry(kernel);

            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            Assert.Equal([AgentRole.Developer], dispatchedRoles);
            var feedback = Assert.Single(kernel.GetTask(goal.Id, developer.Id).CriterionRetryFeedback);
            foreach (var violation in expectedViolations)
            {
                Assert.Contains(violation, feedback, StringComparison.Ordinal);
            }
            Assert.Contains("alpha.cs", feedback, StringComparison.Ordinal);
            Assert.Contains("3 lines", feedback, StringComparison.Ordinal);
            Assert.Contains("ceiling of 2", feedback, StringComparison.Ordinal);
            Assert.Contains("beta.cs", feedback, StringComparison.Ordinal);
            Assert.Contains("6 lines", feedback, StringComparison.Ordinal);
            Assert.Contains("ceiling of 4", feedback, StringComparison.Ordinal);
            Assert.DoesNotContain(staleFeedback, feedback, StringComparison.Ordinal);

            var segment = Assert.Single(Assert.IsType<TaskBriefSource>(nextDeveloperBrief).Segments
                .Where(item => item.TypedProjectionIdentity == new LogicalArtifactIdentity("task/criterion-retry-feedback.json")));
            var renderedFeedback = string.Join(Environment.NewLine, segment.Lines);
            Assert.Contains(feedback, renderedFeedback, StringComparison.Ordinal);
            Assert.DoesNotContain(staleFeedback, renderedFeedback, StringComparison.Ordinal);
            Assert.Equal(feedback, kernel.GetTask(goal.Id, developer.Id).AcceptedRetryFeedback?.Message);
            var obligation = Assert.IsType<RetryCorrectionObligation>(developer.AcceptedRetryFeedback!.Obligation);
            Assert.Equal(RetryCorrectionObligationKind.Structural, obligation.Kind);
            Assert.Equal(RetryCorrectionCondition.SourceSizeRatchet, obligation.Condition);
            Assert.Equal(candidateSha, obligation.CandidateSha);
            Assert.Equal(developer.LastDispatch!.BaseCommit, obligation.CandidateSha);
            Assert.Equal(RetryCorrectionObligation.HashFeedback(feedback), obligation.FeedbackSha256);
            var source = Assert.IsType<TaskBriefSource>(nextDeveloperBrief);
            var legacy = source.ProjectLegacyMarkedTextV1(emitTypedSourceBoundaries: false);
            AssertObligationBeforeHistory(legacy.Content, oldFinding, candidateSha);
            var contextDirectory = Path.Combine(executionDirectory, "context");
            Directory.CreateDirectory(contextDirectory);
            File.WriteAllText(Path.Combine(contextDirectory, "artifact-registry.json"), "{\"artifacts\":[]}");
            var package = WorkerProfileDispatcher.BuildContextPackage(goal, developer, worktreePath,
                contextDirectory, legacy, currentCandidateSha: candidateSha, typedSource: source);
            AssertObligationBeforeHistory(WorkerContextPackageBuilder.Render(package), oldFinding, candidateSha);
            Assert.Equal(RetryCause.NewSourceFinding, developer.PendingRetryCause);
            Assert.Equal(RetryRoundKind.Mechanical, developer.PendingRetryRoundKind);
            Assert.StartsWith("developer-completion structural pre-check failed:", Assert.Single(notes), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(executionDirectory, recursive: true);
        }
    }

    private static void AssertObligationBeforeHistory(string prompt, string oldFinding, string candidate)
    {
        var current = prompt.IndexOf(RetryCorrectionObligationBriefSection.CurrentHeading, StringComparison.Ordinal);
        var history = prompt.IndexOf(RetryCorrectionObligationBriefSection.HistoryHeading, StringComparison.Ordinal);
        var old = prompt.IndexOf(oldFinding, StringComparison.Ordinal);
        Assert.True(current >= 0 && history > current && old > history,
            "Expected current obligation, explicit history heading, then the older finding.");
        var projection = prompt[current..history];
        Assert.Contains(candidate, projection, StringComparison.Ordinal);
        Assert.Contains("condition=source-size-ratchet", projection, StringComparison.Ordinal);
        Assert.DoesNotContain(oldFinding, projection, StringComparison.Ordinal);
    }
}
