using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: process liveness and termination are injected; no child is launched.
public sealed class SupersededBriefRoundCancellerTests
{
    [Theory]
    [InlineData(AgentRole.Planner)]
    [InlineData(AgentRole.Researcher)]
    public void CancelSupersededRounds_RevisedScout_CancelsWithVersionNote(AgentRole role)
    {
        var (kernel, goal, task) = RunningTask(role);
        var oldVersion = task.LastDispatch!.BriefVersion;
        kernel.ReviseGoalBrief(goal.Id, "Revised brief");
        var killed = new List<int>();
        var runner = Runner(killed);
        var canceller = new SupersededBriefRoundCanceller(runner);

        Assert.Equal(1, canceller.CancelSupersededRounds(kernel));

        Assert.Equal([task.LastProcess!.ProcessId], killed);
        Assert.Equal(WorkTaskStatus.Cancelled, task.Status);
        Assert.True(task.WasCancelledByConductor);
        Assert.True(task.LastProcess.WasCancelled);
        Assert.True(task.LastProcess.WasCancelledByConductor);
        Assert.False(task.LastProcess.IsRunning);
        var note = Assert.Single(goal.Timeline.Where(evt => evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskNote && evt.Message.StartsWith("Brief revision cancelled")));
        Assert.Contains($"dispatch brief v{oldVersion}", note.Message);
        Assert.Contains($"current brief v{goal.AuthoritativeBrief.Version}", note.Message);
        Assert.Equal(GoalStatus.Active, goal.Status);
        Assert.Equal(0, canceller.CancelSupersededRounds(kernel));
        Assert.Single(killed);
    }

