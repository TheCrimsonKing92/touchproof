using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using TestGoal = OwnerConsoleGoalChangeSummaryTestGoal;
using Dialogs = OwnerConsoleGoalChangeSummaryDialogs;

internal static class OwnerConsoleGoalChangeSummaryFixtures
{
    internal static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(1);
    internal static readonly string Candidate = new('a', 40);
    internal static readonly string Basis = new('b', 40);
    internal static readonly string Next = new('c', 40);
    internal static CancellationToken Token => TestContext.Current.CancellationToken;

    internal const string Plan = "<!-- MCG_DURABLE_PLANNER_PLAN:BEGIN -->\n## Acceptance criteria mapping\n" +
        "1. disposition=planned; plan=Deliver expected behavior; owner=Acceptance\n" +
        "## Target seams and symbols\nsrc/Feature.cs\n<!-- MCG_DURABLE_PLANNER_PLAN:END -->";

    internal static TestGoal Fixture(string phase)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(new GoalId("11111111-summary"), "Readable goal\nExpected feature",
            [new(TaskId.New(), "Plan", AgentRole.Planner), new(TaskId.New(), "Build", AgentRole.Developer)]);
        var snapshot = kernel.ExportGoalSnapshot(goal.Id);
        var spec = new RefinedSpec("Expected feature", ["Deliver expected behavior"], VerificationClass.TestVerifiable, [], []);
        var hash = EffectiveAcceptanceCriteriaVersion.ComputeHash(spec, []);
        var changed = phase is "changed" or "failed" or "passed" or "landed";
        var worker = $"WORKER_RESULT:\nfiles: src/Feature.cs\ncommands: build\ntests: deferred - FeatureTests\ncommit: {Candidate}\n" +
            "blockers: none\nassigned_scope_complete: true\nmodel_fit: adequate\nskills: none\nconfidence: high\nsummary: Enable the feature\nEND_WORKER_RESULT";
        var tasks = snapshot.Tasks.Select(task => task with
        {
            Status = task.RequiredRole == AgentRole.Planner && phase != "pending" ? WorkTaskStatus.Completed : WorkTaskStatus.Assigned,
            LastDispatch = task.RequiredRole == AgentRole.Planner && phase != "pending" || task.RequiredRole == AgentRole.Developer && changed
                ? new TaskDispatchSnapshot("worker", "worker", "retained-artifact", At, BaseCommit: Basis,
                    ResultCommit: task.RequiredRole == AgentRole.Developer ? Candidate : null) : null,
            LastVerification = task.RequiredRole == AgentRole.Planner && phase != "pending" || task.RequiredRole == AgentRole.Developer && changed
                ? new TaskVerificationSnapshot("worker", "retained-artifact", 0, task.RequiredRole == AgentRole.Planner ? Plan : worker,
                    "", At.AddMinutes(1), DispatchStartedAt: At, AcceptanceCriteriaVersionHash: hash,
                    // Match persisted evidence: the complete authoritative stream equals StandardOutput.
                    AuthoritativeStandardOutputUnavailableReason: VerificationSnapshotText.StoredAsBoundedText) : null
        }).ToArray();
        return new TestGoal(snapshot with
        {
            Status = phase == "landed" ? GoalStatus.Completed : GoalStatus.Active, Tasks = tasks,
            RefinedSpecVersions = [new(1, new("Expected feature", ["Deliver expected behavior"], "TestVerifiable", [], []), At, 1)],
            CriterionEvidenceObligations = phase is "failed" or "passed" or "landed" ? [Obligation(Candidate, phase != "failed")] : null
        });
    }

    internal static CriterionEvidenceObligation Obligation(string candidate, bool passed) => new(
        "criterion-v1-0", 0, 1, "Deliver expected behavior", CriterionEvidenceOwner.Acceptance,
        passed ? CriterionEvidenceState.Satisfied : CriterionEvidenceState.Failed,
        CriterionEvidenceScopes.FullAcceptanceGate, "acceptance receipt", At.AddMinutes(2),
        candidate, "receipt-1", "executed acceptance", ExpectedCandidateSha: candidate);

    internal static GoalPromotionFact Promotion(Goal goal) => new(goal.Id.Value, Candidate, Basis, Next, At, GoalPromotionFact.SoloProvenance);
    internal static OwnerGoalChangeSummary Summary(string id) => new(id, 1, "criteria", null,
        new("pending", [], "brief"), new("unavailable", [], "Git"), new("unavailable", [], "worker"),
        new("pending", [], "receipts"), "landing not recorded");
    internal static OwnerConsoleViewModel Model(Goal goal) => new(new(ConductorLiveness.NotRunning, 1, 0, 0, null, 0), [],
        [new("11111111", "Readable epic", "Readable goal", "Active", "Developer", null, goal.Id.Value)], []);
    internal static OwnerConsoleScreenController Controller(OwnerConsoleHarness harness, Dialogs dialogs, OwnerGoalDetailQuery query) =>
        OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, dialogs, harness.State, harness.Tail,
            harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock, goalDetail: query);
}
