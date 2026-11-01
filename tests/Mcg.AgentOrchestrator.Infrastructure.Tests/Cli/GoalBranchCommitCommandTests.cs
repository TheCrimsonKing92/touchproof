using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: isolated real Git/SQLite resources and async-local console capture.
public sealed class GoalBranchCommitCommandTests
{
    [Fact]
    public void StagedChange_CommitsOnceAndAppliesOnlyEligibleRebindsAndOperatorNotes()
    {
        using var fixture = new GoalBranchCommitTestFixture();
        fixture.StageChange();
        File.WriteAllText(Path.Combine(fixture.Worktree, "untracked.txt"), "leave untracked");
        var beforeState = fixture.StateJson();
        var unchanged = fixture.Goal.CriterionEvidenceObligations.Where(d => d.CriterionIndex >= 2).ToArray();
        Assert.Empty(fixture.Intents());

        var result = fixture.Run();

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        var head = fixture.Head;
        Assert.NotEqual(fixture.PreviousHead, head);
        Assert.Equal("1", fixture.Git("rev-list", "--count", $"{fixture.PreviousHead}..HEAD"));
        Assert.Equal(fixture.PreviousHead, fixture.Git("rev-parse", "HEAD^"));
        Assert.Equal("Operator test fix\n\nOperator-Commit: goal-branch-commit",
            fixture.Git("log", "-1", "--format=%B").Replace("\r\n", "\n"));
        Assert.Equal("operator change", fixture.Git("show", "HEAD:change.txt"));
        Assert.Equal("", fixture.Git("diff", "--cached", "--name-only"));
        Assert.Equal("?? untracked.txt", fixture.Git("status", "--short"));
        // Durable submission succeeds before any conductor-owned state application.
        Assert.Equal(beforeState, fixture.StateJson());
        var intents = fixture.Intents();
        Assert.Equal(4, intents.Length);
        Assert.All(intents, intent => Assert.Equal(OperatorIntentStatus.Pending, intent.Status));
        Assert.Equal(2, intents.Count(intent => intent.Verb == OperatorIntentVerbs.CriterionEvidenceMap));
        Assert.Equal(2, intents.Count(intent => intent.Verb == OperatorIntentVerbs.GoalNote));
        foreach (var intent in intents)
            Assert.Contains($"Operator intent {intent.Id}: Pending", result.Output);
        Assert.Contains($"New head: {head}", result.Output);
        Assert.Contains("Rebinds submitted: criterion-v1-0, criterion-v1-1", result.Output);
        Assert.Contains("criterion-v1-2 (bound-to-other-sha)", result.Output);
        Assert.Contains("criterion-v1-3 (operator-owned)", result.Output);
        Assert.Contains("criterion-v1-4 (unbound)", result.Output);

        fixture.ApplyPending();

        var persisted = fixture.Repository.LoadGoalsAsync([fixture.GoalId]).GetAwaiter().GetResult().GetGoal(fixture.GoalId);
        var rebound = persisted.CriterionEvidenceObligations.Where(d => d.CriterionIndex < 2).ToArray();
        Assert.Equal(2, rebound.Length);
        Assert.All(rebound, duty =>
        {
            Assert.Equal(head, duty.ExpectedCandidateSha);
            Assert.Equal(CriterionEvidenceOwner.Acceptance, duty.Owner);
            Assert.Equal("acceptance:full-gate", duty.RequiredScope);
            Assert.Equal($"rebind-{fixture.GoalId.Value[..8]}-criterion-{duty.CriterionIndex}", duty.FindingStableId);
        });
        Assert.Equal(unchanged, persisted.CriterionEvidenceObligations.Where(d => d.CriterionIndex >= 2).ToArray());
        Assert.Contains(persisted.Timeline, entry => entry.Message.StartsWith("Operator note:", StringComparison.Ordinal) &&
            entry.Message.Contains(head, StringComparison.Ordinal) &&
            rebound.All(duty => entry.Message.Contains(duty.Id, StringComparison.Ordinal)));
    }
}
