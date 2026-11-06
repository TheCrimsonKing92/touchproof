using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.Tests.LandingExecutorTestsDenylistOwnerHold;

// Parallel-safe: private repositories, decision stores and instance caches; no static runner overrides.
public sealed class PassedCohortApprovalHoldEvaluationCacheTests(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact]
    public void UnchangedInputs_ReuseActualEvaluation() => CheckInvalidation(null);

    [Fact]
    public void CandidateRevisionChanged_Recomputes() => CheckInvalidation("candidate");

    [Fact]
    public void DenylistContentChanged_Recomputes() => CheckInvalidation("denylist");

    [Fact]
    public void RecordedApprovalsChanged_RecomputesAndApproves() => CheckInvalidation("approval");

    [Fact]
    public void MainRevisionChanged_Recomputes() => CheckInvalidation("main");

    [Fact]
    public void LandingPathsChanged_Recomputes() => CheckInvalidation("paths");

    [Fact]
    public void ApprovalArrivingDuringEvaluation_DoesNotPublishOldDenial()
    {
        var goal = CreateVerifiedSimpleGoal(new AgentOrchestratorKernel(), "Moving approval");
        using var fixture = new AdditiveConflictGitFixture(goalId: goal.Id);
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Repository);
        var decisions = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var cache = new PassedCohortApprovalHoldEvaluationCache();
        cache.Evaluate(fixture.Repository, Denylist("protected"), decisions, goal.Id.Value,
            fixture.MainHead, fixture.OriginalHead, [AdditiveConflictGitFixture.WidgetPath], () =>
            {
                Approve(workspace, goal, fixture.OriginalHead);
                return new([], "v1:old", false, "old denial");
            });
        Assert.Equal(0, cache.Count);
        var live = cache.Evaluate(fixture.Repository, Denylist("protected"), decisions, goal.Id.Value,
            fixture.MainHead, fixture.OriginalHead, [AdditiveConflictGitFixture.WidgetPath]);
        Assert.True(live.Approved);
        Assert.Equal(1, cache.Count);
    }

    private static void CheckInvalidation(string? change)
    {
        var goal = CreateVerifiedSimpleGoal(new AgentOrchestratorKernel(), "Approval cache");
        using var fixture = new AdditiveConflictGitFixture(goalId: goal.Id);
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Repository);
        var decisions = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var denylist = Denylist("protected");
        var main = fixture.MainHead;
        var candidate = fixture.OriginalHead;
        string[] paths = [AdditiveConflictGitFixture.WidgetPath];
        var cache = new PassedCohortApprovalHoldEvaluationCache();
        var calls = 0;
        LandingDenylistOwnerGate.Result Evaluate() => cache.Evaluate(fixture.Repository, denylist, decisions,
            goal.Id.Value, main, candidate, paths, () =>
            {
                calls++;
                return LandingDenylistOwnerGate.Evaluate(fixture.Repository, denylist, decisions,
                    goal.Id.Value, main, candidate, paths);
            });
        var first = Evaluate();
        Assert.False(first.Approved);
        Assert.StartsWith("v1:", first.Fingerprint);
        Assert.Same(first, Evaluate());
        Assert.Equal(1, calls);
        switch (change)
        {
            case "candidate": candidate = fixture.MainHead; break;
            case "denylist": denylist = Denylist("changed-rule"); break;
            case "approval": Approve(workspace, goal, candidate); break;
            case "main": main = fixture.OriginalHead; break;
            case "paths": paths = ["tests/Ordinary.cs"]; break;
        }
        var next = Evaluate();
        Assert.Equal(change is null ? 1 : 2, calls);
        if (change is null) Assert.Same(first, next);
        else Assert.NotSame(first, next);
        if (change == "approval") Assert.True(next.Approved);
        if (change == "denylist") Assert.Equal("changed-rule", Assert.Single(next.Matches).RuleId);
    }

    [Fact]
    public void Capacity_EvictsLeastRecent_AndChangedGoalMisses()
    {
        var root = CreateTempDirectory("approval-cache-bound");
        try
        {
            var decisions = CollaborationItemStore.ForDirectory(Path.Combine(root, ".orchestrator"));
            var cache = new PassedCohortApprovalHoldEvaluationCache(2);
            var calls = 0;
            var goalId = GoalId.New().Value;
            LandingDenylistOwnerGate.Result Read(string candidate, string? id = null) => cache.Evaluate(root,
                Denylist("protected"), decisions, id ?? goalId, new string('a', 40), candidate, [], () =>
                {
                    calls++;
                    return new([], string.Empty, true, string.Empty);
                });
            var first = new string('b', 40);
            var second = new string('c', 40);
            var third = new string('d', 40);
            var original = Read(first);
            Read(second);
            Assert.Same(original, Read(first));
            Read(third);
            Assert.Equal(2, cache.Count);
            Read(second);
            Assert.Equal(4, calls);
            Read(second, GoalId.New().Value);
            Assert.Equal(5, calls);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void DenylistMemo_EvictsAndBindsRepositoryAndFullMain()
    {
        var cache = new PassedCohortApprovalHoldEvaluationCache(2);
        var calls = 0;
        LandingDenylist Read(string directory, string main) => cache.LoadDenylist(directory, main, () =>
        {
            calls++;
            return Denylist("protected");
        });
        var first = new string('a', 40);
        var second = new string('b', 40);
        var original = Read("repo", first);
        Read("repo", second);
        Assert.Same(original, Read("repo", first));
        Read("other", first);
        Assert.Equal(2, cache.DenylistCount);
        Read("repo", second);
        Assert.Equal(4, calls);
        Read("repo", "abc1234");
        Read("repo", "abc1234");
        Assert.Equal(6, calls);
    }

    [Theory]
    [InlineData("unavailable", "read failed")]
    [InlineData("v1:fingerprint", "hold diagnostic=git failed")]
    public void FailedObservation_IsRetried(string fingerprint, string reason)
    {
        var root = CreateTempDirectory("approval-cache-transient");
        try
        {
            var cache = new PassedCohortApprovalHoldEvaluationCache();
            var decisions = CollaborationItemStore.ForDirectory(Path.Combine(root, ".orchestrator"));
            var calls = 0;
            var goalId = GoalId.New().Value;
            for (var i = 0; i < 2; i++) cache.Evaluate(root, Denylist("protected"), decisions,
                goalId, new string('a', 40), new string('b', 40), [], () =>
                {
                    calls++;
                    return new([], fingerprint, false, reason);
                });
            Assert.Equal(2, calls);
            Assert.Equal(0, cache.Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static LandingDenylist Denylist(string id) => LandingDenylist.Parse(
        $$"""{"version":1,"rules":[{"id":"{{id}}","reason":"Owner review","paths":["src/Widget.cs"]}]}""", "test");
}
