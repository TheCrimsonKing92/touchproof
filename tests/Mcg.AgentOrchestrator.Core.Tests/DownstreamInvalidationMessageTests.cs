using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: isolated in-memory goals and clocks; repository sources are read only.
public sealed class DownstreamInvalidationMessageTests
{
    [Theory]
    [InlineData("changed candidate from a to b", "abc123", "a later verification superseded that stored verdict")]
    [InlineData("", "unknown", "")]
    [InlineData(" reason; punctuation. ", " candidate ", " reason; punctuation. ")]
    public void Builders_SuppliedValues_PreserveOriginalText(
        string invalidationReason, string reviewedCandidate, string reason)
    {
        var downstreamRole = AgentRole.Tester;
        var upstreamRole = AgentRole.Developer;
        var upstreamTaskId = new TaskId("abcd1234567890abcdef1234567890ab");

        Assert.Equal(
            $"Invalidated {downstreamRole} task because upstream {upstreamRole} task {upstreamTaskId.Value[..8]} was retried.",
            DownstreamInvalidationMessage.UpstreamRetried(downstreamRole, upstreamRole, upstreamTaskId));
        Assert.Equal(
            $"Invalidated {downstreamRole} task because retried upstream {upstreamRole} task {upstreamTaskId.Value[..8]} {invalidationReason}.",
            DownstreamInvalidationMessage.RetriedWithReason(downstreamRole, upstreamRole, upstreamTaskId, invalidationReason));
        Assert.Equal(
            $"Invalidated superseded {downstreamRole} verdict for reviewed candidate {reviewedCandidate} because {reason}; historical findings remain in verification history.",
            DownstreamInvalidationMessage.SupersededVerdict(downstreamRole, reviewedCandidate, reason));
    }

    [Theory]
    [MemberData(nameof(BuilderMessages))]
    public void IsInvalidation_BuilderOutput_RequiresTaskRetried(string message)
    {
        Assert.True(DownstreamInvalidationMessage.IsInvalidation(Event(message)));
        Assert.False(DownstreamInvalidationMessage.IsInvalidation(
            Event(message) with { Kind = ProgressKind.TaskNote }));
    }

    [Theory]
    [InlineData("Invalidated Tester task because upstream Developer task abcd1234 was retried.")]
    [InlineData("Invalidated Tester task because retried upstream Developer task abcd1234 changed candidate from a to b.")]
    [InlineData("Invalidated superseded Tester verdict for reviewed candidate a because a later verification superseded that stored verdict; historical findings remain in verification history.")]
    public void IsInvalidation_PersistedShapes_RecognizesLegacyText(string message)
    {
        Assert.True(DownstreamInvalidationMessage.IsInvalidation(Event(message)));
    }

    [Theory]
    [InlineData("Retried Tester task because verification failed.")]
    [InlineData("Invalidated latest")]
    [InlineData("Invalidated Tester task because upstream changed")]
    [InlineData("invalidated Tester task because upstream Developer task abcd1234 was retried.")]
    [InlineData("Invalidated Tester task because upstream Developer task abcd1234 was retried")]
    [InlineData("Invalidated Tester task because retried upstream Developer task abcd1234 changed candidate")]
    [InlineData("Invalidated superseded Tester verdict for reviewed candidate a because changed")]
    [InlineData("Invalidated Tester verdict for reviewed candidate a because changed; historical findings remain in verification history.")]
    public void IsInvalidation_UnsupportedText_DoesNotRecognize(string message)
    {
        Assert.False(DownstreamInvalidationMessage.IsInvalidation(Event(message)));
    }