    [Fact]
    public void CancelSupersededRounds_RevisedPlanner_RequeuesOnCurrentBrief()
    {
        var (kernel, goal, task) = RunningTask(AgentRole.Planner);
        var priorDispatch = task.LastDispatch!;
        kernel.ReviseGoalBrief(goal.Id, "Revised Planner requirements");
        Assert.Equal(GoalStatus.Active, goal.Status);
        var runner = Runner([]);
        var canceller = new SupersededBriefRoundCanceller(runner);

        Assert.Equal(1, canceller.CancelSupersededRounds(kernel));
        Assert.Equal(GoalStatus.Active, goal.Status);
        Assert.Equal(1, runner.RequeueInterruptedDispatches(kernel));
        Assert.Equal(GoalStatus.Active, goal.Status);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Null(task.LastDispatch);
        Assert.Null(task.LastProcess);
        Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskRetried);

        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("worker", "next round", "C:\\goal",
                priorDispatch.DispatchedAt.AddSeconds(1)));

        Assert.Equal(goal.AuthoritativeBrief.Version, task.LastDispatch!.BriefVersion);
        Assert.Equal(goal.Objective, task.LastDispatch.BriefSnapshot);
        Assert.NotEqual(priorDispatch.BriefVersion, task.LastDispatch.BriefVersion);
        StartProcess(kernel, goal, task, 445);
        Assert.Equal(0, canceller.CancelSupersededRounds(kernel));
        Assert.Equal(WorkTaskStatus.Running, task.Status);
        Assert.Equal(GoalStatus.Active, goal.Status);
    }

    [Fact]
    public void CancelAndRequeueInterruptedDispatches_RevisedPlanner_RecoversInOnePass()
    {
        var (kernel, goal, task) = RunningTask(AgentRole.Planner);
        kernel.ReviseGoalBrief(goal.Id, "Revised brief");
        var killed = new List<int>();

        new SupersededBriefRoundCanceller(Runner(killed))
            .CancelAndRequeueInterruptedDispatches(kernel);

        Assert.Single(killed);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Null(task.LastProcess);
        Assert.Equal(GoalStatus.Active, goal.Status);
        Assert.Contains(goal.Timeline, evt => evt.Kind == ProgressKind.TaskRetried);
    }

    [Theory]
    [InlineData(AgentRole.Planner, AgentRole.Researcher)]
    [InlineData(AgentRole.Researcher, AgentRole.Planner)]
    public void CancelAndRequeueInterruptedDispatches_ScopedGoal_LeavesOtherScoutRunning(
        AgentRole watchedRole, AgentRole otherRole)
    {
        var (kernel, watchedGoal, watchedTask) = RunningTask(watchedRole);
        var (_, otherGoal, otherTask) = RunningTask(otherRole, kernel, pid: 445);
        kernel.ReviseGoalBrief(watchedGoal.Id, "Revised watched brief");
        kernel.ReviseGoalBrief(otherGoal.Id, "Revised other brief");
        var watchedProcess = watchedTask.LastProcess!;
        var otherProcess = otherTask.LastProcess!;
        var otherDispatch = otherTask.LastDispatch!;
        var otherTimelineCount = otherGoal.Timeline.Count;
        var killed = new List<int>();
        var runner = new BackgroundDispatchRunner(
            isStillRunning: pid => pid == otherProcess.ProcessId,
            tryKillOwnedProcess: pid => { killed.Add(pid); return true; });

        new SupersededBriefRoundCanceller(runner)
            .CancelAndRequeueInterruptedDispatches(kernel, onlyGoalId: watchedGoal.Id);

        Assert.Equal([watchedProcess.ProcessId], killed);
        Assert.Equal(WorkTaskStatus.Assigned, watchedTask.Status);
        Assert.Null(watchedTask.LastProcess);
        Assert.Contains(watchedGoal.Timeline, evt => evt.Kind == ProgressKind.TaskRetried);
        Assert.Equal(GoalStatus.Active, watchedGoal.Status);
        Assert.Equal(WorkTaskStatus.Running, otherTask.Status);
        Assert.Same(otherProcess, otherTask.LastProcess);
        Assert.Same(otherDispatch, otherTask.LastDispatch);
        Assert.True(otherProcess.IsRunning);
        Assert.False(otherProcess.WasCancelled);
        Assert.False(otherProcess.WasCancelledByConductor);
        Assert.False(otherTask.WasCancelledByConductor);
        Assert.Equal(otherTimelineCount, otherGoal.Timeline.Count);
        Assert.Equal(GoalStatus.Active, otherGoal.Status);
    }

    [Theory]
    [InlineData(AgentRole.Developer)]
    [InlineData(AgentRole.Tester)]
    [InlineData(AgentRole.Reviewer)]
    [InlineData(AgentRole.Ideation)]
    public void CancelSupersededRounds_RevisedOtherRole_LeavesProcessUntouched(AgentRole role)
    {
        var (kernel, goal, task) = RunningTask(role);
        var process = task.LastProcess!;
        kernel.ReviseGoalBrief(goal.Id, "Revised brief");
        var timelineCount = goal.Timeline.Count;
        var killed = new List<int>();

        Assert.Equal(0, new SupersededBriefRoundCanceller(Runner(killed))
            .CancelSupersededRounds(kernel));

        Assert.Empty(killed);
        Assert.Equal(WorkTaskStatus.Running, task.Status);
        Assert.Same(process, task.LastProcess);
        Assert.True(task.LastProcess.IsRunning);
        Assert.False(task.LastProcess.WasCancelled);
        Assert.Equal(timelineCount, goal.Timeline.Count);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.TaskCancelled);
    }

    [Theory]
    [InlineData(AgentRole.Planner)]
    [InlineData(AgentRole.Researcher)]
    public void CancelSupersededRounds_CurrentBrief_LeavesRoundRunning(AgentRole role)
    {
        var (kernel, goal, task) = RunningTask(role);
        var killed = new List<int>();
        Assert.Equal(goal.AuthoritativeBrief.Version, task.LastDispatch!.BriefVersion);

        Assert.Equal(0, new SupersededBriefRoundCanceller(Runner(killed))
            .CancelSupersededRounds(kernel));

        Assert.Empty(killed);
        Assert.Equal(WorkTaskStatus.Running, task.Status);
        Assert.True(task.LastProcess!.IsRunning);
    }

    [Theory]
    [InlineData(WorkTaskStatus.WaitingForHuman)]
    [InlineData(WorkTaskStatus.Completed)]
    public void CancelSupersededRounds_NonRunningScout_LeavesProcessUntouched(WorkTaskStatus status)
    {
        var (kernel, goal, task) = RunningTask(AgentRole.Planner);
        kernel.ReviseGoalBrief(goal.Id, "Revised brief");
        if (status == WorkTaskStatus.WaitingForHuman)
        {
            // Snapshot restoration resumes a waiting task unless it has a current pending request.
            kernel.RequestHumanInput(goal.Id, task.Id, "Which revised requirement applies?");
        }
        var snapshot = kernel.ExportSnapshot();
        var goalSnapshot = snapshot.Goals.Single();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            // Keep the goal eligible so this test isolates the task-status exclusion.
            Goals = [goalSnapshot with
            {
                Status = GoalStatus.Active,
                Tasks = [goalSnapshot.Tasks.Single() with { Status = status }]
            }]
        });
        goal = kernel.GetGoal(goal.Id);
        task = goal.Tasks.Single();
        Assert.Equal(GoalStatus.Active, goal.Status);
        Assert.Equal(status, task.Status);
        Assert.NotEqual(goal.AuthoritativeBrief.Version, task.LastDispatch!.BriefVersion);
        Assert.True(task.LastProcess!.IsRunning);
        var killed = new List<int>();

        Assert.Equal(0, new SupersededBriefRoundCanceller(Runner(killed))
            .CancelSupersededRounds(kernel));

        Assert.Empty(killed);
        Assert.Equal(status, task.Status);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.TaskCancelled);
    }

    [Fact]
    public void CancelSupersededRounds_InactiveGoal_LeavesRoundUntouched()
    {
        var (kernel, goal, task) = RunningTask(AgentRole.Planner);
        kernel.ReviseGoalBrief(goal.Id, "Revised brief");
        kernel.CancelGoal(goal.Id, "Operator stopped goal", allowLiveDispatches: true);
        var process = task.LastProcess;
        var killed = new List<int>();

        Assert.Equal(0, new SupersededBriefRoundCanceller(Runner(killed))
            .CancelSupersededRounds(kernel));

        Assert.Empty(killed);
        Assert.Same(process, task.LastProcess);
        Assert.False(task.LastProcess!.WasCancelled);
    }

    [Theory]
    [InlineData("dispatch")]
    [InlineData("snapshot")]
    [InlineData("process")]
    public void CancelSupersededRounds_MissingEvidence_LeavesRoundUntouched(string missing)
    {
        var (kernel, goal, _) = RunningTask(AgentRole.Planner);
        kernel.ReviseGoalBrief(goal.Id, "Revised brief");
        var snapshot = kernel.ExportSnapshot();
        var goalSnapshot = snapshot.Goals.Single();
        var taskSnapshot = goalSnapshot.Tasks.Single();
        taskSnapshot = missing switch
        {
            "dispatch" => taskSnapshot with { LastDispatch = null, DispatchHistory = [] },
            "snapshot" => taskSnapshot with
            {
                LastDispatch = taskSnapshot.LastDispatch! with
                {
                    BriefSnapshot = null, BriefSnapshotFromVersion = false
                }
            },
            "process" => taskSnapshot with { LastProcess = null },
            _ => throw new InvalidOperationException("Unknown missing evidence case.")
        };
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = [goalSnapshot with { Tasks = [taskSnapshot] }]
        });
        var task = kernel.GetGoal(goal.Id).Tasks.Single();
        Assert.Equal(WorkTaskStatus.Running, task.Status);
        if (missing == "snapshot") Assert.Null(task.LastDispatch!.BriefSnapshot);
        var killed = new List<int>();

        Assert.Equal(0, new SupersededBriefRoundCanceller(Runner(killed))
            .CancelSupersededRounds(kernel));

        Assert.Empty(killed);
        Assert.Equal(WorkTaskStatus.Running, task.Status);
        Assert.DoesNotContain(kernel.GetGoal(goal.Id).Timeline,
            evt => evt.Kind == ProgressKind.TaskCancelled);
    }

    [Fact]
    public void CancelSupersededRounds_ReapFailure_PropagatesWithoutCancellationNote()
    {
        var (kernel, goal, task) = RunningTask(AgentRole.Planner);
        kernel.ReviseGoalBrief(goal.Id, "Revised brief");
        var calls = 0;
        var failure = new IOException("Injected termination failure");
        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false,
            tryKillOwnedProcess: _ => { calls++; throw failure; });

        Assert.Same(failure, Assert.Throws<IOException>(() =>
            new SupersededBriefRoundCanceller(runner).CancelSupersededRounds(kernel)));

        Assert.Equal(1, calls);
        Assert.Equal(WorkTaskStatus.Running, task.Status);
        Assert.False(task.LastProcess!.WasCancelled);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.TaskCancelled ||
            evt.Message.StartsWith("Brief revision cancelled"));
    }

    private static BackgroundDispatchRunner Runner(List<int> killed) => new(
        isStillRunning: _ => false,
        tryKillOwnedProcess: pid => { killed.Add(pid); return true; });

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task) RunningTask(
        AgentRole role, AgentOrchestratorKernel? kernel = null, int pid = 444)
    {
        kernel ??= new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Original brief", [new TaskSpec(TaskId.New(), "Round", role)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.Single();
        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("worker", "round", "C:\\goal", DateTimeOffset.UtcNow));
        StartProcess(kernel, goal, task, pid);
        Assert.Equal(WorkTaskStatus.Running, task.Status);
        Assert.NotNull(task.LastDispatch!.BriefSnapshot);
        return (kernel, goal, task);
    }

    private static void StartProcess(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task, int pid)
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"superseded-round-{Guid.NewGuid():N}");
        var dispatch = task.LastDispatch!;
        kernel.RecordTaskProcessStarted(goal.Id, task.Id,
            new TaskProcessRecord(pid, dispatch.Command, dispatch.WorkingDirectory,
                missingPath + ".out", missingPath + ".err", missingPath + ".exit",
                dispatch.DispatchedAt, null, null, OwnedProcessIds: [pid]));
    }
}
