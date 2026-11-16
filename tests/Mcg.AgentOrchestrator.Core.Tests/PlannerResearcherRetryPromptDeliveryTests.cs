using Mcg.AgentOrchestrator.Core;

// Parallel-safe: every test owns its in-memory kernel and fake clock.
public sealed class PlannerResearcherRetryPromptDeliveryTests
{
    [Fact]
    public void PlannerRetryBriefCarriesExactMultiLineCorrectionAndDiffersFromPreRetryBrief()
    {
        var (clock, kernel, goal, task) = CreateGoal(AgentRole.Planner);
        var correction = Correction("PLANNER-DELIVERY-ROUND-ONE");
        var before = kernel.BuildTaskBrief(goal.Id, task.Id).Content;
        Assert.DoesNotContain(correction, before, StringComparison.Ordinal);

        FailAndRetry(clock, kernel, goal, task, correction);

        var after = kernel.BuildTaskBrief(goal.Id, task.Id).Content;
        Assert.Contains(correction, after, StringComparison.Ordinal);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void ResearcherRetryBriefCarriesExactMultiLineCorrectionAndDiffersFromPreRetryBrief()
    {
        var (clock, kernel, goal, task) = CreateGoal(AgentRole.Researcher);
        var correction = Correction("RESEARCHER-DELIVERY-ROUND-ONE");
        var before = kernel.BuildTaskBrief(goal.Id, task.Id).Content;
        Assert.DoesNotContain(correction, before, StringComparison.Ordinal);

        FailAndRetry(clock, kernel, goal, task, correction);

        var after = kernel.BuildTaskBrief(goal.Id, task.Id).Content;
        Assert.Contains(correction, after, StringComparison.Ordinal);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void SecondPlannerRetryKeepsNewestReasonInFullAfterOversizedOlderFeedback()
    {
        var (clock, kernel, goal, task) = CreateGoal(AgentRole.Planner);
        var olderCorrection = "PLANNER-OVERSIZED-OLDER-REASON\n" + new string('x', 3600);
        var newestCorrection = Correction("PLANNER-DELIVERY-ROUND-TWO");
        Assert.True(olderCorrection.Length > 3500);

        FailAndRetry(clock, kernel, goal, task, olderCorrection);
        FailAndRetry(clock, kernel, goal, task, newestCorrection);

        var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;
        Assert.Contains(newestCorrection, brief, StringComparison.Ordinal);
        var headingCount = brief.Split(
            RetryCorrectionObligationBriefSection.CurrentHeading, StringSplitOptions.None).Length - 1;
        Assert.InRange(headingCount, 0, 1);
    }

    private static (FakeClock Clock, AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task)
        CreateGoal(AgentRole role)
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "Prepare retry delivery evidence.", role);
        var developer = new TaskSpec(TaskId.New(), "Implement the resulting plan.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Deliver operator correction feedback in the rebuilt brief", [task, developer]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        return (clock, kernel, goal, task);
    }

    private static void FailAndRetry(
        FakeClock clock, AgentOrchestratorKernel kernel, Goal goal, TaskSpec task, string correction)
    {
        clock.Advance();
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Prior attempt needs correction.");
        clock.Advance();
        kernel.RetryTaskWithAuthoritativeFeedback(
            goal.Id, task.Id, correction, RetryCause.ContractClarification);
    }

    private static string Correction(string marker) => string.Join("\n",
        $"{marker}: Inspect the kernel retry operation and identify the exact brief-building seam that delivers operator feedback to this task.",
        $"{marker}: Preserve every interior line break and the complete supplied correction when rebuilding the brief after the failed attempt.",
        $"{marker}: Record the owning evidence source and the next verification step so this final sentinel survives beyond the timeline preview.");
}
