using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using GoalDecision = Mcg.AgentOrchestrator.App.Orchestration.PostAdoptionRegressionEvaluator.GoalDecision;
using HandleStatus = Mcg.AgentOrchestrator.App.Orchestration.PostAdoptionRegressionReverter.HandleStatus;
using IncidentEvent = Mcg.AgentOrchestrator.App.Orchestration.PostAdoptionRegressionReverter.IncidentEvent;
using RevertOutcome = Mcg.AgentOrchestrator.App.Orchestration.ActivationFailedMainRevert.Outcome;

// Parallel-safe: each fixture has its own policy directory and fakes all repository and handoff actions.
public sealed class PostAdoptionRegressionRecoveryTests
{
    private const string Landing = "landing-sha";
    private const string Revert = "revert-sha";
    private const string Branch = "integration";
    private static readonly DateTimeOffset AdoptedAt = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly PolicyDecisionRecord Hold =
        new("solo-landing-admission", "Hold", 1, "no-parallel-acceptance-candidate", "shared blocker", []);
    private static readonly GoalDecision[] Baseline = [new("one", true, null), new("two", true, null)];
    private static readonly GoalDecision[] Regressing = [new("one", false, Hold), new("two", false, Hold)];

    [Fact]
    public void GuardRevertedIncidentPushesRevertOnceThenRequestsOneRelaunch()
    {
        using var fixture = new Fixture();
        var guard = fixture.CreateGuard();

        ObserveBatch(guard, 11);
        Assert.Empty(fixture.Trace);

        guard.ObserveGoalResult(12, AdoptedAt.AddMinutes(12), "closing-goal", null, false, false, 2);

        Assert.Equal((fixture.Root, Revert, Branch), Assert.Single(fixture.Pushes));
        Assert.Equal(new ConductorSelfRelaunchRequest("closing-goal", 11, fixture.Target), Assert.Single(fixture.Requests));
        Assert.Equal(new[]
        {
            "POST_ADOPTION_REGRESSION_REVERTED", $"push:{Revert}", "REVERT_PUSH_PUSHED",
            "POST_ADOPTION_REGRESSION_BACKLOG_FILED", "relaunch", "POST_ADOPTION_REGRESSION_RELAUNCH_REQUESTED"
        }, fixture.Trace);
    }

    [Theory]
    [InlineData("Held")]
    [InlineData("NotApplicable")]
    [InlineData("Suppressed")]
    public void NonRevertedOutcomesNeverPushOrRelaunch(string outcome)
    {
        using var fixture = new Fixture();
        var guard = fixture.CreateGuard(outcome == "Held" ? RevertOutcome.Held : RevertOutcome.NotApplicable);
        if (outcome == "Suppressed")
        {
            Assert.Equal(HandleStatus.NotApplicable, ObserveRegression(guard));
            fixture.Trace.Clear();
            fixture.Events.Clear();
        }

        Assert.Equal(Enum.Parse<HandleStatus>(outcome), ObserveRegression(guard));

        Assert.Empty(fixture.Pushes);
        Assert.Empty(fixture.Requests);
        Assert.Equal(0, fixture.TargetResolutions);
        if (outcome == "Held")
            Assert.StartsWith($"POST_ADOPTION_REGRESSION_REVERT_HELD landing={Landing} reason=\"merge conflict\"",
                Assert.Single(fixture.Events));
        else if (outcome == "NotApplicable")
            Assert.StartsWith("POST_ADOPTION_REGRESSION_REVERT_NOT_APPLICABLE ", Assert.Single(fixture.Events));
        else
            Assert.Empty(fixture.Events);
    }

    [Fact]
    public void DisabledPushPolicySkipsPushAndStillRequestsRelaunch()
    {
        using var fixture = new Fixture(pushEnabled: false);

        Assert.Equal(HandleStatus.Reverted, ObserveRegression(fixture.CreateGuard()));

        Assert.Empty(fixture.Pushes);
        Assert.Single(fixture.Requests);
        var skipped = Assert.Single(fixture.Events, detail => detail.StartsWith("REVERT_PUSH_SKIPPED ", StringComparison.Ordinal));
        Assert.Contains("source=post-adoption-regression", skipped);
        Assert.Contains("reason=adopted-build-push-disabled", skipped);
        Assert.Equal(new[]
        {
            "POST_ADOPTION_REGRESSION_REVERTED", "REVERT_PUSH_SKIPPED", "POST_ADOPTION_REGRESSION_BACKLOG_FILED",
            "relaunch", "POST_ADOPTION_REGRESSION_RELAUNCH_REQUESTED"
        }, fixture.Trace);
    }

