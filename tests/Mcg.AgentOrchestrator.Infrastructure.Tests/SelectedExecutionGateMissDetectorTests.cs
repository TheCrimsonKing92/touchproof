using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: fixture-local stores and marker paths; no external gate or process.
public sealed class SelectedExecutionGateMissDetectorTests
{
    [Theory]
    [InlineData("fresh")]
    [InlineData("reused")]
    [InlineData("covered")]
    public async Task Driver_InheritedSkippedFailure_RecordsMissAndPreservesDecision(string path)
    {
        using var control = new DriverScenario(path, detectorEnabled: false);
        using var scenario = new DriverScenario(path);
        await control.Backstop.Land();
        await scenario.Backstop.Land();

        var expected = control.Observe();
        var actual = scenario.Observe();

        Assert.Equal(FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired, actual.Kind);
        Assert.Contains("inherited from a RED Baseline arm", actual.Evidence, StringComparison.Ordinal);
        Assert.Equal(Normalize(expected), Normalize(actual));
        scenario.AssertPath();
        AssertNoMiss(await control.Backstop.Store.ReadAsync(), control.Backstop);
        var state = (await scenario.Backstop.Store.ReadAsync())!;
        var miss = Assert.IsType<SelectedExecutionBackstopMissRecord>(state.OpenMiss);
        Assert.Equal(new[] { "goal-1" }, miss.GoalIds);
        Assert.Equal(new[] { "Candidate.X.First" }, miss.FailingTests);
        var item = Assert.Single(await scenario.Backstop.Items.ListAsync());
        Assert.Equal("selected-execution-backstop-miss:gate-goal-1-main-1", item.CorrelationKey);
        Assert.Contains($"gate_goal={scenario.Goal.Id}", miss.GoalRangeNote, StringComparison.Ordinal);
        var suspension = await AcceptanceSelectedExecutionSessionSuspension.ReadAsync("unused", scenario.Backstop.Home);
        Assert.Equal(miss.Id, suspension!.MissId);
        var session = new AcceptanceSelectedExecutionSession(AcceptanceSelectedExecutionMode.Enforce, "in-memory-candidate",
            AcceptanceSelectedExecutionFixture.Changes, read: (_, _) => new(ReverseDependencyTestSelection.Resolved(["A"]),
                AcceptanceSelectedExecutionFixture.Classes, ["Candidate.Guard"]), suspension: suspension);
        Assert.Equal("Shadow", AcceptanceSelectedExecutionFixture.Observe(session).SelectedExecution!.Mode);
        Assert.Empty(scenario.Backstop.Calls);

        scenario.Observe();

        Assert.Equal(item.Id, Assert.Single(await scenario.Backstop.Items.ListAsync()).Id);
        Assert.Single(scenario.Backstop.Lines.Where(line => line.Contains("result=miss-raised ", StringComparison.Ordinal)));
        Assert.Empty(scenario.Backstop.Calls);
    }

