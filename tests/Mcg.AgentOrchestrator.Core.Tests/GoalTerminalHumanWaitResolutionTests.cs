using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

public sealed class GoalTerminalHumanWaitResolutionTests
{
    [Theory]
    [InlineData(nameof(AgentOrchestratorKernel.CancelGoal), GoalStatus.Cancelled, false)]
    [InlineData(nameof(AgentOrchestratorKernel.SupersedeGoal), GoalStatus.Superseded, false)]
    [InlineData(nameof(AgentOrchestratorKernel.CompleteGoal), GoalStatus.Completed, false)]
    [InlineData(nameof(AgentOrchestratorKernel.CompleteGoalFromMergeEvidence), GoalStatus.Completed, false)]
    [InlineData(nameof(AgentOrchestratorKernel.CompleteGoalFromMergeEvidence), GoalStatus.Completed, true)]
    public void TerminalTransition_ResolvesOnlyOpenDismissibleRequests(
        string transition, GoalStatus outcome, bool recoveringFromReceipt)
    {
        var kernel = new AgentOrchestratorKernel();
        var subject = CreateGoalWithProtectedRequests(kernel);
        var control = CreateGoalWithProtectedRequests(kernel);
        var unrelated = CreateGoalWithProtectedRequests(kernel);
        var dismissible = RequestDismissibleWaits(kernel, subject);
        RequestDismissibleWaits(kernel, unrelated);

        // A linked blocking wait moves the task to WaitingForHuman. Seed equivalent
        // task states for both goals, including terminal tasks for the completion paths.
        var snapshot = kernel.ExportSnapshot();
        var transitionSnapshot = snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal.Id == unrelated.Id.Value
                ? goal
                : goal with
                {
                    Status = outcome == GoalStatus.Completed ? GoalStatus.Verifying : GoalStatus.Active,
                    Tasks =
                    [
                        goal.Tasks[0] with
                        {
                            Status = outcome == GoalStatus.Completed ? WorkTaskStatus.Completed : WorkTaskStatus.WaitingForHuman
                        },
                        goal.Tasks[1] with { Status = WorkTaskStatus.Cancelled }
                    ]
                }).ToArray()
        };
        kernel = AgentOrchestratorKernel.FromSnapshot(transitionSnapshot);
        subject = kernel.GetGoal(subject.Id);
        control = kernel.GetGoal(control.Id);
        var before = kernel.ExportSnapshot();
        var subjectTimelineLength = subject.Timeline.Count;
        var controlTimelineLength = control.Timeline.Count;
        var subjectRequests = RequestsFor(before, subject.Id);
        var protectedWait = Assert.Single(subjectRequests, request => !request.IsCompleted && request.IsDismissible == false);
        var answeredWait = Assert.Single(subjectRequests, request => request.IsCompleted && request.IsDismissible == true);
        Assert.Equal("Original answer", answeredWait.Answer);
        Assert.NotNull(answeredWait.AnsweredAt);
        Assert.Equal(2, subjectRequests.Count(request => !request.IsCompleted && request.IsDismissible == true));
        Assert.DoesNotContain(RequestsFor(before, control.Id), request => !request.IsCompleted && request.IsDismissible == true);
        var unrelatedRequests = RequestsFor(before, unrelated.Id);
        Assert.Contains(unrelatedRequests, request => !request.IsCompleted && request.IsDismissible == true);
        Assert.Contains(unrelatedRequests, request => request.IsCompleted && request.Answer == "Original answer");

        ApplyTransition(kernel, subject.Id, transition, recoveringFromReceipt);
        ApplyTransition(kernel, control.Id, transition, recoveringFromReceipt);

        var after = kernel.ExportSnapshot();
        var afterRequests = RequestsFor(after, subject.Id);
        var appended = subject.Timeline.Skip(subjectTimelineLength).ToArray();
        var resolutions = appended.Where(entry => entry.Kind == ProgressKind.HumanInputReceived).ToArray();
        Assert.Equal(dismissible.Length, resolutions.Length);
        foreach (var request in dismissible)
        {
            var resolved = Assert.Single(afterRequests, entry => entry.Id == request.Id.Value);
            Assert.True(resolved.IsCompleted);
            Assert.True(resolved.WasDismissed);
            Assert.Null(resolved.Answer);
            Assert.Empty(resolved.AnswerHistory ?? []);
            var entry = Assert.Single(resolutions, entry => entry.TaskId == request.TaskId);
            Assert.Contains(request.Id.Value[..8], entry.Message, StringComparison.Ordinal);
            Assert.Contains(outcome.ToString(), entry.Message, StringComparison.Ordinal);
        }

        Assert.Equal(JsonSerializer.Serialize(protectedWait),
            JsonSerializer.Serialize(Assert.Single(afterRequests, request => request.Id == protectedWait.Id)));
        Assert.Equal(JsonSerializer.Serialize(answeredWait),
            JsonSerializer.Serialize(Assert.Single(afterRequests, request => request.Id == answeredWait.Id)));

        Assert.Equal(outcome, subject.Status);
        Assert.Equal(control.Status, subject.Status);
        Assert.Equal(control.Tasks.Select(task => (task.Description, task.Status)),
            subject.Tasks.Select(task => (task.Description, task.Status)));
        Assert.Equal(control.RefinedSpec!.AcceptanceCriteria.ToArray(), subject.RefinedSpec!.AcceptanceCriteria.ToArray());
        Assert.Equal(2, subject.AuthoritativeBrief.Version);
        Assert.Equal(control.AuthoritativeBrief.Version, subject.AuthoritativeBrief.Version);
        Assert.Equal(control.Timeline.Skip(controlTimelineLength).Select(entry => (entry.Kind, entry.Message)),
            appended.Where(entry => entry.Kind != ProgressKind.HumanInputReceived).Select(entry => (entry.Kind, entry.Message)));
        if (!recoveringFromReceipt)
        {
            Assert.Equal((control.Timeline[^1].Kind, control.Timeline[^1].Message),
                (subject.Timeline[^1].Kind, subject.Timeline[^1].Message));
        }

        Assert.Equal(JsonSerializer.Serialize(unrelatedRequests), JsonSerializer.Serialize(RequestsFor(after, unrelated.Id)));
        Assert.Equal(JsonSerializer.Serialize(before.Goals.Single(goal => goal.Id == unrelated.Id.Value).Timeline),
            JsonSerializer.Serialize(after.Goals.Single(goal => goal.Id == unrelated.Id.Value).Timeline));
    }

    private static Goal CreateGoalWithProtectedRequests(AgentOrchestratorKernel kernel)
    {
        var goal = kernel.CreateGoal("Prepare a terminal transition",
        [
            new TaskSpec(TaskId.New(), "Update implementation", AgentRole.Developer),
            new TaskSpec(TaskId.New(), "Review implementation", AgentRole.Reviewer)
        ]);
        kernel.ReviseGoalBrief(goal.Id, "Prepare a terminal transition with existing decisions.", "Clarify scope.");
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec("Resolve eligible waits.", ["Preserve operator decisions."],
            VerificationClass.TestVerifiable, [], []));
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var answered = kernel.RequestHumanInput(goal.Id, null, "Which option was chosen?", isDismissible: true);
        kernel.SubmitHumanInput(answered.Id, "Original answer");
        kernel.RequestHumanInput(goal.Id, goal.Tasks[0].Id, "Which decision must remain open?", isDismissible: false);
        return goal;
    }

    private static HumanInputRequest[] RequestDismissibleWaits(AgentOrchestratorKernel kernel, Goal goal) =>
    [
        kernel.RequestHumanInput(goal.Id, goal.Tasks[0].Id, "Which task detail can be dropped?"),
        kernel.RequestHumanInput(goal.Id, null, "Which goal detail can be dropped?",
            HumanWaitKind.ProspectiveAcceptanceEvidence, isDismissible: true)
    ];

    private static HumanInputRequestSnapshot[] RequestsFor(OrchestratorSnapshot snapshot, GoalId goalId) =>
        snapshot.HumanInputRequests.Where(request => request.GoalId == goalId.Value)
            .OrderBy(request => request.Id, StringComparer.Ordinal).ToArray();

    private static void ApplyTransition(
        AgentOrchestratorKernel kernel, GoalId goalId, string transition, bool recoveringFromReceipt)
    {
        const string reason = "Record terminal outcome.";
        _ = transition switch
        {
            nameof(AgentOrchestratorKernel.CancelGoal) => kernel.CancelGoal(goalId, reason),
            nameof(AgentOrchestratorKernel.SupersedeGoal) => kernel.SupersedeGoal(goalId, reason),
            nameof(AgentOrchestratorKernel.CompleteGoal) => kernel.CompleteGoal(goalId, reason),
            nameof(AgentOrchestratorKernel.CompleteGoalFromMergeEvidence) =>
                kernel.CompleteGoalFromMergeEvidence(goalId, "integrate-sha", reason, recoveringFromReceipt),
            _ => throw new ArgumentOutOfRangeException(nameof(transition), transition, null)
        };
    }
}
