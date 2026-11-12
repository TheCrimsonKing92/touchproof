using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Private state and process records only: no workers, git, gates or builds.
internal sealed class DispatchStartRecoveryTestFixture
{
    internal sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-11-11T21:30:00Z");
    }

    internal Clock Time { get; } = new();
    internal string Root { get; } = InfrastructureTestSupport.CreateTempDirectory();
    internal OrchestratorWorkspace Workspace { get; }
    internal AgentOrchestratorKernel Kernel { get; private set; }
    internal GoalId GoalId { get; }
    internal TaskId TaskId { get; }
    internal Goal Goal => Kernel.GetGoal(GoalId);
    internal TaskSpec Task => Kernel.GetTask(GoalId, TaskId);

    internal DispatchStartRecoveryTestFixture(bool solo = true)
    {
        Workspace = OrchestratorWorkspace.ForDirectory(Root, Root);
        Kernel = new AgentOrchestratorKernel(Time);
        var task = new TaskSpec(TaskId.New(), "Repair a recorded finding", AgentRole.Developer);
        var goal = Kernel.CreateGoal("Retry start recovery", [task]);
        GoalId = goal.Id;
        TaskId = task.Id;
        Kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec("Repair", ["Repair the finding"], VerificationClass.TestVerifiable, [], []));
        Kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        if (solo)
        {
            Kernel.RecordCriterionRetryFeedback(goal.Id, task.Id, ["ATTRIBUTED_FAILURE_ROUTE source=solo finding=fixture"]);
            Kernel.RetryTaskAutomatically(goal.Id, task.Id, "Repair the solo finding", RetryCause.NewTestFinding);
        }
    }

    internal RetryContextFingerprint Fingerprint(string candidate = "candidate", PaidRouteClassification route = PaidRouteClassification.NonPaid) =>
        RetryContextFingerprintBuilder.Build(new(GoalId.Value, TaskId.Value, Task.RequiredRole,
            "OpenAI", "gpt-6.1-sol", route, candidate, "criteria", [], [], [], [], "base", "main"));

    internal TaskDispatchRecord Prepare(bool fingerprint = true, PaidRouteClassification route = PaidRouteClassification.NonPaid)
    {
        Time.UtcNow = Time.UtcNow.AddSeconds(10);
        Kernel.RecordTaskDispatch(GoalId, TaskId, new("fixture-worker", "fixture-command", Root, Time.UtcNow,
            ProviderName: "OpenAI", ModelName: "gpt-6.1-sol", RetryContextFingerprint: fingerprint ? Fingerprint(route: route) : null,
            PaidRoute: route));
        return Task.LastDispatch!;
    }

    internal void ClaimCurrent(bool confirmed = false)
    {
        var dispatch = Task.LastDispatch!;
        var reserved = RetryAdmissionSnapshotReservation.Apply(Kernel.ExportGoalSnapshot(GoalId), TaskId,
            dispatch.RetryContextFingerprint!, dispatch.PaidRoute, Task.PendingRetryCause, dispatch,
            Time.UtcNow, "fixture-owner", Time.UtcNow.AddMinutes(1));
        Assert.True(reserved.Admission.AllowsProcessStart);
        var claimed = RetryAdmissionSnapshotStartClaim.Apply(reserved.Snapshot, TaskId,
            dispatch.DispatchedAt, "fixture-owner", Time.UtcNow);
        Assert.True(claimed.Claimed);
        Kernel.ReplaceGoalWithSnapshot(claimed.Snapshot);
        if (confirmed)
        {
            var confirmation = RetryAdmissionSnapshotStartConfirmation.Apply(Kernel.ExportGoalSnapshot(GoalId), TaskId,
                dispatch.DispatchedAt, "fixture-owner", Time.UtcNow);
            Assert.True(confirmation.Claimed);
            Kernel.ReplaceGoalWithSnapshot(confirmation.Snapshot);
        }
    }

    internal void SettleAndAnswer()
    {
        var dispatch = Task.LastDispatch!;
        Time.UtcNow = Time.UtcNow.AddSeconds(1);
        Kernel.RecordTaskProcessStarted(GoalId, TaskId,
            new(4242, dispatch.Command, Root, "out.log", "err.log", "exit.txt", Time.UtcNow, null, null));
        Time.UtcNow = Time.UtcNow.AddSeconds(1);
        var snapshot = Kernel.ExportGoalSnapshot(GoalId);
        Kernel.ReplaceGoalWithSnapshot(snapshot with { Tasks = snapshot.Tasks.Select(task => task with
        {
            LastProcess = task.LastProcess! with { CompletedAt = Time.UtcNow, ExitCode = 1 }
        }).ToArray() });
        var request = Kernel.RequestHumanInput(GoalId, TaskId, "Confirm the repair scope", HumanWaitKind.SpecClarification);
        Kernel.SubmitHumanInput(request.Id, "Use the clarified scope");
        Assert.Equal(WorkTaskStatus.Assigned, Task.Status);
    }

    internal void Save() => InfrastructureTestSupport.CreateMigratedStateRepository(Workspace.SqliteStatePath)
        .SaveAsync(Kernel).GetAwaiter().GetResult();

    internal void Restart()
    {
        Save();
        Kernel = AgentOrchestratorKernel.FromSnapshot(
            new SqliteOrchestratorStateRepository(Workspace.SqliteStatePath).LoadAsync().GetAwaiter().GetResult().ExportSnapshot(), Time);
    }
}
