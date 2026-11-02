using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: each test owns its kernel, fixed observation times and unique temporary directory.
public sealed class PullRequestReviewHoldOwnerTests
{
    private const string DeliveredReason = "delivered for review: https://github.com/o/r/pull/14";

    [Theory]
    [InlineData(DeliveredReason)]
    [InlineData(DeliveredReason + " as touchproof[bot]")]
    public void LandingResult_DeliveredForReview_AssignsReviewOwner(string reason)
    {
        var (_, _, _, held) = LandHeld(reason);

        Assert.Equal(ConductorHoldOwner.PullRequestReview, held.Owner);
        var decision = Assert.IsType<PolicyDecisionRecord>(held.Decision);
        Assert.Equal("landing-completion", decision.Stage);
        Assert.Equal("Hold", decision.Action);
        Assert.Equal(12, decision.Rung);
        Assert.Equal("landing-delivered-for-review", decision.DiscriminatingEvidence);
        Assert.Equal(reason, decision.Reason);
    }

    [Theory]
    [InlineData(DeliveredReason)]
    [InlineData(DeliveredReason + " as touchproof[bot]")]
    public void ReviewHold_PastThreshold_PreservesHoldWithoutEscalation(string reason)
    {
        var (kernel, goal, driver, held) = LandHeld(reason);
        using var fixture = new EscalationFixture(kernel, goal, driver);

        foreach (var minutes in new[] { 0, 11, 22 }) fixture.Observe(held, minutes);

        var hold = Assert.IsType<GoalHoldState>(goal.CurrentHold);
        Assert.Equal("Verified", hold.State);
        Assert.Equal(reason, hold.Blocker);
        Assert.Null(hold.StalledAt);
        fixture.AssertNoEscalation();
    }

    [Fact]
    public void ReviewHold_AfterOwnerlessObservation_DoesNotPromoteStall()
    {
        var (kernel, goal, driver, held) = LandHeld(DeliveredReason);
        using var fixture = new EscalationFixture(kernel, goal, driver);
        fixture.Observe(held with { Owner = ConductorHoldOwner.None }, 0);
        var earlier = Assert.IsType<GoalHoldState>(goal.CurrentHold);

        fixture.Observe(held, 11);
        fixture.Observe(held, 22);

        var hold = Assert.IsType<GoalHoldState>(goal.CurrentHold);
        Assert.Equal(earlier.Identity, hold.Identity);
        Assert.Equal(earlier.StartedAt, hold.StartedAt);
        Assert.Equal(DeliveredReason, hold.Blocker);
        Assert.Null(hold.StalledAt);
        fixture.AssertNoEscalation();
    }

    [Theory]
    [InlineData("landing mutation blocked: circuit opened inside landing")]
    [InlineData("landing mutation blocked: delivered-for-review record write failed")]
    public void LandingResult_MutationHold_KeepsOwnerlessOutcome(string reason)
    {
        var (_, _, _, held) = LandHeld(reason);

        Assert.Equal(ConductorHoldOwner.None, held.Owner);
        var decision = Assert.IsType<PolicyDecisionRecord>(held.Decision);
        Assert.Equal("Hold", decision.Action);
        Assert.Equal(9, decision.Rung);
        Assert.Equal("landing-mutation-hold-escalation", decision.DiscriminatingEvidence);
        Assert.Equal(reason, decision.Reason);
    }

    [Fact]
    public void OwnerlessHold_PastThreshold_StillEscalatesOnce()
    {
        var (kernel, goal) = SimpleGoal();
        using var fixture = new EscalationFixture(kernel, goal, MakeDriver());
        const string reason = "waiting for an owner";
        var held = new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, reason);
        Assert.Equal(ConductorHoldOwner.None, held.Owner);
        fixture.Observe(held, 0);
        fixture.AssertNoEscalation();

        fixture.Observe(held, 11);
        fixture.Observe(held, 22);