    [Theory]
    [InlineData("fresh", "baseline-green")]
    [InlineData("reused", "baseline-green")]
    [InlineData("covered", "baseline-green")]
    [InlineData("fresh", "candidate-green")]
    [InlineData("fresh", "selected-class")]
    [InlineData("reused", "selected-class")]
    [InlineData("covered", "selected-class")]
    [InlineData("fresh", "candidate-own")]
    [InlineData("reused", "candidate-own")]
    [InlineData("covered", "candidate-own")]
    public async Task Driver_NegativeControls_LeaveMissAndSuspensionAbsent(string path, string control)
    {
        using var scenario = new DriverScenario(path, control: control);
        await scenario.Backstop.Land(record: control == "selected-class"
            ? SelectedExecutionBackstopFixture.Applied with { Lanes = [new("lane", "skipped", [], ["Other.Y"], null)] }
            : null);

        var decision = scenario.Observe();

        scenario.AssertPath();
        Assert.NotEqual(FailedGoalFindingObservationKind.None, decision.Kind);
        Assert.Empty(await scenario.Backstop.Items.ListAsync());
        AssertNoMiss(await scenario.Backstop.Store.ReadAsync(), scenario.Backstop);
        Assert.Null(await AcceptanceSelectedExecutionSessionSuspension.ReadAsync("unused", scenario.Backstop.Home));
        Assert.Empty(scenario.Diagnostics);
        Assert.Empty(scenario.Backstop.Calls);
        if (control is "selected-class" or "candidate-own")
            Assert.Single(scenario.Backstop.Lines.Where(line => line.Contains("result=gate-no-miss ", StringComparison.Ordinal)));
        else
            Assert.DoesNotContain(scenario.Backstop.Lines, line => line.Contains("result=gate-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("fresh")]
    [InlineData("reused")]
    [InlineData("covered")]
    public async Task Driver_ObservationFails_RecordsFailureAndPreservesDecision(string path)
    {
        using var control = new DriverScenario(path, detectorEnabled: false);
        using var scenario = new DriverScenario(path, observationFailure: new IOException("miss persistence unavailable"));
        await scenario.Backstop.Land();

        var expected = control.Observe();
        var actual = scenario.Observe();

        Assert.Equal(Normalize(expected), Normalize(actual));
        scenario.AssertPath();
        var line = Assert.Single(scenario.Diagnostics);
        Assert.Contains($"result=detector-failed goal={scenario.Goal.Id} attempt={scenario.AttemptId} step=record", line, StringComparison.Ordinal);
        Assert.Contains("IOException: miss persistence unavailable", line, StringComparison.Ordinal);
        var note = Assert.Single(scenario.Goal.Timeline.Where(item => item.Message.Contains("result=detector-failed", StringComparison.Ordinal)));
        Assert.Equal(scenario.Requester.Id, note.TaskId);
        Assert.Equal(line, note.Message);
        Assert.Empty(scenario.Backstop.Calls);
    }

    [Theory]
    [InlineData("fresh", false)]
    [InlineData("reused", false)]
    [InlineData("covered", false)]
    [InlineData("fresh", true)]
    [InlineData("reused", true)]
    [InlineData("covered", true)]
    public async Task Driver_CancellationOrOutOfMemory_Propagates(string path, bool outOfMemory)
    {
        Exception failure = outOfMemory ? new OutOfMemoryException("injected") : new OperationCanceledException("injected");
        using var scenario = new DriverScenario(path, observationFailure: failure);
        await scenario.Backstop.Land();

        Assert.Same(failure, Record.Exception(() => scenario.Observe()));

        Assert.Empty(scenario.Diagnostics);
        Assert.DoesNotContain(scenario.Goal.Timeline, item => item.Message.Contains("result=detector-failed", StringComparison.Ordinal));
    }

    private static FailedGoalFindingObservation Normalize(FailedGoalFindingObservation decision) => decision with
    {
        Evidence = Regex.Replace(decision.Evidence, "finding-evidence-[a-f0-9]{24}", "<attempt>")
    };

    private static void AssertNoMiss(SelectedExecutionBackstopState? state, SelectedExecutionBackstopFixture fixture)
    {
        Assert.NotNull(state);
        Assert.Null(state.OpenMiss);
        Assert.False(File.Exists(AcceptanceSelectedExecutionSessionSuspension.MarkerPath(fixture.Home)));
    }

    private sealed class DriverScenario : IDisposable
    {
        private const string CandidateSha = "abc1234";
        private readonly AgentOrchestratorKernel _kernel;
        private readonly ConductorDriver _driver;
        private readonly string _path;
        private readonly string _control;
        private int _runs;
        internal SelectedExecutionBackstopFixture Backstop { get; } = new();
        internal Goal Goal { get; }
        internal TaskSpec Requester { get; }
        internal List<string> Diagnostics { get; } = [];
        internal string AttemptId => Requester.VerificationHistory.SelectMany(record => record.FindingEvidenceReceipts ?? []).Last().ReceiptId;

        internal DriverScenario(string path, bool detectorEnabled = true, string control = "inherited", Exception? observationFailure = null)
        {
            _path = path;
            _control = control;
            (_kernel, Goal) = SoftwareGoal();
            Requester = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            var developer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            foreach (var task in Goal.Tasks.Where(task => task != Requester))
                PassVerification(_kernel, Goal, task, hasCommittedChanges: task == developer);
            var finding = EvidenceFindingWithRequest("Class needs gate evidence", "gate-evidence",
                category: FindingCategory.SpecDefect, classes: ["Candidate.X"]);
            FailReviewerNeedsWork(_kernel, Goal, Requester, "Needs evidence", findings: [finding]);
            var settings = new AcceptanceGateEngineSettings();
            _driver = MakeDriver(
                getPreReviewEvidenceContext: _ => NoPreReviewContext(CandidateSha),
                getFindingEvidenceEngineSettings: _ => settings,
                focusedEvidenceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    Backstop.Home, TrunkBranchName.Default, runInline: true, acquireStableSlotLease: (_, _) => null),
                runFocusedEvidence: (_, request) => { _runs++; return Evidence(request); },
                recordTaskNote: (goalId, taskId, text) => _kernel.RecordTaskNote(goalId, taskId, text),
                recordFindingEvidenceRequest: (goalId, taskId, text) => _kernel.RecordFindingEvidenceRequest(goalId, taskId, text),
                recordFindingEvidenceRun: (goalId, taskId, text) => _kernel.RecordFindingEvidenceRun(goalId, taskId, text),
                recordFindingEvidenceRequestDispositions: (goalId, taskId, dispositions) =>
                    _kernel.RecordFindingEvidenceRequestDispositions(goalId, taskId, dispositions),
                recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                    _kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));
            if (detectorEnabled)
            {
                var coordinator = observationFailure is null ? Backstop.Coordinator : new SelectedExecutionBackstopCoordinator(
                    Backstop.Store, Backstop.Items, Backstop.Home, () => throw observationFailure, _ => { });
                _driver.GateMissDetector = new(coordinator, Diagnostics.Add);
            }
            if (path == "fresh") return;

            Assert.True(WorkerResultBlockers.TryFindReviewFindingRound(Requester.LastVerification, out var round, out _));
            var fingerprint = ConductorDriver.BuildFindingRoundFingerprint(Requester, round);
            var request = finding.EvidenceRequest!;
            var identity = ConductorDriver.BuildFindingEvidenceIdentity(request);
            var arms = Evidence("retained evidence").Arms!.Select(ConductorDriver.CreateFindingEvidenceArmReceipt).ToArray();
            // Opaque filters require an exact receipt identity to select the covered path.
            var receiptId = path == "covered"
                ? ConductorDriver.CreateFindingEvidenceReceiptId(CandidateSha, fingerprint, identity)
                : "retained-attempt";
            var receipt = new FindingEvidenceReceipt(receiptId, CandidateSha, request, true, false, "retained red", arms,
                [new(finding.StableId, identity, "executed", Decision: FindingEvidenceRequestDecision.Executed)],
                path == "covered" ? fingerprint : "previous-round",
                ConductorDriver.BuildFindingEvidenceExecutionBasisIdentity(settings));
            _kernel.RecordFindingEvidenceOutcome(Goal.Id, Requester.Id, finding.StableId,
                new(true, receipt.ReceiptId, ResultReason: FindingEvidenceOutcomeReason.CandidateRed), receipt);
            var group = new FindingEvidenceRequestGroup(identity, "retained evidence", request, [finding]);
            var batch = FindingEvidenceRoundPlanner.BuildFindingEvidenceBatches([group]);
            var plan = FindingRoundBatchSchedule.Plan(batch, Requester.Id, [receipt], CandidateSha, fingerprint,
                receipt.ExecutionBasisIdentity!, Goal.Timeline);
            if (path == "covered") Assert.Single(plan.RedCovered);
            else
            {
                Assert.Empty(plan.RedCovered);
                Assert.True(FindingEvidenceTrxCoverage.HasContentBoundRedTrxCoverage(arms[0], ["Candidate.X"]));
            }
        }

        internal FailedGoalFindingObservation Observe()
        {
            Assert.True(_driver.TryBuildFindingEvidenceRequest(Goal, Requester, ConductorAutonomyPolicy.Permissive, out var decision));
            return decision;
        }

        internal void AssertPath()
        {
            Assert.Equal(_path == "fresh" ? 1 : 0, _runs);
            var reused = Goal.Timeline.Any(item => item.Message.Contains("disposition=reused-red;", StringComparison.Ordinal));
            Assert.Equal(_path == "reused", reused);
        }

        private FocusedEvidenceRunResult Evidence(string request)
        {
            var candidateGreen = _control == "candidate-green";
            var baselineGreen = _control == "baseline-green";
            var baselineFailure = _control == "candidate-own" ? "Other.Y.First" : "Candidate.X.First";
            var candidate = Check("candidate", "Candidate.X.First", candidateGreen);
            var baseline = Check("baseline", baselineFailure, baselineGreen);
            return new(request, true, candidateGreen, "dual-arm gate evidence", [candidate],
                Arms: [new(FindingEvidenceArm.Candidate, CandidateSha,
                    candidateGreen ? FindingEvidenceArmDisposition.Green : FindingEvidenceArmDisposition.Red,
                    true, candidateGreen, "candidate", [candidate]),
                    new(FindingEvidenceArm.Baseline, "1234567",
                    baselineGreen ? FindingEvidenceArmDisposition.Green : FindingEvidenceArmDisposition.Red,
                    true, baselineGreen, "baseline", [baseline])],
                OutcomeReason: candidateGreen ? FindingEvidenceOutcomeReason.ValidEvidence : FindingEvidenceOutcomeReason.CandidateRed);
        }

        private AcceptanceCheckResult Check(string name, string identity, bool passed)
        {
            var path = Path.Combine(Backstop.Home, name + ".trx");
            XNamespace trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            new XDocument(new XElement(trx + "TestRun",
                new XElement(trx + "TestDefinitions", new XElement(trx + "UnitTest", new XAttribute("id", "one"),
                    new XElement(trx + "TestMethod", new XAttribute("className", identity[..identity.LastIndexOf('.')]),
                        new XAttribute("name", "First")))),
                new XElement(trx + "Results", new XElement(trx + "UnitTestResult", new XAttribute("testId", "one"),
                    new XAttribute("testName", identity), new XAttribute("outcome", passed ? "Passed" : "Failed"))),
                new XElement(trx + "ResultSummary", new XElement(trx + "Counters", new XAttribute("total", 1),
                    new XAttribute("executed", 1), new XAttribute("passed", passed ? 1 : 0), new XAttribute("failed", passed ? 0 : 1)))))
                .Save(path);
            return new(name, passed, passed ? 0 : 1, "synthetic gate check", TestResultPaths: [path],
                FailingTestIdentities: passed ? [] : [identity], ExecutedTestCount: 1);
        }

        public void Dispose() => Backstop.Dispose();
    }

    [Fact]
    public async Task InheritedFailureInSkippedClassRaisesOneMissAgainstLandingAndSuspends()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        await fixture.Land();
        var before = (await fixture.Store.ReadAsync())!;
        var detector = new SelectedExecutionGateMissDetector(fixture.Coordinator);

        var result = await detector.ObserveAsync("gate-goal", Arm(FindingEvidenceArm.Candidate), Arm(FindingEvidenceArm.Baseline));

        Assert.Equal(CandidateRedBaselineVerdict.Inherited, result.Verdict);
        var detected = Assert.Single(result.Misses);
        var state = (await fixture.Store.ReadAsync())!;
        var miss = Assert.IsType<SelectedExecutionBackstopMissRecord>(state.OpenMiss);
        Assert.Equal(detected.Id, miss.Id);
        Assert.Equal(new[] { "goal-1" }, miss.GoalIds);
        Assert.Equal("main-1", miss.CoveredFromCommit);
        Assert.Equal("main-1", miss.CoveredToCommit);
        Assert.Equal(new[] { "Candidate.X.First" }, miss.FailingTests);
        Assert.Empty(result.UnclassifiableLandings);
        Assert.Empty(result.UnmatchedTests);
        Assert.Equal(before.CoveredCommit, state.CoveredCommit);
        Assert.Equal(before.RangeStartCommit, state.RangeStartCommit);
        Assert.Equal(JsonSerializer.Serialize(before.RangeLandings), JsonSerializer.Serialize(state.RangeLandings));
        Assert.Equal(before.RecordedLandingInstants, state.RecordedLandingInstants);
        var item = Assert.Single(await fixture.Items.ListAsync());
        Assert.Equal(miss.CorrelationKey, item.CorrelationKey);
        Assert.Equal(miss.Id, JsonSerializer.Deserialize<SelectedExecutionBackstopMissRecord>(item.Body)!.Id);
        Assert.Single(fixture.Lines.Where(line => line.Contains("result=miss-raised ", StringComparison.Ordinal)));
        Assert.Equal(miss.Id, (await AcceptanceSelectedExecutionSessionSuspension.ReadAsync("unused", fixture.Home))!.MissId);
        Assert.True(File.Exists(AcceptanceSelectedExecutionSessionSuspension.MarkerPath(fixture.Home)));
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task RepeatedGateMissReusesCorrelationKeyAndResolutionClearsSuspension()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        await fixture.Land();
        var detector = new SelectedExecutionGateMissDetector(fixture.Coordinator);
        var first = Assert.Single((await detector.ObserveAsync("gate-1",
            Arm(FindingEvidenceArm.Candidate), Arm(FindingEvidenceArm.Baseline))).Misses);
        var second = Assert.Single((await detector.ObserveAsync("gate-2",
            Arm(FindingEvidenceArm.Candidate), Arm(FindingEvidenceArm.Baseline))).Misses);

        Assert.Equal(first.CorrelationKey, second.CorrelationKey);
        Assert.Equal("selected-execution-backstop-miss:" + first.Id, second.CorrelationKey);
        var item = Assert.Single(await fixture.Items.ListAsync());
        Assert.Equal(first.CorrelationKey, item.CorrelationKey);
        Assert.Single(fixture.Lines.Where(line => line.Contains("result=miss-raised ", StringComparison.Ordinal)));
        Assert.Equal(first.Id, (await AcceptanceSelectedExecutionSessionSuspension.ReadAsync("unused", fixture.Home))!.MissId);

        Assert.True(await fixture.Items.TryResolveAsync(first.CorrelationKey, "operator verified fix"));
        await fixture.Service();

        Assert.Null((await fixture.Store.ReadAsync())!.OpenMiss);
        Assert.False(File.Exists(AcceptanceSelectedExecutionSessionSuspension.MarkerPath(fixture.Home)));
        Assert.Null(await AcceptanceSelectedExecutionSessionSuspension.ReadAsync("unused", fixture.Home));
        Assert.Single(fixture.Lines.Where(line => line.Contains("result=miss-resolved ", StringComparison.Ordinal)));

        await detector.ObserveAsync("gate-3", Arm(FindingEvidenceArm.Candidate), Arm(FindingEvidenceArm.Baseline));

        Assert.Equal(item.Id, Assert.Single(await fixture.Items.ListAsync()).Id);
        Assert.Null((await fixture.Store.ReadAsync())!.OpenMiss);
        Assert.False(File.Exists(AcceptanceSelectedExecutionSessionSuspension.MarkerPath(fixture.Home)));
        Assert.Single(fixture.Lines.Where(line => line.Contains("result=miss-raised ", StringComparison.Ordinal)));
        Assert.Single(fixture.Lines.Where(line => line.Contains("result=miss-resolved ", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task EveryMatchingLandingGetsAnItemAndUnreadableLandingsRemainReported(int resolvedFirst)
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        await fixture.Land();
        await fixture.Land("goal-2", "main-2", SelectedExecutionBackstopFixture.Applied with
        {
            Lanes = [new("lane", "skipped", [], ["Other.Y"], null)]
        });
        await fixture.Coordinator.NoteLandingAsync("goal-u", "main-3", null, "main-2");
        var detector = new SelectedExecutionGateMissDetector(fixture.Coordinator);
        string[] failures = ["Candidate.X.First", "Other.Y.Second", "Selected.Z.Third"];

        var result = await detector.ObserveAsync("gate", Arm(FindingEvidenceArm.Candidate, failures),
            Arm(FindingEvidenceArm.Baseline, failures));

        Assert.Equal(new[] { "goal-1", "goal-2" }, result.Misses.Select(miss => Assert.Single(miss.GoalIds)));
        Assert.Equal(new[] { "main-1", "main-2" }, result.Misses.Select(miss => miss.CoveredToCommit));
        Assert.Equal(new[] { "Candidate.X.First" }, result.Misses[0].FailingTests);
        Assert.Equal(new[] { "Other.Y.Second" }, result.Misses[1].FailingTests);
        Assert.Equal("goal-u", Assert.Single(result.UnclassifiableLandings).GoalId);
        Assert.Equal(new[] { "Selected.Z.Third" }, result.UnmatchedTests);
        var items = await fixture.Items.ListAsync();
        Assert.Equal(2, items.Count);
        Assert.All(result.Misses, miss => Assert.Single(items.Where(item => item.CorrelationKey == miss.CorrelationKey)));
        Assert.Equal(result.Misses[0].Id, (await fixture.Store.ReadAsync())!.OpenMiss!.Id);
        Assert.NotNull(await AcceptanceSelectedExecutionSessionSuspension.ReadAsync("unused", fixture.Home));

        await detector.ObserveAsync("repeat", Arm(FindingEvidenceArm.Candidate, failures), Arm(FindingEvidenceArm.Baseline, failures));
        Assert.Equal(2, (await fixture.Items.ListAsync()).Count);
        Assert.Equal(2, fixture.Lines.Count(line => line.Contains("result=miss-raised ", StringComparison.Ordinal)));

        var first = result.Misses[resolvedFirst];
        var remaining = result.Misses[1 - resolvedFirst];
        Assert.True(await fixture.Items.TryResolveAsync(first.CorrelationKey, "first landing verified"));
        Assert.NotNull(await AcceptanceSelectedExecutionSessionSuspension.ReadAsync("unused", fixture.Home));

        // A restarted coordinator must recover every open miss from canonical items.
        var restarted = fixture.CreateCoordinator();
        await restarted.ServiceAsync();

        Assert.Equal(remaining.Id, (await fixture.Store.ReadAsync())!.OpenMiss!.Id);
        Assert.Equal(remaining.Id,
            (await AcceptanceSelectedExecutionSessionSuspension.ReadAsync("unused", fixture.Home))!.MissId);
        Assert.True(File.Exists(AcceptanceSelectedExecutionSessionSuspension.MarkerPath(fixture.Home)));
        Assert.Empty(fixture.Calls);

        Assert.True(await fixture.Items.TryResolveAsync(remaining.CorrelationKey, "second landing verified"));
        await restarted.ServiceAsync();

        Assert.Null((await fixture.Store.ReadAsync())!.OpenMiss);
        Assert.Null(await AcceptanceSelectedExecutionSessionSuspension.ReadAsync("unused", fixture.Home));
        Assert.False(File.Exists(AcceptanceSelectedExecutionSessionSuspension.MarkerPath(fixture.Home)));
        Assert.Equal(2, (await fixture.Items.ListAsync()).Count);
        Assert.Equal(2, fixture.Lines.Count(line => line.Contains("result=miss-raised ", StringComparison.Ordinal)));
    }

    private static FindingEvidenceArmReceipt Arm(FindingEvidenceArm arm, string[]? failures = null) =>
        new(arm, arm == FindingEvidenceArm.Candidate ? "candidate-sha" : "baseline-sha", FindingEvidenceArmDisposition.Red,
            Accepted: true, Passed: false, Summary: "synthetic evidence",
            FailingTestIdentities: failures ?? ["Candidate.X.First"], TestResultPaths: []);
}
