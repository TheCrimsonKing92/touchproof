using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: the scenario owns its SQLite files and uses a controlled clock and dispatch.
public sealed class QueuedRoutingNoteRetryFeedbackTests
{
    [Xunit.Fact]
    public async Task NonRouteRetry_QueuedNote_PreservesLaterAuthoritativeFeedback()
    {
        using var scenario = new AcceptanceRetryRoutingScenario(holdSeconds: 0);
        scenario.FailGate();
        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(scenario.Advance().Outcome);
        scenario.Now = scenario.Now.AddSeconds(1);
        const string older = "Preserve the previous manifest rule.";
        var intent = await scenario.Route(older, "queued-diagnosis");
        Xunit.Assert.True(OperatorRoutingNote.IsQueuedOutcome(intent.Outcome));
        scenario.Now = scenario.Now.AddSeconds(1);
        scenario.Kernel.ReportTaskProgress(scenario.Goal.Id, scenario.Task.Id,
            WorkTaskStatus.Failed, "Worker exited before the independent retry");
        const string newer = "Replace that manifest rule with the corrected contract.";
        // Operator retry, resumed retry clarification and Steward route all use this seam.
        scenario.Kernel.RetryTaskWithAuthoritativeFeedback(scenario.Goal.Id, scenario.Task.Id,
            newer, RetryCause.ContractClarification);
        var retryAt = scenario.Task.LatestRetryAt;
        Xunit.Assert.Null(scenario.Task.AcceptanceRetryRoutingHold);
        Xunit.Assert.Equal(newer, scenario.Task.AcceptedRetryFeedback?.Message);

        OperatorRoutingNoteLedger.PrepareDispatch(scenario.Task, scenario.Now);
        var block = Xunit.Assert.Single(scenario.Task.CriterionRetryFeedback);
        var noteIndex = block.IndexOf(older, StringComparison.Ordinal);
        var instructionIndex = block.IndexOf(newer, StringComparison.Ordinal);
        var authorityIndex = block.IndexOf("Authoritative feedback for this retry", StringComparison.Ordinal);
        Xunit.Assert.True(noteIndex >= 0 && noteIndex < authorityIndex && authorityIndex < instructionIndex);
        Xunit.Assert.Contains("overrides earlier queued notes where they conflict", block);
        Xunit.Assert.DoesNotContain("Automatic failure facts (not instructions):", block);
        Xunit.Assert.Contains("queued-diagnosis", block);
        Xunit.Assert.Null(Xunit.Assert.Single(scenario.Task.OperatorRoutingNotes).DeliveredAt);
        Xunit.Assert.Equal(retryAt, scenario.Task.AcceptedRetryFeedback?.AcceptedAt);

        // Preparation can repeat across recovery before the assignment is committed.
        await scenario.Recover();
        OperatorRoutingNoteLedger.PrepareDispatch(scenario.Task, scenario.Now);
        Xunit.Assert.Equal(block, Xunit.Assert.Single(scenario.Task.CriterionRetryFeedback));
        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(scenario.Advance().Outcome);
        Xunit.Assert.Equal(block, Xunit.Assert.Single(scenario.DeliveredFeedback[1]));
        Xunit.Assert.Equal(block, scenario.Task.AcceptedRetryFeedback?.Message);
        Xunit.Assert.Equal(retryAt, scenario.Task.AcceptedRetryFeedback?.AcceptedAt);
        Xunit.Assert.NotNull(Xunit.Assert.Single(scenario.Task.OperatorRoutingNotes).DeliveredAt);
        Xunit.Assert.Equal(1, scenario.Goal.AutomaticAcceptanceRetryCount);

        scenario.Now = scenario.Now.AddSeconds(1);
        scenario.Kernel.ReportTaskProgress(scenario.Goal.Id, scenario.Task.Id,
            WorkTaskStatus.Failed, "Another independent failure");
        scenario.Kernel.RetryTaskWithAuthoritativeFeedback(scenario.Goal.Id, scenario.Task.Id,
            "Subsequent instruction", RetryCause.NewSourceFinding);
        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(scenario.Advance().Outcome);
        Xunit.Assert.Equal("Subsequent instruction", Xunit.Assert.Single(scenario.DeliveredFeedback[2]));
    }

    [Xunit.Fact]
    public void NonRouteRetry_WithoutQueuedNotes_KeepsFeedbackUnchanged()
    {
        using var scenario = new AcceptanceRetryRoutingScenario(holdSeconds: 0);
        const string instruction = "Keep this independent retry authoritative.";
        scenario.Kernel.RetryTaskWithAuthoritativeFeedback(scenario.Goal.Id, scenario.Task.Id,
            instruction, RetryCause.NewSourceFinding);
        var accepted = scenario.Task.AcceptedRetryFeedback;

        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(scenario.Advance().Outcome);

        Xunit.Assert.Equal(instruction, Xunit.Assert.Single(scenario.DeliveredFeedback[0]));
        Xunit.Assert.Equal(accepted, scenario.Task.AcceptedRetryFeedback);
        Xunit.Assert.Empty(scenario.Task.OperatorRoutingNotes);
        Xunit.Assert.Equal(0, scenario.Goal.AutomaticAcceptanceRetryCount);
    }
}