        Assert.NotNull(goal.CurrentHold?.StalledAt);
        var escalation = Assert.Single(fixture.Timeline());
        Assert.Equal("ownerless-hold-stall", escalation.GetProperty("source").GetString());
        Assert.Equal(goal.Id.Value, escalation.GetProperty("goalId").GetString());
        Assert.Equal("Verified", escalation.GetProperty("state").GetString());
        var detail = escalation.GetProperty("reason").GetString()!;
        Assert.StartsWith("ownerless-hold-stalled ", detail, StringComparison.Ordinal);
        Assert.Contains(reason, detail, StringComparison.Ordinal);
        Assert.Equal(detail, Assert.Single(fixture.Conduct()).GetProperty("detail").GetString());
        Assert.Single(fixture.Lines, line => line.StartsWith("GOAL_STALLED ", StringComparison.Ordinal));
    }

    private static (AgentOrchestratorKernel, Goal, ConductorDriver, ConductorAdvanceOutcome.Held)
        LandHeld(string reason)
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var effects = new List<string>();
        var driver = MakeDriver(runAcceptanceSummary: _ =>
        {
            effects.Add("acceptance");
            return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
        }, land: g =>
        {
            effects.Add("land");
            return new LandingResult(g.Id.Value, g.Id.Value[..8],
                new LandingDecision.Escalate(reason), "integration", false, "Held");
        }, writeEscalation: (_, _, _) => throw new InvalidOperationException("Landing hold must not escalate."));

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(
            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);
        Assert.Equal(new[] { "acceptance", "land" }, effects);
        Assert.Equal(GoalStatus.Verified, goal.Status);
        Assert.Equal(GoalLifecycleState.Verified, held.State);
        Assert.Equal(reason, held.Reason);
        Assert.Null(held.StableIdentity);
        Assert.Null(held.TypedReason);
        return (kernel, goal, driver, held);
    }

    private sealed class EscalationFixture : IDisposable
    {
        private static readonly DateTimeOffset Start = new(2026, 11, 2, 6, 0, 0, TimeSpan.Zero);
        private readonly string _root = Path.Combine(Path.GetTempPath(), "pr-review-hold-" + Guid.NewGuid().ToString("N"));
        private readonly AgentOrchestratorKernel _kernel;
        private readonly Goal _goal;
        private readonly ConductorDriver _driver;
        private readonly GoalLifecycleEventWriter _timeline;
        private readonly ConductEventLogWriter _conduct;
        private readonly HashSet<GoalId> _changed = [];
        internal List<string> Lines { get; } = [];

        internal EscalationFixture(AgentOrchestratorKernel kernel, Goal goal, ConductorDriver driver)
        {
            (_kernel, _goal, _driver) = (kernel, goal, driver);
            Directory.CreateDirectory(_root);
            _timeline = new GoalLifecycleEventWriter(Path.Combine(_root, "timeline"), new FixedClock(),
                sessionRetentionOptions: new DispatchProviderSessionRetentionOptions(TimeSpan.Zero));
            _driver.HoldEscalationEventWriter = _timeline;
            _conduct = new ConductEventLogWriter(Path.Combine(_root, "conduct.log"), utcNow: () => Start);
        }

        internal void Observe(ConductorAdvanceOutcome outcome, int minutes) =>
            ConductorBatchLoop.TrackGoalOutcome(_kernel, _driver, _goal, outcome, Start.AddMinutes(minutes),
                TimeSpan.FromMinutes(10), _changed, Lines, _conduct);

        internal void AssertNoEscalation()
        {
            Assert.Empty(Timeline());
            Assert.Empty(Conduct());
            Assert.DoesNotContain(Lines, line => line.StartsWith("GOAL_STALLED ", StringComparison.Ordinal));
        }

        internal List<JsonElement> Timeline() => ReadEvents(_timeline.EventFilePath(_goal.Id), "eventType", "GoalEscalated");
        internal List<JsonElement> Conduct() => ReadEvents(_conduct.CurrentPath, "eventKind", "goal-escalation");

        private static List<JsonElement> ReadEvents(string path, string key, string value)
        {
            if (!File.Exists(path)) return [];
            var events = new List<JsonElement>();
            foreach (var line in File.ReadAllLines(path))
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.GetProperty(key).GetString() == value)
                    events.Add(document.RootElement.Clone());
            }
            return events;
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);

        private sealed class FixedClock : IClock
        {
            public DateTimeOffset UtcNow => Start;
        }
    }
}
