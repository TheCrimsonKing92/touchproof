using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: unique repository/stores and async-local console capture.
public sealed class GoalBranchCommitWaitListingTests
{
    [Fact]
    public void Commit_OpenEvidenceWaits_ListsCompleteIdsAndQuestionsWithoutCompletingThem()
    {
        using var fixture = new GoalBranchCommitTestFixture();
        var first = fixture.Kernel.RequestHumanInput(fixture.GoalId, null,
            "Confirm the reviewer diff evidence?", HumanWaitKind.ProspectiveAcceptanceEvidence);
        var second = fixture.Kernel.RequestHumanInput(fixture.GoalId, null,
            "Confirm the developer's complete deferred-test list?\nInclude every class and its receipt.",
            HumanWaitKind.ProspectiveAcceptanceEvidence);
        var completed = fixture.Kernel.RequestHumanInput(fixture.GoalId, null,
            "Previously answered evidence?", HumanWaitKind.ProspectiveAcceptanceEvidence);
        fixture.Kernel.SubmitHumanInput(completed.Id, "Already recorded", supersedeSiblings: false);
        var otherKind = fixture.Kernel.RequestHumanInput(fixture.GoalId, null, "Unrelated clarification?", HumanWaitKind.SpecClarification);
        var otherGoal = fixture.Kernel.CreateGoal("Another goal");
        var foreign = fixture.Kernel.RequestHumanInput(otherGoal.Id, null, "Other goal evidence?", HumanWaitKind.ProspectiveAcceptanceEvidence);
        fixture.Save();
        fixture.StageChange();
        Assert.Equal(2, fixture.Kernel.GetPendingHumanInput(fixture.GoalId)
            .Count(wait => wait.Kind == HumanWaitKind.ProspectiveAcceptanceEvidence));
        Assert.True(completed.IsCompleted);
        var beforeState = fixture.StateJson();

        var result = fixture.Run();

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        Assert.Contains("Open ProspectiveAcceptanceEvidence waits: 2", result.Output);
        foreach (var wait in new[] { first, second })
            Assert.Contains($"{wait.Id.Value}: {wait.Question}", result.Output);
        foreach (var excluded in new[] { completed, otherKind, foreign })
        {
            Assert.DoesNotContain(excluded.Id.Value, result.Output);
            Assert.DoesNotContain(excluded.Question, result.Output);
        }
        var ordered = new[] { first, second }.OrderBy(wait => wait.RequestedAt)
            .ThenBy(wait => wait.Id.Value, StringComparer.Ordinal).ToArray();
        Assert.True(result.Output.IndexOf(ordered[0].Id.Value, StringComparison.Ordinal) <
            result.Output.IndexOf(ordered[1].Id.Value, StringComparison.Ordinal));
        Assert.Equal(beforeState, fixture.StateJson());
        var persisted = fixture.Repository.LoadGoalsAsync([fixture.GoalId]).GetAwaiter().GetResult();
        Assert.Equal(2, persisted.GetPendingHumanInput(fixture.GoalId)
            .Count(wait => wait.Kind == HumanWaitKind.ProspectiveAcceptanceEvidence));
        Assert.All(persisted.HumanInputRequests.Where(wait => wait.Id == first.Id || wait.Id == second.Id),
            wait => Assert.False(wait.IsCompleted));
    }

    [Fact]
    public void Commit_NoEvidenceWaits_PrintsExplicitNone()
    {
        using var fixture = new GoalBranchCommitTestFixture();
        fixture.StageChange();

        var result = fixture.Run();

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        Assert.Contains("Open ProspectiveAcceptanceEvidence waits: none", result.Output);
    }
}