    [Theory]
    [InlineData(false, "push-failed")]
    [InlineData(true, "IOException")]
    public void FailedPushAppendsHeldEventKeepsRevertAndStillRequestsRelaunch(bool throws, string reason)
    {
        using var fixture = new Fixture();
        fixture.Push = () => throws ? throw new IOException("injected push failure") : false;

        Assert.Equal(HandleStatus.Reverted, ObserveRegression(fixture.CreateGuard()));

        Assert.Equal(Revert, fixture.LocalTip);
        Assert.Equal((fixture.Root, Revert, Branch), Assert.Single(fixture.Pushes));
        Assert.Single(fixture.Requests);
        var held = Assert.Single(fixture.Events, detail => detail.StartsWith("REVERT_PUSH_HELD ", StringComparison.Ordinal));
        Assert.Contains($"commit={Revert} branch={Branch} reason={reason} ", held);
        Assert.Equal(new[]
        {
            "POST_ADOPTION_REGRESSION_REVERTED", $"push:{Revert}", "REVERT_PUSH_HELD",
            "POST_ADOPTION_REGRESSION_BACKLOG_FILED", "relaunch", "POST_ADOPTION_REGRESSION_RELAUNCH_REQUESTED"
        }, fixture.Trace);
    }

    [Fact]
    public void RepeatedRegressionObservationsInOneIncidentRecoverOnce()
    {
        using var fixture = new Fixture();
        var guard = fixture.CreateGuard();

        for (var tick = 1; tick <= 5; tick++)
            ObserveBatch(guard, tick);
        guard.Adopt("second-landing", Baseline, AdoptedAt);
        Assert.Equal(HandleStatus.Suppressed, ObserveRegression(guard));

        Assert.Equal((fixture.Root, Branch, Landing), Assert.Single(fixture.Reverts));
        Assert.Single(fixture.Pushes);
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public void RunningGateObservationBlocksPushAndRelaunch()
    {
        using var fixture = new Fixture();
        var recovery = fixture.CreateRecovery();
        var incident = RevertedIncident();

        recovery.Recover(HandleStatus.Reverted, incident, 2, "one", gateRunning: true);
        recovery.Recover(HandleStatus.Reverted, incident, 3, "one", gateRunning: false);

        Assert.Empty(fixture.Pushes);
        Assert.Empty(fixture.Requests);
        Assert.Equal(0, fixture.TargetResolutions);
        Assert.StartsWith("POST_ADOPTION_REGRESSION_RECOVERY_HELD reason=gate-running ", Assert.Single(fixture.Events));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingRevertCommitPreventsRecovery(string? revertCommit)
    {
        using var fixture = new Fixture();

        fixture.CreateRecovery().Recover(HandleStatus.Reverted, RevertedIncident() with { RevertCommit = revertCommit },
            2, "one", gateRunning: false);

        Assert.Empty(fixture.Trace);
        Assert.Empty(fixture.Requests);
        Assert.Equal(0, fixture.TargetResolutions);
    }

    [Fact]
    public void MissingRestoredBuildAlertsOperatorAndConsumesTheAttempt()
    {
        using var fixture = new Fixture();
        var target = fixture.Target;
        fixture.Target = null;
        var recovery = fixture.CreateRecovery();
        var incident = RevertedIncident();

        recovery.Recover(HandleStatus.Reverted, incident, 2, "one", gateRunning: false);
        fixture.Target = target;
        recovery.Recover(HandleStatus.Reverted, incident, 3, "one", gateRunning: false);

        Assert.Single(fixture.Pushes);
        Assert.Empty(fixture.Requests);
        Assert.Equal(1, fixture.TargetResolutions);
        var hold = Assert.Single(fixture.Events, detail => detail.StartsWith("POST_ADOPTION_REGRESSION_RELAUNCH_HELD ", StringComparison.Ordinal));
        Assert.Contains("classification=OPERATOR_HOLD reason=no-restored-build-target", hold);
        Assert.Contains($"goal=one tick=2 revertCommit={Revert}", hold);
        var attention = Assert.Single(fixture.OperatorHolds);
        Assert.Equal(("one", Revert), (attention.GoalId, attention.Commit));
        Assert.Contains("relaunch held: no restored-build target", attention.Detail);
    }

    [Fact]
    public void RefusedRelaunchIsRecordedAndNeverRetried()
    {
        using var fixture = new Fixture { AcceptRelaunch = false };
        var recovery = fixture.CreateRecovery();
        var incident = RevertedIncident();

        recovery.Recover(HandleStatus.Reverted, incident, 2, "one", gateRunning: false);
        recovery.Recover(HandleStatus.Reverted, incident, 3, "one", gateRunning: false);

        Assert.Single(fixture.Pushes);
        Assert.Single(fixture.Requests);
        Assert.Contains(fixture.Events, detail => detail.StartsWith(
            "POST_ADOPTION_REGRESSION_RELAUNCH_REFUSED reason=request-not-accepted ", StringComparison.Ordinal));
    }

    private static IncidentEvent RevertedIncident() =>
        new(Landing, "shared-blocker", RevertOutcome.Reverted, Revert, null, [], false, false, 2);

    private static void ObserveBatch(PostAdoptionRegressionGuard guard, int tick)
    {
        guard.ObserveGoalResult(tick, AdoptedAt.AddMinutes(tick), "one", Hold, false, false, 2);
        guard.ObserveGoalResult(tick, AdoptedAt.AddMinutes(tick), "two", Hold, false, false, 2);
    }

    private static HandleStatus? ObserveRegression(PostAdoptionRegressionGuard guard) =>
        guard.ObserveTick(Regressing, false, false, 2, AdoptedAt.AddMinutes(1), tick: 1, goalId: "one");

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("regression-recovery-");
        private readonly string _orchestratorDirectory;
        internal string Root => _directory.FullName;
        internal string? LocalTip { get; private set; }
        internal HomeBuildAdoptionRecord? Target { get; set; }
        internal Func<bool> Push { get; set; } = () => true;
        internal bool AcceptRelaunch { get; init; } = true;
        internal int TargetResolutions { get; private set; }
        internal List<string> Trace { get; } = [];
        internal List<string> Events { get; } = [];
        internal List<(string? Root, string? Branch, string? Landing)> Reverts { get; } = [];
        internal List<(string Root, string Commit, string Branch)> Pushes { get; } = [];
        internal List<ConductorSelfRelaunchRequest> Requests { get; } = [];
        internal List<(string? GoalId, string Commit, string Detail)> OperatorHolds { get; } = [];

        internal Fixture(bool pushEnabled = true)
        {
            _orchestratorDirectory = _directory.CreateSubdirectory(".orchestrator").FullName;
            File.WriteAllText(Path.Combine(_orchestratorDirectory, "conductor-policy.json"),
                (ConductorAutonomyPolicy.Conservative with { AdoptedBuildPushEnabled = pushEnabled }).ToJson());
            var runDirectory = Path.Combine(Root, "restored-run");
            Target = new(1, Root, 2, "restored-sha", runDirectory,
                Path.Combine(runDirectory, "Mcg.AgentOrchestrator.App.dll"), "adopted");
        }

        internal PostAdoptionRegressionGuard CreateGuard(RevertOutcome outcome = RevertOutcome.Reverted)
        {
            var reverter = new PostAdoptionRegressionReverter(Root, Branch, Append, (root, branch, landing) =>
            {
                Reverts.Add((root, branch, landing));
                if (outcome == RevertOutcome.Reverted)
                    return new(outcome, RevertCommit: LocalTip = Revert);
                return new(outcome, RevertCommit: Revert, Reason: "merge conflict");
            });
            return new(Landing, Baseline, AdoptedAt, reverter, Append, CreateRecovery());
        }

        internal PostAdoptionRegressionRecovery CreateRecovery() =>
            new(Root, Branch, _orchestratorDirectory, Append, () =>
            {
                TargetResolutions++;
                return Target;
            }, request =>
            {
                Trace.Add("relaunch");
                Requests.Add(request);
                return AcceptRelaunch;
            }, (root, commit, branch) =>
            {
                Trace.Add($"push:{commit}");
                Pushes.Add((root, commit, branch));
                return Push();
            }, (goalId, commit, detail) => OperatorHolds.Add((goalId, commit, detail)));

        private void Append(string detail)
        {
            Events.Add(detail);
            Trace.Add(detail.Split(' ', 2)[0]);
        }

        public void Dispose() => _directory.Delete(recursive: true);
    }
}
