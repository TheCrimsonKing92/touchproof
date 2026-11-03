using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static Mcg.AgentOrchestrator.Infrastructure.Tests.LandingExecutorTestsDenylistOwnerHold;

// Each scenario owns its repository and stores; the collection isolates landing git hooks.
[Collection(TestCollections.LandingGitRunner)]
public sealed class PassedCohortApprovalOverlapEventTests : AcceptanceCohortWorkflowTests
{
    [Theory(Timeout = 30_000)]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "CrossTick")]
    public void NormalTicks_StreamOneStructuredObservationPerReceiptEachTick(bool overlap)
    {
        using var scenario = new PassedCohortReceiptApprovalHoldTests.Scenario();
        using var capacity = PassedCohortReceiptApprovalHoldTests.Scenario.PauseNewGates();
        var partnerPath = scenario.Receipt.Identity.Members.Single(member => member.GoalId == scenario.Goals[1].Id).LandingPaths.Single();
        var other = CreateCompletedGoal(scenario.Kernel, "Other gate-ready candidate", scenario.Repo);
        var revision = CreateWorktreeCandidate(scenario.Repo, other.Id,
            overlap ? partnerPath : "tests/UnrelatedOverlapCandidate.cs", "other candidate");
        var projection = Assert.IsType<GateReadyCandidateProjectionResult.Ready>(
            scenario.Driver.ProjectGroupedAdmissionCandidate(other, ConductorAutonomyPolicy.Permissive, scenario.Kernel)).Projection;
        Assert.Equal(scenario.Receipt.Identity.ObservedMainRevision, projection.MainRevision);
        var beforeReceipt = ReceiptState(scenario);
        var observedAt = scenario.Receipt.CompletedAt.AddMilliseconds(42_900);
        var tickCount = 0;
        new ConductorBatchLoop(conductEventLogWriter: new ConductEventLogWriter(scenario.Workspace.ConductEventsLogPath),
            utcNow: () => observedAt).Run(scenario.Kernel, scenario.Driver, ConductorAutonomyPolicy.Permissive,
            Path.Combine(scenario.Repo, "stop-does-not-exist"), maxIterations: 2,
            watchInterval: TimeSpan.FromSeconds(1), sleepFunc: _ => false,
            onTick: summary =>
            {
                tickCount++;
                Assert.NotNull(summary.ProgressLines);
                Assert.Single(summary.ProgressLines.Where(line => line.StartsWith("COHORT_APPROVAL_OVERLAP ", StringComparison.Ordinal)));
                scenario.AssertPartnerReleased(summary.ProgressLines, heldIndex: 0);
                // Runs inside the normal event-writer scope: an unrelated prefix must stay rejected.
                ConductorBatchLoop.EmitProgress("UNRELATED_APPROVAL_OBSERVATION unknown=true");
            });

        Assert.Equal(2, tickCount);
        var events = ReadEvents(scenario.Workspace.ConductEventsLogPath);
        Assert.DoesNotContain(events, record => record.Detail.StartsWith("UNRELATED_APPROVAL_OBSERVATION", StringComparison.Ordinal));
        var observations = events.Where(record => record.Detail.StartsWith("COHORT_APPROVAL_OVERLAP ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, observations.Length);
        Assert.Equal(observations[0].Detail, observations[1].Detail);
        Assert.All(observations, record =>
        {
            Assert.Equal("acceptance-cohort", record.EventKind);
            using var document = JsonDocument.Parse(record.Detail["COHORT_APPROVAL_OVERLAP ".Length..]);
            var root = document.RootElement;
            Assert.Equal(scenario.Receipt.ReceiptId, root.GetProperty("receiptIdentity").GetProperty("receiptId").GetString());
            Assert.Equal(scenario.Receipt.Identity.Value, root.GetProperty("receiptIdentity").GetProperty("value").GetString());
            Assert.Equal(scenario.Receipt.CompletedAt.ToUniversalTime(), root.GetProperty("completedAt").GetDateTimeOffset());
            Assert.Equal(42, root.GetProperty("receiptAgeSeconds").GetInt64());
            Assert.Equal(scenario.Goals.Select(goal => goal.Id.Value).Order(StringComparer.Ordinal),
                root.GetProperty("heldMemberIds").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(new[] { scenario.Goals[0].Id.Value },
                root.GetProperty("missingApprovalIds").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(overlap ? 1 : 0, root.GetProperty("totalOverlapGoals").GetInt32());
            Assert.Equal(0, root.GetProperty("omittedGoalCount").GetInt32());
            var rows = root.GetProperty("overlaps").EnumerateArray().ToArray();
            if (overlap)
            {
                var row = Assert.Single(rows);
                Assert.Equal(other.Id.Value, row.GetProperty("goalId").GetString());
                Assert.Equal(revision, row.GetProperty("candidateRevision").GetString());
                Assert.Equal(1, row.GetProperty("sharedPathCount").GetInt32());
                Assert.Equal(new[] { partnerPath }, row.GetProperty("sharedPaths").EnumerateArray().Select(value => value.GetString()));
                Assert.Equal(0, row.GetProperty("omittedPathCount").GetInt32());
            }
            else Assert.Empty(rows);
        });
        Assert.Equal(beforeReceipt, ReceiptState(scenario));
        Assert.Equal(1, scenario.Verifier.RunCount);
        Assert.All(scenario.Goals.Append(other), goal =>
        {
            Assert.Equal(GoalStatus.Verified, goal.Status);
            Assert.Empty(scenario.Store.ReadFinalizedLandingsForGoal(goal.Id));
            AssertNoLanded(scenario.Workspace, goal.Id);
        });
    }

    [Fact]
    public void ForwardingOrSuppressingObservation_PreservesAdmissionAndMemberState()
    {
        using var scenario = new PassedCohortReceiptApprovalHoldTests.Scenario();
        var other = CreateCompletedGoal(scenario.Kernel, "Overlapping ready candidate", scenario.Repo);
        var partnerPath = scenario.Receipt.Identity.Members.Single(member => member.GoalId == scenario.Goals[1].Id).LandingPaths.Single();
        CreateWorktreeCandidate(scenario.Repo, other.Id, partnerPath, "other candidate");
        var goals = scenario.Goals.Append(other).ToArray();
        var candidates = goals.Select(goal => new ConductorSpeculativeAcceptanceCandidate(goal.Id,
            scenario.Driver.ProjectGroupedAdmissionCandidate(goal, ConductorAutonomyPolicy.Permissive, scenario.Kernel))).ToArray();
        var beforeGoals = scenario.GoalState();
        var beforeReceipt = ReceiptState(scenario);

        var suppressed = Apply(forwardObservation: false);
        var forwarded = Apply(forwardObservation: true);
        Assert.Equal(suppressed.Eligible, forwarded.Eligible);
        Assert.Equal(suppressed.Candidates, forwarded.Candidates);
        Assert.Equal(suppressed.Results, forwarded.Results);
        Assert.Equal(suppressed.Progress, forwarded.Progress.Where(line => !line.StartsWith("COHORT_APPROVAL_OVERLAP ", StringComparison.Ordinal)));
        var observation = Assert.Single(forwarded.Progress.Where(line => line.StartsWith("COHORT_APPROVAL_OVERLAP ", StringComparison.Ordinal)));
        using var document = JsonDocument.Parse(observation["COHORT_APPROVAL_OVERLAP ".Length..]);
        Assert.Equal(other.Id.Value, Assert.Single(document.RootElement.GetProperty("overlaps").EnumerateArray()).GetProperty("goalId").GetString());
        Assert.Equal(new[] { scenario.Goals[1].Id, other.Id }, forwarded.Eligible);
        Assert.Equal(beforeGoals, scenario.GoalState());
        Assert.Equal(beforeReceipt, ReceiptState(scenario));
        Assert.Equal(1, scenario.Verifier.RunCount);

        (GoalId[] Eligible, ConductorSpeculativeAcceptanceCandidate[] Candidates, string Results, string[] Progress) Apply(bool forwardObservation)
        {
            var results = new Dictionary<string, ParallelLandingOutcome>();
            var progress = new List<string>();
            var changed = new HashSet<GoalId>();
            var admitted = PassedCohortReceiptApprovalHoldAdmission.ApplyBeforeGating(
                scenario.Driver, ConductorAutonomyPolicy.Permissive, scenario.Kernel, goals, candidates,
                goals, new HashSet<GoalId>(), _ => true, new HashSet<string>(), results, 1,
                line => { if (forwardObservation || !line.StartsWith("COHORT_APPROVAL_OVERLAP ", StringComparison.Ordinal)) progress.Add(line); },
                () => false, _ => false, changed, scenario.Receipt.CompletedAt.AddSeconds(7));
            Assert.Empty(changed);
            Assert.Equal(scenario.Goals[0].Id.Value, Assert.Single(results).Key);
            Assert.True(PassedCohortReceiptApprovalHold.IsReadOnlyReservation(results[scenario.Goals[0].Id.Value].Result));
            Assert.Equal(beforeGoals, scenario.GoalState());
            Assert.Equal(beforeReceipt, ReceiptState(scenario));
            Assert.All(goals, goal => Assert.Empty(scenario.Store.ReadFinalizedLandingsForGoal(goal.Id)));
            return (admitted.Eligible.Select(goal => goal.Id).ToArray(), admitted.Candidates, JsonSerializer.Serialize(results), progress.ToArray());
        }
    }

    private static string ReceiptState(PassedCohortReceiptApprovalHoldTests.Scenario scenario) =>
        JsonSerializer.Serialize(scenario.Store.TryReadReceipt(scenario.Receipt.Identity.Value));

    private static ConductEventRecord[] ReadEvents(string path) => File.ReadAllLines(path)
        .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToArray();
}
