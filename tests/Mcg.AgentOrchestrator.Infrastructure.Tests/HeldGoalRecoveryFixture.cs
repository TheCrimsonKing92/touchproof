using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

internal sealed class HeldGoalRecoveryFixture : IDisposable
{
    internal string Root { get; } = ConductorDriverTests.CreateTempDirectory();
    internal AgentOrchestratorKernel Kernel { get; }
    internal Goal Goal { get; }
    internal ConductorDriver Driver { get; }
    internal ConductorParallelAcceptanceAttemptCoordinator Coordinator { get; }
    internal string CandidateSha { get; set; } = "abc1234";
    internal AcceptanceGateEngineSettings Settings { get; set; } = new();
    internal ConductorParallelAcceptanceAttempt Baseline { get; private set; } = null!;
    internal int ChangedFiles, Contexts, Siblings, SourceRequests, Outcomes, Retries;

    internal HeldGoalRecoveryFixture()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".orchestrator"));
        (Kernel, Goal) = SoftwareGoal();
        var developer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in Goal.Tasks.Where(task => task != reviewer))
            PassVerification(Kernel, Goal, task, hasCommittedChanges: task == developer);
        FailReviewerNeedsWork(Kernel, Goal, reviewer, "candidate RED requires a baseline",
            findings: [EvidenceFindingWithRequest("Find the failing test", id: "held-baseline", classes: ["ConductorDriverTests"])]);
        var launches = 0;
        Coordinator = new(Root, TrunkBranchName.Default,
            isProcessAlive: _ => true,
            launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7100 + ++launches),
            acquireStableSlotLease: (_, _) => null);
        Driver = MakeDriver(
            getPreReviewEvidenceContext: _ => { Contexts++; return NoPreReviewContext(CandidateSha); },
            getLandingFileScopes: _ => { ChangedFiles++; return ["src/Changed.cs"]; },
            resolveFindingEvidenceSiblingClasses: (_, _, _) => { Siblings++; return []; },
            getFindingEvidenceEngineSettings: _ => Settings,
            focusedEvidenceAttemptCoordinator: Coordinator, executionDirectory: Root,
            runFocusedEvidence: (_, request) => CandidateRedFindingEvidence(request, CandidateSha, "OutsideTests.Fails"),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            { Retries++; return Kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind); },
            recordFindingEvidenceRequest: (goalId, taskId, message) => Kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceRequestDispositions: (goalId, taskId, dispositions) => Kernel.RecordFindingEvidenceRequestDispositions(goalId, taskId, dispositions),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
            { Outcomes++; Kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt); });
        Driver.RecoveryCandidateRevision = _ => CandidateSha;
        Driver.FindingFailureSources = root => { SourceRequests++; return new(root); };
    }

    internal ConductorAdvanceResult Tick() => Driver.AdvanceOnce(Goal, ConductorAutonomyPolicy.Permissive);

    internal void StartBaseline()
    {
        Tick();
        var candidate = Assert.Single(Coordinator.GetUnreconciledAttempts([Goal.Id.Value]));
        Assert.False(candidate.FocusedEvidenceRunsBaselineArm);
        Complete(candidate, includeBaseline: false);
        var result = Tick();
        Baseline = Assert.Single(Coordinator.GetUnreconciledAttempts([Goal.Id.Value]));
        Assert.True(Baseline.FocusedEvidenceRunsBaselineArm);
        Assert.NotNull(Baseline.CandidateEvidenceBeforeBaseline);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(FindingBaselineAttemptObservation.Pending("finding-baseline-arm", Baseline.AttemptId).Evidence, held.Reason);
    }

    internal void Complete(ConductorParallelAcceptanceAttempt attempt, bool includeBaseline)
    {
        var candidate = ConductorParallelAcceptanceCandidate.Create(Goal, 0, [], CandidateSha, mainHeadSha: null);
        ConductorParallelAcceptanceAttemptCoordinator.RunPreReviewEvidenceAttempt(Coordinator, attempt, candidate,
            ConductorAutonomyPolicy.Permissive, (attemptCandidate, request, _, _, _) =>
            {
                var red = CandidateRedFindingEvidence(request, CandidateSha, "OutsideTests.Fails");
                if (!includeBaseline) red = red with { Arms = red.Arms!.Where(arm => arm.Arm == FindingEvidenceArm.Candidate).ToArray() };
                return ConductorParallelAcceptanceRunResult.Focused(attemptCandidate, red);
            });
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
