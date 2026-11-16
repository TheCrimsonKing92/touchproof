using Mcg.AgentOrchestrator.Core;

// Parallel-safe: every case owns its kernel, requests and injected clock.
public sealed class HumanWaitAutoDefaultGateTests
{
    [Fact]
    public void PolicyDefault_IsFalseForEveryHumanWaitKind()
    {
        foreach (var kind in Enum.GetValues<HumanWaitKind>())
        {
            Assert.False(HumanWaitPolicyDefaults.IsAutoDefaultable(kind));
        }
    }

    [Fact]
    public void SpecClarificationWithSuggestedDefault_WithoutExplicitFlag_IsNotAutoDefaultable()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal("Require explicit opt-in");

        var request = kernel.RequestHumanInput(goal.Id, null, "Use proposed scope?",
            suggestedDefaultAnswer: "Use proposed scope.");

        Assert.Equal(HumanWaitKind.SpecClarification, request.Kind);
        Assert.False(request.IsAutoDefaultable);
        Assert.Equal("Use proposed scope.", request.SuggestedDefaultAnswer);
    }

    [Fact]
    public void CriterionWeakeningClarification_WithoutExplicitFlag_IsNeverAutoAnswered()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var weakeningGoal = kernel.CreateGoal("Preserve acceptance criteria");
        var controlGoal = kernel.CreateGoal("Default reversible clarification");
        var weakening = kernel.RequestHumanInput(weakeningGoal.Id, null,
            "May we drop the acceptance criterion requiring regression coverage?",
            suggestedDefaultAnswer: "Drop the regression coverage criterion.");
        var control = kernel.RequestHumanInput(controlGoal.Id, null, "Use proposed scope?",
            isAutoDefaultable: true, suggestedDefaultAnswer: "Use proposed scope.");
        clock.Advance(TimeSpan.FromHours(25));

        var resolved = kernel.SweepStaleHumanWaits(TimeSpan.FromHours(24));

        var pending = kernel.GetHumanInputRequest(weakening.Id);
        Assert.False(pending.IsCompleted);
        Assert.Null(pending.Answer);
        Assert.Null(pending.AuthoritativeAnswer);
        Assert.False(pending.WasDismissed);
        Assert.DoesNotContain(resolved, result => result.RequestId == weakening.Id);
        var defaulted = Assert.Single(resolved);
        Assert.Equal(control.Id, defaulted.RequestId);
        Assert.Equal(HumanWaitPolicyResolution.Defaulted, defaulted.Resolution);
        Assert.True(kernel.GetHumanInputRequest(control.Id).AuthoritativeAnswer!.IsAutoDefaulted);
    }

    [Fact]
    public void FlaggedPlannerBriefAmendment_StaysOpen_WhileFlaggedSpecClarificationDefaults()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var planner = new TaskSpec(TaskId.New(), "Plan acceptance criteria", AgentRole.Planner);
        var amendmentGoal = kernel.CreateGoal("Preserve amendment authority", [planner]);
        var controlGoal = kernel.CreateGoal("Default reversible clarification");
        var amendment = kernel.RequestHumanInputDeduplicated(amendmentGoal.Id, planner.Id,
            "Relax the first acceptance criterion?", HumanWaitKind.PlannerBriefAmendment,
            isAutoDefaultable: true, suggestedDefaultAnswer: "Relax criterion 1.",
            amendmentCriterionIndex: 1).Request;
        var control = kernel.RequestHumanInput(controlGoal.Id, null, "Use proposed scope?",
            isAutoDefaultable: true, suggestedDefaultAnswer: "Use proposed scope.");
        clock.Advance(TimeSpan.FromHours(25));

        var resolved = kernel.SweepStaleHumanWaits(TimeSpan.FromHours(24));

        var pending = kernel.GetHumanInputRequest(amendment.Id);
        Assert.True(pending.IsAutoDefaultable);
        Assert.False(pending.IsCompleted);
        Assert.Null(pending.Answer);
        Assert.Null(pending.AuthoritativeAnswer);
        Assert.False(pending.WasDismissed);
        var defaulted = Assert.Single(resolved);
        Assert.Equal(control.Id, defaulted.RequestId);
        Assert.Equal(HumanWaitPolicyResolution.Defaulted, defaulted.Resolution);
        var completed = kernel.GetHumanInputRequest(control.Id);
        Assert.True(completed.IsCompleted);
        var answer = Assert.IsType<HumanInputAnswerRecord>(completed.AuthoritativeAnswer);
        Assert.True(answer.IsAutoDefaulted);
        Assert.Equal(HumanInputAnswerOrigin.Operator, answer.Origin);
        Assert.Equal("Use proposed scope.", answer.Text);
    }
}
