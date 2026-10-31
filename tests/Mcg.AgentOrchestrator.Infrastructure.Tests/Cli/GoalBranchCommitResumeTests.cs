using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each test owns the real repository/stores; the failure seam is instance-local.
public sealed class GoalBranchCommitResumeTests
{
    [Fact]
    public void SecondRebindFails_CommitSurvivesAndResumeCompletesWithoutDuplicateEffects()
    {
        using var fixture = new GoalBranchCommitTestFixture();
        fixture.StageChange();
        var failingStore = new GoalBranchCommitFailingIntentStore(fixture.Store);
        var beforeState = fixture.StateJson();

        var failure = fixture.Run(store: failingStore);

        Assert.Equal(1, failingStore.FailureCount);
        Assert.Equal(2, failure.ExitCode);
        Assert.Contains("injected-second-rebind-failure", failure.Error);
        Assert.Contains($"Resume: {fixture.ResumeCommand}", failure.Error);
        var committedHead = fixture.Head;
        Assert.NotEqual(fixture.PreviousHead, committedHead);
        Assert.Equal(fixture.PreviousHead, fixture.Git("rev-parse", "HEAD^"));
        Assert.Equal("1", fixture.Git("rev-list", "--count", $"{fixture.PreviousHead}..HEAD"));
        Assert.Contains($"New head: {committedHead}", failure.Output);
        Assert.Contains("Rebinds submitted: criterion-v1-0", failure.Output);
        Assert.Contains("Rebinds not submitted: criterion-v1-1", failure.Output);
        Assert.Equal(beforeState, fixture.StateJson());
        var partial = fixture.Intents();
        Assert.Equal(2, partial.Length);
        var noteIntent = Assert.Single(partial, intent => intent.Verb == OperatorIntentVerbs.GoalNote);
        var note = JsonSerializer.Deserialize<GoalNoteOperatorIntentPayload>(noteIntent.PayloadJson, OperatorIntentJson.Options)!;
        Assert.Contains("rebind incomplete", note.Text);
        Assert.Contains(fixture.ResumeCommand, note.Text);
        Assert.Contains(committedHead, note.Text);

        fixture.ApplyPending();
        Assert.Equal(committedHead, fixture.Goal.CriterionEvidenceObligations.Single(d => d.CriterionIndex == 0).ExpectedCandidateSha);
        Assert.Equal(fixture.PreviousHead, fixture.Goal.CriterionEvidenceObligations.Single(d => d.CriterionIndex == 1).ExpectedCandidateSha);
        Assert.Contains(fixture.Goal.Timeline, entry => entry.Message.StartsWith("Operator note:", StringComparison.Ordinal) &&
            entry.Message.Contains("rebind incomplete", StringComparison.Ordinal) &&
            entry.Message.Contains(fixture.ResumeCommand, StringComparison.Ordinal));

        var resumed = fixture.Run(resume: true);

        Assert.Equal(0, resumed.ExitCode);
        Assert.Empty(resumed.Error);
        Assert.Equal(committedHead, fixture.Head);
        Assert.Contains("criterion-v1-0 (already-bound-to-new-head)", resumed.Output);
        fixture.ApplyPending();
        Assert.All(fixture.Goal.CriterionEvidenceObligations.Where(d => d.CriterionIndex < 2),
            duty => Assert.Equal(committedHead, duty.ExpectedCandidateSha));
        Assert.Equal(4, fixture.Intents().Length);
        Assert.Contains(fixture.Goal.Timeline, entry => entry.Message.StartsWith("Operator note:", StringComparison.Ordinal) &&
            entry.Message.Contains("submitted for obligations: criterion-v1-0, criterion-v1-1", StringComparison.Ordinal));
        var completedState = fixture.StateJson();
        var completedIntents = fixture.Intents();

        var repeated = fixture.Run(resume: true);

        Assert.Equal(0, repeated.ExitCode);
        Assert.Empty(repeated.Error);
        Assert.Equal(committedHead, fixture.Head);
        Assert.Equal("1", fixture.Git("rev-list", "--count", $"{fixture.PreviousHead}..HEAD"));
        Assert.Equal(JsonSerializer.Serialize(completedIntents), JsonSerializer.Serialize(fixture.Intents()));
        Assert.Equal(completedState, fixture.StateJson());
        Assert.Contains("Rebinds submitted: none", repeated.Output);
        Assert.Contains("criterion-v1-1 (already-bound-to-new-head)", repeated.Output);
    }

    [Fact]
    public void Resume_OrdinaryCommit_RefusesWithoutIntentsOrStateChanges()
    {
        using var fixture = new GoalBranchCommitTestFixture();
        var state = fixture.StateJson();

        var result = fixture.Run(resume: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("ResumeNotApplicable", result.Error);
        Assert.Equal(fixture.PreviousHead, fixture.Head);
        Assert.Empty(fixture.Intents());
        Assert.Equal(state, fixture.StateJson());
    }
}
