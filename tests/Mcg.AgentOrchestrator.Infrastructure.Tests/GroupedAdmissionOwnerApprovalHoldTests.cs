using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static Mcg.AgentOrchestrator.Infrastructure.Tests.LandingExecutorTestsDenylistOwnerHold;

// Each scenario owns its repository and stores; isolate the shared landing git hooks.
[Collection(TestCollections.LandingGitRunner)]
public sealed class GroupedAdmissionOwnerApprovalHoldTests
{
    [Fact]
    public void Projection_PendingApproval_ExcludesUntilMatchingApprovalRecorded()
    {
        using var scenario = new PassedCohortReceiptApprovalHoldTests.Scenario();
        var goal = scenario.Goals[0];
        Assert.Equal(GoalStatus.Verified, goal.Status);
        Assert.Empty(scenario.Kernel.GetPendingHumanInput(goal.Id));
        var solo = Assert.IsType<GateReadyCandidateProjectionResult.Ready>(
            scenario.Driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Permissive));
        Assert.Equal(GoalLifecycleState.Verified, solo.Projection.LifecycleState);
        Assert.Equal(scenario.CandidateRevisions[0], solo.Projection.CandidateRevision);

        var progress = new List<string>();
        var candidates = Project();
        var held = Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(candidates[0].ProjectionResult);
        Assert.Equal(GateReadyCandidateExclusionReason.LandingHeld, held.Reason);
        Assert.Contains(LandingDenylistOwnerGate.HoldReasonPrefix, held.HoldReason!, StringComparison.Ordinal);
        Assert.Contains($"goal={goal.Id.Value[..8]}", held.HoldReason!, StringComparison.Ordinal);
        Assert.Contains(scenario.CandidateRevisions[0], held.HoldReason!, StringComparison.Ordinal);
        Assert.IsType<GateReadyCandidateProjectionResult.Ready>(candidates[1].ProjectionResult);
        Assert.Null(ConductorAcceptanceCohortSelector.Select(candidates).Selection);
        Assert.Empty(ConductorSpeculativeAcceptanceCohortPlanner.Plan(candidates).Cohorts);
        Assert.Contains(progress, line => line.StartsWith("GROUPED_ADMISSION_HELD ", StringComparison.Ordinal) &&
            line.Contains($"goal={goal.Id.Value[..8]} reason={held.HoldReason}", StringComparison.Ordinal));
        Assert.IsType<GateReadyCandidateProjectionResult.Ready>(
            scenario.Driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Permissive));

        Approve(scenario.Workspace, goal, scenario.CandidateRevisions[0]);
        progress.Clear();
        var approved = Project();
        var ready = Assert.IsType<GateReadyCandidateProjectionResult.Ready>(approved[0].ProjectionResult);
        Assert.Equal(solo.Projection.CandidateRevision, ready.Projection.CandidateRevision);
        Assert.Equal(solo.Projection.MainRevision, ready.Projection.MainRevision);
        Assert.Equal(scenario.Goals.Select(member => member.Id),
            ConductorAcceptanceCohortSelector.Select(approved).Selection!.Members.Select(member => member.GoalId));
        Assert.Equal(scenario.Goals.Select(member => member.Id),
            Assert.Single(ConductorSpeculativeAcceptanceCohortPlanner.Plan(approved).Cohorts)
                .Members.Select(member => member.GoalId));
        Assert.Empty(progress);

        ConductorSpeculativeAcceptanceCandidate[] Project() => GroupedAdmissionCandidateProjection.ProjectAll(
            scenario.Goals, scenario.Driver, ConductorAutonomyPolicy.Permissive, scenario.Kernel, 1, progress.Add);
    }

    [Fact]
    public void Projection_NoOwnerApprovalHold_PreservesOrdinaryEligibility()
    {
        using var scenario = new PassedCohortReceiptApprovalHoldTests.Scenario(protectedMemberIndex: null);
        var progress = new List<string>();
        var candidates = GroupedAdmissionCandidateProjection.ProjectAll(scenario.Goals, scenario.Driver,
            ConductorAutonomyPolicy.Permissive, scenario.Kernel, 1, progress.Add);

        foreach (var (goal, candidate) in scenario.Goals.Zip(candidates))
        {
            var solo = Assert.IsType<GateReadyCandidateProjectionResult.Ready>(
                scenario.Driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Permissive));
            var grouped = Assert.IsType<GateReadyCandidateProjectionResult.Ready>(candidate.ProjectionResult);
            Assert.Equal(solo.Projection.CandidateRevision, grouped.Projection.CandidateRevision);
            Assert.Equal(solo.Projection.LandingPaths, grouped.Projection.LandingPaths);
        }
        Assert.Equal(scenario.Goals.Select(goal => goal.Id),
            ConductorAcceptanceCohortSelector.Select(candidates).Selection!.Members.Select(member => member.GoalId));
        Assert.Equal(scenario.Goals.Select(goal => goal.Id),
            Assert.Single(ConductorSpeculativeAcceptanceCohortPlanner.Plan(candidates).Cohorts)
                .Members.Select(member => member.GoalId));
        Assert.Empty(progress);
    }
}
