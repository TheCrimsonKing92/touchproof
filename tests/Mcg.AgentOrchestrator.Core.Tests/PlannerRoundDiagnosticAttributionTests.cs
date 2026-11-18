using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each case hydrates its own snapshot with fixed timestamps and no external state.
public sealed class PlannerRoundDiagnosticAttributionTests
{
    private const string Rejected = "CLASSIFIER rule=planner-output-contract-rejected; outcome_class=unknown-era";
    private const string Unrelated = "CLASSIFIER rule=unknown-failure; outcome_class=unknown-era";
    private static DateTimeOffset At(int hour, int minute = 0) => new(2026, 11, 1, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void HalfOpenWindows_DeduplicateAndKeepBoundaryWithNextDispatch()
    {
        var rounds = Rounds(Task(AgentRole.Planner, 1, 2, 3),
            Event(1, 30, Rejected), Event(1, 31, Rejected),
            Event(1, 40, "failed", ProgressKind.TaskFailed),
            Event(2, 0, "question", ProgressKind.HumanInputRequested),
            Event(2, 0, "question", ProgressKind.HumanInputRequested), Event(2, 30, Unrelated),
            Event(3, 0, Rejected), Event(3, 30, "failed", ProgressKind.TaskFailed));

        Assert.Equal(new bool?[] { true, false, true }, rounds.Select(r => r.PlannerContractRejected));
        Assert.Equal(new bool?[] { false, true, false }, rounds.Select(r => r.PlannerClarification));
        Assert.Equal(new[] { WorkerRoundStopCause.Failed, WorkerRoundStopCause.Clarification,
            WorkerRoundStopCause.Failed }, rounds.Select(r => r.StopCause));
    }

    [Fact]
    public void RejectionAndClarification_AreIndependentWithinOneDispatch()
    {
        var round = Assert.Single(Rounds(Task(AgentRole.Planner, 1), Event(1, 30, Rejected),
            Event(1, 31, "question", ProgressKind.HumanInputRequested)));
        Assert.True(round.PlannerContractRejected);
        Assert.True(round.PlannerClarification);
    }

    [Theory]
    [InlineData(ProgressKind.TaskFailed)]
    [InlineData(ProgressKind.TaskCancelled)]
    [InlineData(ProgressKind.TaskVerificationRecorded)]
    [InlineData(ProgressKind.OperatorTaskNote)]
    public void NonClassifierChannels_DoNotPromoteWorkerNarration(ProgressKind kind)
    {
        var task = Task(AgentRole.Planner, 1) with
        {
            LastVerification = new("fixture", "root", 1, Rejected, Rejected, At(1, 30))
        };
        var round = Assert.Single(Rounds(task, Event(1, 30, Rejected, kind),
            Event(1, 30, "worker wrote: " + Rejected),
            Event(1, 31, "question", ProgressKind.HumanInputRequested, "other-task")));
        Assert.Null(round.PlannerContractRejected);
        Assert.Null(round.PlannerClarification);
    }

    [Theory]
    [InlineData(AgentRole.Developer)]
    [InlineData(AgentRole.Researcher)]
    public void TrackedRole_IsAuthoritative(AgentRole role)
    {
        var round = Assert.Single(Rounds(Task(role, 1), Event(1, 30, Rejected),
            Event(1, 31, "question", ProgressKind.HumanInputRequested)));
        Assert.Null(round.PlannerContractRejected);
        Assert.Null(round.PlannerClarification);
    }

    [Fact]
    public void MissingHistory_IsUnavailableButUnrelatedClassifierIsKnownAbsence()
    {
        var rounds = Rounds(Task(AgentRole.Planner, 1, 2),
            Event(1, 30, "failed", ProgressKind.TaskFailed), Event(2, 30, Unrelated));
        Assert.Null(rounds[0].PlannerContractRejected);
        Assert.Null(rounds[0].PlannerClarification);
        Assert.False(rounds[1].PlannerContractRejected);
        Assert.False(rounds[1].PlannerClarification);
    }

    [Theory]
    [InlineData(1, null, null)] // Delayed old identity at the next dispatch boundary.
    [InlineData(0, null, null)] // Identity absent from this task's dispatch history.
    [InlineData(2, null, true)] // Consistent explicit identity belongs to the next dispatch.
    public void ExplicitIdentity_MustAgreeWithHalfOpenInterval(int identity, bool? first, bool? second)
    {
        var task = Task(AgentRole.Planner, 1, 2) with
        {
            VerificationHistory = [new("fixture", "root", 1, "", "", At(2), DispatchAttemptAt: At(identity))]
        };
        var rounds = Rounds(task, Event(2, 0, Rejected));
        Assert.Equal(first, rounds[0].PlannerContractRejected);
        Assert.Equal(second, rounds[1].PlannerContractRejected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConflictingIdentities_InvalidateBothClaimedAndTemporalWindows(bool request)
    {
        var task = Task(AgentRole.Planner, 1, 2) with
        {
            VerificationHistory =
            [
                new("fixture", "root", 1, "", "", At(2, 30), DispatchAttemptAt: At(1)),
                new("fixture", "root", 1, "", "", At(2, 30), DispatchAttemptAt: At(2))
            ]
        };
        var rounds = Rounds(task, Event(1, 30, Unrelated), Event(2, 20, Unrelated),
            Event(2, 30, request ? "question" : Rejected,
                request ? ProgressKind.HumanInputRequested : ProgressKind.TaskNote));
        Assert.All(rounds, r => Assert.Null(request ? r.PlannerClarification : r.PlannerContractRejected));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, null)]
    public void DuplicateStarts_PreserveLedgerButDoNotGuessAmbiguousIdentity(bool conflict, bool? rejected)
    {
        var dispatch = Dispatch(1);
        var task = Task(AgentRole.Planner, 1) with
        {
            DispatchHistory = [dispatch, conflict ? dispatch with { ModelName = "different" } : dispatch]
        };
        var round = Assert.Single(Rounds(task, Event(1, 30, Rejected)));
        Assert.Equal(1, round.RoundIndex);
        Assert.Equal(rejected, round.PlannerContractRejected);
    }

    private static TaskDispatchSnapshot Dispatch(int hour) => new("worker", "fixture", "root", At(hour));
    private static TaskSnapshot Task(AgentRole role, params int[] hours) =>
        new("planner", "fixture", role, WorkTaskStatus.Failed, null, null, null, [], null, null,
            DispatchHistory: hours.Select(Dispatch).ToArray());
    private static ProgressEventSnapshot Event(int hour, int minute, string message,
        ProgressKind kind = ProgressKind.TaskNote, string task = "planner") =>
        new("goal", task, kind, message, At(hour, minute));
    private static IReadOnlyList<WorkerRoundRecord> Rounds(TaskSnapshot task, params ProgressEventSnapshot[] events) =>
        WorkerRoundLedger.FromGoal(AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [new GoalSnapshot("goal", "fixture", GoalStatus.Cancelled, [task], events)], [])).Goals.Single());
}