    [Fact]
    public void BuildTaskBrief_ThreeInvalidations_CountsOnlyOneRealRetry()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var developer = new TaskSpec(TaskId.New(), "Implement.", AgentRole.Developer);
        var tester = new TaskSpec(TaskId.New(), "Test.", AgentRole.Tester);
        var reviewer = new TaskSpec(TaskId.New(), "Review.", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Count actual retry rounds", [developer, tester, reviewer]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.RetryTask(goal.Id, reviewer.Id, "verification failed");
        var messages = new[]
        {
            DownstreamInvalidationMessage.UpstreamRetried(reviewer.RequiredRole, developer.RequiredRole, developer.Id),
            DownstreamInvalidationMessage.RetriedWithReason(reviewer.RequiredRole, developer.RequiredRole, developer.Id,
                "changed candidate from a to b"),
            DownstreamInvalidationMessage.SupersededVerdict(reviewer.RequiredRole, "a", "a later verification superseded that stored verdict")
        };
        foreach (var message in messages)
        {
            clock.Advance();
            kernel.GetGoal(goal.Id).Append(new ProgressEvent(
                goal.Id, reviewer.Id, ProgressKind.TaskRetried, message, clock.UtcNow));
        }

        var brief = kernel.BuildTaskBrief(goal.Id, reviewer.Id).Content;

        Assert.Contains("Most recent retry: Retry 1 of 1;", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("Retry 2 of", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("Retry 3 of", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("Retry 4 of", brief, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BuilderMessages))]
    public void LatestResetAt_EachInvalidation_StartsFreshStreak(string message)
    {
        var clock = new FakeClock();
        var task = new TaskSpec(TaskId.New(), "Verify.", AgentRole.Tester);
        task.RecordVerification(IncompleteRecord(clock));
        clock.Advance();
        task.RecordVerification(IncompleteRecord(clock));
        clock.Advance();
        var reset = Event(message) with { TaskId = task.Id, OccurredAt = clock.UtcNow };
        clock.Advance();
        task.RecordVerification(IncompleteRecord(clock));

        Assert.Equal(3, TerminalContractRepairStreak.Count([], task));
        Assert.Equal(reset.OccurredAt, TerminalContractRepairStreak.LatestResetAt([reset], task.Id));
        Assert.Equal(1, TerminalContractRepairStreak.Count([reset], task));
        Assert.Null(TerminalContractRepairStreak.LatestResetAt(
            [reset with { TaskId = TaskId.New() }], task.Id));
    }

    [Fact]
    public void SourceLiterals_AllProductionFiles_HaveOneOwner()
    {
        var root = VerifiedRepositoryRoot.Find();
        var owners = SourceFiles(Path.Combine(root, "src"))
            .Where(path => File.ReadAllText(path).Contains("\"Invalidated ", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToArray();

        Assert.Equal("src/Mcg.AgentOrchestrator.Core/Application/DownstreamInvalidationMessage.cs",
            Assert.Single(owners));
    }

    public static IEnumerable<object[]> BuilderMessages()
    {
        var id = new TaskId("abcd1234567890abcdef1234567890ab");
        yield return [DownstreamInvalidationMessage.UpstreamRetried(AgentRole.Tester, AgentRole.Developer, id)];
        yield return [DownstreamInvalidationMessage.RetriedWithReason(AgentRole.Tester, AgentRole.Developer, id,
            "changed candidate from a to b")];
        yield return [DownstreamInvalidationMessage.SupersededVerdict(AgentRole.Tester, "a", "superseded")];
        yield return [DownstreamInvalidationMessage.RetriedWithReason(AgentRole.Reviewer, AgentRole.Developer, id, "")];
        yield return [DownstreamInvalidationMessage.SupersededVerdict(AgentRole.Reviewer, "unknown", " reason; punctuation. ")];
    }

    private static ProgressEvent Event(string message) =>
        new(GoalId.New(), TaskId.New(), ProgressKind.TaskRetried, message, new FakeClock().UtcNow);

    private static TaskVerificationRecord IncompleteRecord(FakeClock clock) =>
        new("fixture", @"C:\repo", 0, "", "", clock.UtcNow,
            CompletionVerdictRule: TaskOutcomeRules.VerificationTerminalContractIncomplete.Token);

    private static IEnumerable<string> SourceFiles(string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory))
            yield return path;
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (Path.GetFileName(child) is "bin" or "obj" or ".scratch" or ".orchestrator-prototype" or
                "TestResults" or "playwright-report")
                continue;
            foreach (var path in SourceFiles(child))
                yield return path;
        }
    }
}
