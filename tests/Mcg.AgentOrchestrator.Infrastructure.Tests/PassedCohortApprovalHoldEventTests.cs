using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static Mcg.AgentOrchestrator.Infrastructure.Tests.LandingExecutorTestsDenylistOwnerHold;

// Each fixture owns its git repository and stores; the collection isolates landing git hooks.
[Collection(TestCollections.LandingGitRunner)]
public sealed class PassedCohortApprovalHoldEventTests : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void HeldTick_SecondMemberNeedsApproval_StreamsOnlyWaitingMemberHold()
    {
        using var scenario = new PassedCohortReceiptApprovalHoldTests.Scenario(protectedMemberIndex: 1);
        using var capacity = PassedCohortReceiptApprovalHoldTests.Scenario.PauseNewGates();
        var logPath = scenario.Workspace.ConductEventsLogPath;
        var progress = scenario.Tick(events: new ConductEventLogWriter(logPath));

        AssertHeldLines(scenario, progress);
        scenario.AssertPartnerReleased(progress, heldIndex: 1);
        var events = ReadHeldEvents(logPath);
        Assert.Single(events);
        AssertHeldLines(scenario, events.Select(record => record.Detail).ToArray());
        Assert.Equal(scenario.Goals[1].Id.Value[..8], events[0].GoalId);
        Assert.Equal(1, scenario.Verifier.RunCount);
    }

    [Fact]
    [Trait("Category", "CrossTick")]
    public void TwoTicks_UnchangedHold_StreamsEachTickAndPersistsOneDecision()
    {
        using var scenario = new PassedCohortReceiptApprovalHoldTests.Scenario(protectedMemberIndex: 1);
        using var capacity = PassedCohortReceiptApprovalHoldTests.Scenario.PauseNewGates();
        var persisted = new Dictionary<GoalId, int>();
        var ticks = new List<BatchTickSummary>();
        Assert.All(scenario.Goals, goal => Assert.Empty(HeldDecisions(goal)));

        // One Run preserves the solo-hold disposition cache across both ticks.
        new ConductorBatchLoop(conductEventLogWriter: new ConductEventLogWriter(
            scenario.Workspace.ConductEventsLogPath)).Run(
            scenario.Kernel, scenario.Driver, ConductorAutonomyPolicy.Permissive,
            Path.Combine(scenario.Repo, "stop-does-not-exist"), maxIterations: 2,
            watchInterval: TimeSpan.FromSeconds(1), sleepFunc: _ => false,
            persistGoalTick: (kernel, ids) =>
            {
                foreach (var id in ids)
                    persisted[id] = HeldDecisions(kernel.GetGoal(id)).Count;
            },
            onTick: tick =>
            {
                ticks.Add(tick);
                AssertHeldLines(scenario, tick.ProgressLines);
                scenario.AssertPartnerReleased(tick.ProgressLines, heldIndex: 1);
                var goal = scenario.Goals[1];
                var decision = Assert.Single(HeldDecisions(goal));
                Assert.Contains(scenario.Receipt.Identity.Value, decision.Message, StringComparison.Ordinal);
                // This check runs after each tick, including tick one, proving journal durability.
                Assert.True(persisted.ContainsKey(goal.Id), "The held member journal was not persisted.");
                Assert.Equal(1, persisted[goal.Id]);
                Assert.Empty(HeldDecisions(scenario.Goals[0]));
            });

        Assert.Equal(2, ticks.Count);
        var events = ReadHeldEvents(scenario.Workspace.ConductEventsLogPath);
        Assert.Equal(2, events.Length);
        Assert.All(events, record => Assert.Equal(scenario.Goals[1].Id.Value[..8], record.GoalId));
        Assert.Equal(1, scenario.Verifier.RunCount);
    }

    private static List<ProgressEvent> HeldDecisions(Goal goal) => goal.Timeline
        .Where(entry => entry.Kind == ProgressKind.GoalPolicyDecision &&
            entry.Message.Contains("held at Verified", StringComparison.Ordinal) &&
            entry.Message.Contains("cohort=", StringComparison.Ordinal)).ToList();

    private static ConductEventRecord[] ReadHeldEvents(string path) => File.ReadAllLines(path)
        .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
        .Where(record => record.Detail.StartsWith("GOAL ", StringComparison.Ordinal) &&
            record.Detail.Contains("result=held", StringComparison.Ordinal) &&
            record.Detail.Contains("landing-denylist_owner_approval_required", StringComparison.Ordinal)).ToArray();

    private static void AssertHeldLines(PassedCohortReceiptApprovalHoldTests.Scenario scenario,
        IReadOnlyList<string> progress)
    {
        var held = progress.Where(line => line.StartsWith("GOAL ", StringComparison.Ordinal) &&
            line.Contains("result=held", StringComparison.Ordinal) &&
            line.Contains("landing-denylist_owner_approval_required", StringComparison.Ordinal)).ToArray();
        var line = Assert.Single(held);
        var waiting = scenario.Goals[1].Id.Value[..8];
        Assert.StartsWith($"GOAL goal={waiting} ", line, StringComparison.Ordinal);
        Assert.Contains("result=held state=Verified", line, StringComparison.Ordinal);
        Assert.Contains("landing-denylist_owner_approval_required", line, StringComparison.Ordinal);
        Assert.Contains($"cohort={scenario.Receipt.Identity.Value}", line, StringComparison.Ordinal);
        Assert.Contains($"waiting={waiting}", line, StringComparison.Ordinal);
        Assert.Contains($"candidate={scenario.CandidateRevisions[1]}", line, StringComparison.Ordinal);
        Assert.Contains("rule=landing-policy", line, StringComparison.Ordinal);
        Assert.Contains($"path={Protected}", line, StringComparison.Ordinal);
        Assert.Contains($"approve-policy-change_{waiting}_{scenario.CandidateRevisions[1]}",
            line, StringComparison.Ordinal);
    }
}
