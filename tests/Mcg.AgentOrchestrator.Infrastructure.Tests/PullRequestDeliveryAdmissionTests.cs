using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: private repository and state; no process environment or static hooks are changed.
public sealed class PullRequestDeliveryAdmissionTests
{
    [Fact]
    public void Pull_request_project_disables_both_admission_flags_but_direct_and_home_keep_them()
    {
        using var fixture = new PullRequestLandingTestFixture();
        var workspace = fixture.Workspace;
        var direct = Driver(workspace with { Delivery = ProjectDelivery.Direct });
        Assert.True(direct.AcceptanceCohortsEnabled);
        var pullRequest = Driver(workspace);
        Assert.False(pullRequest.AcceptanceCohortsEnabled);
        Assert.False(pullRequest.MergeTrainsEnabled);
        var home = Driver(workspace with { IsProjectScoped = false, ProjectName = OrchestratorWorkspace.DefaultProjectName });
        Assert.True(home.AcceptanceCohortsEnabled);
        // Preserve the ambient train feature switch; compare with direct rather than assuming it is enabled.
        Assert.Equal(direct.MergeTrainsEnabled, home.MergeTrainsEnabled);
    }

    private static ConductorDriver Driver(OrchestratorWorkspace workspace) => new(new AgentOrchestratorKernel(),
        workspace, new UnusedVerifier(), AgentCatalog.Default().Agents, WorkerProfileCatalog.Default());

    [Fact]
    public void Delivered_result_maps_to_verified_hold_with_the_exact_url_reason()
    {
        var reason = "delivered for review: " + PullRequestLandingTestFixture.Url;
        var decision = LandingCompletionPolicy.Evaluate(new LandingCompletionFacts
        {
            ApparatusRun = false, RunPassed = true, UnmetCriteriaCount = 0,
            LandingResultKind = "delivered-for-review", LandingReason = reason
        });
        Assert.Equal(LandingCompletionAction.Hold, decision.Action);
        Assert.Equal(reason, decision.Reason);
        Assert.Equal("landing-delivered-for-review", decision.DiscriminatingEvidence);
    }

    private sealed class UnusedVerifier : IGoalAcceptanceVerifier
    {
        public Task<AcceptanceVerificationResult> RunOwnedAsync(string worktreePath, GoalId? goalId,
            IReadOnlyList<string>? changedFiles, int? stableSlotIndex, DotnetBuildEnvironmentLease? stableSlotLease,
            IAcceptanceAttemptExecutionOwner executionOwner) => throw new InvalidOperationException("Admission must not run a gate.");
        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceOwnedAsync(string worktreePath, GoalId? goalId,
            string request, IAcceptanceFocusedVerificationOwner executionOwner, int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null, bool runBaselineArm = false) =>
            throw new InvalidOperationException("Admission must not run focused evidence.");
    }
}
