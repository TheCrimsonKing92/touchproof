using System.Text.Json;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: synthetic workers, goal-local state and unique temporary roots. No worker is launched.
public sealed class FindingRoundBatchCompletionTests
{
    private const int CrossTickHangFailsafeMilliseconds = 30_000;

    [Theory(Timeout = CrossTickHangFailsafeMilliseconds)]
    [Trait("Category", "CrossTick")]
    [InlineData(false)]
    [InlineData(true)]
    public void StaggeredReceipts_DrainBeforeOneRoleSelection(bool sourceBlocker)
    {
        using var scenario = new Scenario(sourceBlocker);
        for (var tick = 0; tick < 2; tick++)
        {
            var result = scenario.Driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive);
            Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
            Assert.Equal(tick + 1, scenario.Runs.Count);
            Assert.Empty(scenario.Retries);
            Assert.Empty(DispatchReadinessRules.BuildReadyTaskParallelPlan(scenario.Goal).Batches);
            Assert.Empty(scenario.Prepare());
            scenario.Restart();
        }
        scenario.Driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive);
        Assert.Equal(3, scenario.Runs.Count);
        Assert.Equal(sourceBlocker ? scenario.Developer.Id : scenario.Tester.Id, Assert.Single(scenario.Retries));
        var summary = scenario.ClosingSummary();
        Assert.Equal(3, summary.BatchSize);
        Assert.Equal(3, summary.NewlyExecuted);
        Assert.Equal(0, summary.Unavailable);
        Assert.Contains(sourceBlocker ? "Developer" : "Tester", summary.RoutingReason, StringComparison.Ordinal);
        if (!sourceBlocker)
        {
            Assert.Equal(2, summary.CoalescedSelectionCount);
            Assert.Equal(2, summary.TesterCallsAvoided);
            Assert.Equal(0, summary.ReviewerCallsAvoided);
            Assert.Equal(2, summary.PaidVerificationCallsAvoided);
            Assert.All(summary.EligibilityWitnesses, witness =>
            {
                Assert.NotNull(witness.ReceiptId);
                Assert.Contains("interpretation route", witness.Eligibility!, StringComparison.Ordinal);
            });
            var prepared = Assert.Single(scenario.Prepared);
            Assert.Equal(scenario.Tester.Id, prepared.Task.Id);
            Assert.True(File.Exists(prepared.PromptPath));
        }
        Assert.Equal(0, scenario.ProcessStarts);
        Assert.DoesNotContain(scenario.Goal.Tasks, task => task.LastProcess is { IsRunning: true });
    }

    [Fact(Timeout = CrossTickHangFailsafeMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void TerminalFailure_SettlesUnavailableAndDrainsLaterRequests()
    {
        using var scenario = new Scenario(false, rejectFirst: true);
        for (var tick = 0; tick < 3; tick++)
        {
            scenario.Driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive);
            Assert.Empty(scenario.Retries);
        }
        Assert.Equal(3, scenario.Runs.Count);
        var summary = scenario.ClosingSummary();
        Assert.Equal(3, summary.BatchSize);
        Assert.Equal(1, summary.Unavailable);
        Assert.Equal(2, summary.NewlyExecuted);
        Assert.Equal(0, summary.Covered);
        Assert.Single(scenario.Escalations);
        scenario.Restart();
        scenario.Driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive);
        Assert.Equal(3, scenario.Runs.Count);
        Assert.Single(scenario.Escalations);
        Assert.Equal(0, scenario.ProcessStarts);
    }

    [Fact(Timeout = CrossTickHangFailsafeMilliseconds)]
    [Trait("Category", "CrossTick")]
    public async Task ConcurrentClose_AdmitsOneRetryAndSurvivesSnapshot()
    {
        using var scenario = new Scenario(true);
        FailedGoalFindingObservation observation = FailedGoalFindingObservation.None;
        for (var index = 0; index < 3; index++)
            Assert.True(scenario.Driver.TryBuildFindingEvidenceRequest(scenario.Goal, scenario.Tester,
                ConductorAutonomyPolicy.Permissive, out observation));
        var closure = Assert.IsType<FindingEvidenceBatchClosure>(observation.BatchClosure);
        Assert.Equal(scenario.Developer.Id, observation.TargetTaskId);
        using var barrier = new Barrier(2);
        var applied = 0;
        async Task<bool> Close() => await Task.Run(() =>
        {
            if (!barrier.SignalAndWait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("Both batch close attempts did not reach the admission barrier.");
            return FindingRoundBatchCoordinator.TryApply(scenario.Driver, scenario.Goal, closure, () => true, () =>
            {
                Interlocked.Increment(ref applied);
                // Keep the requesting round readable: duplicate protection must come from admission,
                // rather than accidentally passing because the first retry erased its verification.
                scenario.Kernel.RetryTask(scenario.Goal.Id, scenario.Developer.Id, "Batch drained; repair source.", invalidateDownstream: false);
                return true;
            });
        });
        var results = await Task.WhenAll(Close(), Close());
        Assert.Single(results.Where(result => result));
        Assert.Equal(1, applied);
        Assert.Single(scenario.Goal.Timeline.Where(item => item.Kind == ProgressKind.TaskRetried));
        scenario.Restart();
        Assert.False(FindingEvidenceBatchClosureAdmission.TryApply(scenario.Goal, closure, () => true,
            () => throw new InvalidOperationException("A closed batch retried after restoration."),
            _ => throw new InvalidOperationException("A closed batch was recorded again.")));
        Assert.Equal(3, scenario.ClosingSummary().BatchSize);
    }

    [Fact]
    public void Accounting_DistinctMembershipAndOutcomesSurviveReplay()
    {
        using var scenario = new Scenario(false);
        var identity = FindingRoundBatchCoordinator.Identity(scenario.Driver, scenario.Goal, scenario.Tester)!;
        var duplicate = scenario.Findings[0] with { StableId = "same-request" };
        FindingRoundBatchAccounting.Begin(scenario.Goal, identity, [.. scenario.Findings, duplicate], scenario.Record);
        var members = FindingRoundBatchAccounting.Read(scenario.Goal, identity).Where(item => item.Kind == "member").ToArray();
        Assert.Equal(3, members.Length);
        for (var replay = 0; replay < 2; replay++)
        {
            for (var index = 0; index < members.Length; index++)
                FindingRoundBatchAccounting.Record(scenario.Goal, identity,
                    members[index] with { Kind = new[] { "executed", "covered", "unavailable" }[index], ReceiptId = "receipt-" + index }, scenario.Record);
            scenario.Restart();
        }
        var summary = FindingRoundBatchAccounting.Summarize(scenario.Goal, scenario.Tester, identity, "evidence-unavailable");
        Assert.Equal(3, summary.BatchSize);
        Assert.Equal(1, summary.NewlyExecuted);
        Assert.Equal(1, summary.Covered);
        Assert.Equal(1, summary.Unavailable);
        Assert.Equal(0, summary.CoalescedSelectionCount);
        Assert.Equal(0, summary.PaidVerificationCallsAvoided);
    }

    [Fact]
    public void ChangedIdentity_DoesNotInheritAccountingOrClose()
    {
        using var scenario = new Scenario(false);
        var identity = FindingRoundBatchCoordinator.Identity(scenario.Driver, scenario.Goal, scenario.Tester)!;
        FindingRoundBatchAccounting.Begin(scenario.Goal, identity, scenario.Findings, scenario.Record);
        FindingRoundBatchAccounting.Record(scenario.Goal, identity,
            new(identity.Key, "suppressed-selection", "receipt:tester", [], "receipt", null, "unknown counterfactual"), scenario.Record);
        FindingRoundBatchAccounting.Record(scenario.Goal, identity,
            new(identity.Key, "suppressed-selection", "receipt:tester", [], "receipt", null, "unknown counterfactual"), scenario.Record);
        var summary = FindingRoundBatchAccounting.Summarize(scenario.Goal, scenario.Tester, identity, "pending");
        Assert.Equal(1, summary.UnknownSelections);
        Assert.Null(summary.PaidVerificationCallsAvoided);
        Assert.Equal(0, summary.CoalescedSelectionCount);
        foreach (var changed in new[]
        {
            identity with { RequestingTaskId = scenario.Developer.Id }, identity with { CandidateSha = "def5678" },
            identity with { RoundFingerprint = "new-round" }, identity with { ExecutionBasisIdentity = "new-basis" }
        })
        {
            Assert.Empty(FindingRoundBatchAccounting.Read(scenario.Goal, changed));
            Assert.False(FindingRoundBatchCoordinator.TryApply(scenario.Driver, scenario.Goal, new(changed, "changed"),
                () => true, () => throw new InvalidOperationException("A changed batch inherited closure.")));
        }
    }

    private sealed class Scenario : IDisposable
    {
        private const string Candidate = "abc1234";
        private readonly string _root = ConductorDriverTests.CreateTempDirectory();
        private readonly bool _sourceBlocker;
        private readonly bool _rejectFirst;
        internal Scenario(bool sourceBlocker, bool rejectFirst = false)
        {
            _sourceBlocker = sourceBlocker;
            _rejectFirst = rejectFirst;
            Kernel = new AgentOrchestratorKernel();
            Goal = Kernel.CreateGoal("Drain finding evidence before verification", [
                new TaskSpec(TaskId.New(), "Implement source", AgentRole.Developer),
                new TaskSpec(TaskId.New(), "Verify source", AgentRole.Tester),
                new TaskSpec(TaskId.New(), "Review source", AgentRole.Reviewer)]);
            Kernel.ActivateGoal(Goal.Id, DefaultAgents());
            PassVerification(Kernel, Goal, Developer, hasCommittedChanges: true);
            Kernel.RecordDispatchResultCommit(Goal.Id, Developer.Id, Candidate);
            Findings = [
                EvidenceFindingWithRequest("Only blocking finding requires interpretation", "first",
                    sourceBlocker ? FindingCategory.Correctness : FindingCategory.TestCoverage, classes: ["AlphaTests"]),
                EvidenceFindingWithRequest("Second request", "second", project: "Core.Tests", classes: ["BetaTests"]) with { Severity = FindingSeverity.Advisory },
                EvidenceFindingWithRequest("Third request", "third", classes: ["FullyQualifiedName~GammaTests.Case&Category!=Slow"]) with { Severity = FindingSeverity.Advisory }];
            DispatchTask(Kernel, Goal, Tester, "test", workingDirectory: _root, baseCommit: Candidate);
            var stdout = string.Join(Environment.NewLine, "WORKER_RESULT:", "files: none", "commands: inspect",
                "tests: deferred - acceptance owns execution", "commit: none", "blockers: none",
                $"findings: {JsonSerializer.Serialize(Findings)}", "touched_anchors: []", "verdict: needs-work",
                "model_fit: fixture/model - adequate - source verification", "skills: none", "confidence: high", "END_WORKER_RESULT");
            Kernel.RecordDispatchExecutionResult(Goal.Id, Tester.Id, new("test", _root, 0, stdout, "",
                DateTimeOffset.UtcNow, StandardOutputPath: Path.Combine(_root, "tester.log"), WorkerResultPresent: true));
            Driver = CreateDriver();
        }
        internal AgentOrchestratorKernel Kernel { get; private set; }
        internal Goal Goal { get; private set; }
        internal TaskSpec Developer => Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        internal TaskSpec Tester => Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        internal ReviewFinding[] Findings { get; }
        internal ConductorDriver Driver { get; private set; }
        internal List<string> Runs { get; } = [];
        internal List<TaskId> Retries { get; } = [];
        internal List<string> Escalations { get; } = [];
        internal List<WorkerProfileDispatchResult> Prepared { get; } = [];
        internal int ProcessStarts { get; private set; }
        internal void Record(GoalId goal, TaskId task, string message) => Kernel.RecordFindingEvidenceRequest(goal, task, message);
        internal void Restart()
        {
            var id = Goal.Id;
            Kernel = AgentOrchestratorKernel.FromSnapshot(Kernel.ExportSnapshot());
            Goal = Kernel.GetGoal(id);
            Driver = CreateDriver();
        }
        internal IReadOnlyList<WorkerProfileDispatchResult> Prepare()
        {
            var ready = DispatchReadinessRules.SelectFirstParallelSafeAssignedBatch(Goal, DefaultAgents());
            return Goal.Tasks.Where(task => ready.TaskIds.Contains(task.Id)).Select(task => WorkerProfileDispatcher.PrepareTask(
                Kernel, Goal, task, new WorkerProfile("fixture", "worker --prompt {promptPath}"), Path.Combine(_root, "prompts"),
                _root, DateTimeOffset.UtcNow, sandboxOptions: new WorkerSandboxOptions(false, "fixture", "fixture"))).ToArray();
        }
        private ConductorDriver CreateDriver() => MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(Candidate),
            focusedEvidenceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(_root,
                TrunkBranchName.Default, runInline: true, acquireStableSlotLease: (_, _) => null),
            runFocusedEvidence: (_, request) =>
            {
                Runs.Add(request);
                if (_rejectFirst && Runs.Count == 1) return new(request, false, false, "Request unavailable", []);
                if (_sourceBlocker) return CandidateRedFindingEvidence(request, Candidate, "Fixture.SharedSetupTests.SetupFails");
                var green = ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(_root, request, Candidate);
                return green with { Arms = green.Arms!.Where(arm => arm.Arm == FindingEvidenceArm.Candidate).ToArray(), OutcomeReason = FindingEvidenceOutcomeReason.ValidEvidence };
            },
            retryTaskWithRoundKind: (goal, task, message, round) =>
            {
                Retries.Add(task);
                return Kernel.RetryTask(goal, task, message, retryRoundKind: round);
            },
            dispatchAndStart: _ =>
            {
                Prepared.AddRange(Prepare());
                return DispatchStartOutcome.Deferred("Preparation only; no process launcher configured.");
            },
            startRecordedDispatches: _ => { ProcessStarts++; throw new InvalidOperationException("Paid worker start prohibited."); },
            writeEscalation: (_, _, message) => Escalations.Add(message),
            recordFindingEvidenceRequest: Record,
            recordFindingEvidenceRun: (goal, task, message) => Kernel.RecordFindingEvidenceRun(goal, task, message),
            recordFindingEvidenceRequestDispositions: (goal, task, dispositions) => Kernel.RecordFindingEvidenceRequestDispositions(goal, task, dispositions),
            recordFindingEvidenceOutcome: (goal, task, id, outcome, receipt) => Kernel.RecordFindingEvidenceOutcome(goal, task, id, outcome, receipt));
        internal FindingRoundBatchAccounting.Summary ClosingSummary()
        {
            var item = Assert.Single(Goal.Timeline.Where(item => item.Message.StartsWith("finding-evidence batch-closed;", StringComparison.Ordinal)));
            return JsonSerializer.Deserialize<FindingRoundBatchAccounting.Summary>(item.Message[(item.Message.IndexOf('{'))..])!;
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
