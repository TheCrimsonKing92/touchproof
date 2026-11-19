using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel safe: private filesystem roots, in-memory kernel, controlled attempts; no real processes.
public sealed class PreTesterPartialCoverageTests
{
    private const string CandidateSha = "bbb2222";
    private const string Project = "Infrastructure.Tests";

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void MixedRequestRunsOnlyCAndRetainsOriginalReceiptAcrossRoundTrip()
    {
        using var scenario = new Scenario();
        var original = scenario.RunPreTester();
        var originalBytes = File.ReadAllText(original.ResultPath);
        scenario.Report("BetaTests", "GammaTests");
        var driver = scenario.Driver(); // A fresh coordinator reads the persisted authority.

        scenario.Tick(driver);

        Assert.Equal(["AlphaTests", "BetaTests"], Classes(scenario.Requests[0]));
        Assert.Equal(["GammaTests"], Classes(scenario.Requests[1]));
        Assert.Equal(2, scenario.Requests.Count);
        var verification = scenario.Verification;
        var source = verification.FindingEvidenceReceipts!.Single(receipt =>
            receipt.ReceiptId == original.FocusedEvidenceReceiptId);
        Assert.Equal(JsonSerializer.Serialize(original.OriginalFocusedReceipt), JsonSerializer.Serialize(source));
        Assert.Equal(["AlphaTests", "BetaTests"], source.Request.Selections.Select(selection => selection.TestClass));
        Assert.Equal(originalBytes, File.ReadAllText(original.ResultPath));
        var outcome = scenario.Finding.EvidenceOutcome!;
        Assert.Contains(source.ReceiptId, outcome.SourceReceiptIds!);
        Assert.Equal(2, outcome.SourceReceiptIds!.Distinct().Count());
        Assert.Equal("executed-uncovered-after-reuse:partial", outcome.DecisionReason);
        Assert.Equal(ReviewFindingState.Resolved, scenario.Finding.State);
        Assert.Equal(WorkTaskStatus.Completed, scenario.Tester.Status);
        Assert.Contains(verification.FindingEvidenceRequestDispositions!, disposition =>
            disposition.RequestIdentity.Contains("BetaTests", StringComparison.Ordinal) &&
            disposition.SourceReceiptIds?.Contains(source.ReceiptId) == true);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void CompositeGreen_RestoresExecutionCreditWhileLaterRequestIsPending()
    {
        using var scenario = new Scenario();
        var original = scenario.RunPreTester();
        scenario.Report("BetaTests", "GammaTests", includeLaterRequest: true);
        scenario.RecordBatchAccounting = false;
        scenario.Tick(scenario.Driver());
        var outcome = scenario.Finding.EvidenceOutcome!;
        Assert.Equal("executed-uncovered-after-reuse:partial", outcome.DecisionReason);
        Assert.Contains(original.FocusedEvidenceReceiptId!, outcome.SourceReceiptIds!);
        Assert.Equal(["GammaTests"], Classes(scenario.Requests[1]));
        Assert.Equal(2, scenario.Requests.Count);

        scenario.Reload();
        scenario.RecordBatchAccounting = true;
        var gate = new ConductorParallelAcceptanceAttemptCompletionGateForTests();
        var driver = scenario.Driver(gate);
        scenario.Tick(driver);

        var later = gate.RequiredHandleForTests(scenario.Goal.Id.Value);
        Assert.Equal(["DeltaTests"], Classes(later.Attempt.FocusedEvidenceRequest!));
        var identity = FindingRoundBatchCoordinator.Identity(driver, scenario.Goal, scenario.Tester)!;
        var observations = FindingRoundBatchAccounting.Read(scenario.Goal, identity);
        var executed = Assert.Single(observations.Where(item => item.Kind == "executed"));
        Assert.Equal(outcome.ReceiptId, executed.ReceiptId);
        Assert.Equal(new[] { "mixed" }, executed.FindingIds);
        Assert.DoesNotContain(observations, item => item.Kind == "covered" && item.FindingIds.Contains("mixed"));
        Assert.False(FindingRoundBatchAccounting.IsClosed(scenario.Goal, identity));
        Assert.DoesNotContain(scenario.Goal.Timeline, item => item.Kind == ProgressKind.TaskRetried);
        later.CompleteForTests();
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void PendingRemainderDeduplicatesAndPassingCompletionClosesFinding()
    {
        using var scenario = new Scenario();
        scenario.RunPreTester();
        scenario.Report("BetaTests", "GammaTests");
        var gate = new ConductorParallelAcceptanceAttemptCompletionGateForTests();
        var driver = scenario.Driver(gate);

        scenario.Tick(driver);
        var pending = gate.RequiredHandleForTests(scenario.Goal.Id.Value);
        Assert.Equal(["GammaTests"], Classes(pending.Attempt.FocusedEvidenceRequest!));
        scenario.Tick(driver);
        Assert.Equal(1, gate.HeldCount);
        Assert.Single(scenario.Requests);
        Assert.Equal(ReviewFindingState.Open, scenario.Finding.State);
        Assert.Equal(FindingEvidenceExecutionState.PendingExecution,
            FindingEvidenceExecutionClassifier.Classify(scenario.Tester, scenario.Finding, CandidateSha));

        pending.CompleteForTests();
        scenario.Tick(driver);

        Assert.Equal(2, scenario.Requests.Count);
        Assert.Equal(ReviewFindingState.Resolved, scenario.Finding.State);
        Assert.Equal(2, scenario.Verification.FindingEvidenceReceipts!.Count);
        scenario.Tick(driver);
        Assert.Equal(2, scenario.Requests.Count);
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    [InlineData("red")]
    [InlineData("rejected")]
    [InlineData("incomplete")]
    [InlineData("not-run")]
    public void UnsuccessfulRemainderCannotCloseCombinedFinding(string outcome)
    {
        using var scenario = new Scenario();
        scenario.RunPreTester();
        scenario.Report("BetaTests", "GammaTests");
        scenario.NextResult = request => scenario.Unsuccessful(request, outcome);
        var driver = scenario.Driver();
        scenario.Tick(driver);

        Assert.Equal(["GammaTests"], Classes(scenario.Requests[1]));
        Assert.Equal(ReviewFindingState.Open, scenario.Finding.State);
        Assert.NotEqual(WorkTaskStatus.Completed, scenario.Tester.Status);
        Assert.False(FindingReceiptClosureDiagnosis.Evaluate(scenario.Goal, scenario.Tester, CandidateSha).IsClosable);
        if (outcome == "not-run")
        {
            var identity = FindingRoundBatchCoordinator.Identity(driver, scenario.Goal, scenario.Tester)!;
            var incomplete = Assert.Single(scenario.Verification.FindingEvidenceReceipts!, receipt =>
                receipt is { Accepted: true, Passed: true } && (receipt.RequestDispositions ?? []).Any(disposition =>
                    disposition.Reason == "reused-pre-tester-invalidated-or-incomplete"));
            Assert.DoesNotContain(FindingRoundBatchAccounting.Read(scenario.Goal, identity),
                item => item.Kind == "suppressed-selection" && item.ReceiptId == incomplete.ReceiptId);
            var summary = FindingRoundBatchAccounting.Summarize(scenario.Goal, scenario.Tester, identity, "pending");
            Assert.Equal(0, summary.TesterCallsAvoided);
            Assert.Equal(0, summary.PaidVerificationCallsAvoided);

            scenario.NextResult = request => scenario.Unsuccessful(request, outcome);
            scenario.Tick(driver);

            Assert.Equal(3, scenario.Requests.Count);
            Assert.Equal(["GammaTests"], Classes(scenario.Requests[2]));
            Assert.Equal(FindingEvidenceExecutionState.PendingExecution,
                FindingEvidenceExecutionClassifier.Classify(scenario.Tester, scenario.Finding, CandidateSha));
            Assert.DoesNotContain(scenario.Tester.Id,
                DispatchReadinessRules.SelectFirstParallelSafeAssignedBatch(scenario.Goal, DefaultAgents()).TaskIds);
            Assert.DoesNotContain(scenario.Goal.Timeline, item => item.Kind == ProgressKind.TaskRetried &&
                item.TaskId == scenario.Tester.Id);
            Assert.DoesNotContain(scenario.Goal.Timeline, item => item.TaskId == scenario.Tester.Id &&
                item.Message.StartsWith("finding-evidence batch-closed;", StringComparison.Ordinal));
        }
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    [InlineData("candidate")]
    [InlineData("basis")]
    [InlineData("missing-trx")]
    [InlineData("changed-trx")]
    [InlineData("missing-result")]
    [InlineData("changed-result")]
    [InlineData("legacy")]
    [InlineData("not-run")]
    [InlineData("negative-control")]
    public void InvalidAuthoritySelectsBothRequestedClasses(string control)
    {
        using var scenario = new Scenario();
        if (control == "not-run") scenario.NextResult = request => scenario.Green(request) with
        {
            Arms = scenario.Green(request).Arms!.Select(arm => arm.Arm == FindingEvidenceArm.Candidate
                ? arm with { NotRunSelections = [new(Project, "BetaTests")] } : arm).ToArray()
        };
        var original = scenario.RunPreTester();
        if (control == "not-run") Assert.Contains(original.OriginalFocusedReceipt!.Arms!, arm =>
            arm.Arm == FindingEvidenceArm.Candidate && arm.NotRunSelections.Contains(new(Project, "BetaTests")));
        scenario.Invalidate(original, control);
        scenario.Report("BetaTests", "GammaTests", negativeControl: control == "negative-control");
        scenario.NextResult = request => scenario.Unsuccessful(request, "rejected");

        scenario.Tick(scenario.Driver());

        Assert.Equal(["BetaTests", "GammaTests"], Classes(scenario.Requests[1]));
        Assert.Equal(ReviewFindingState.Open, scenario.Finding.State);
        Assert.DoesNotContain(scenario.Verification.FindingEvidenceReceipts ?? [], receipt =>
            receipt.ReceiptId == original.FocusedEvidenceReceiptId);
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    [InlineData("changed-trx")]
    [InlineData("missing-result")]
    [InlineData("newer-rejected")]
    [InlineData("newer-incomplete")]
    public void InFlightInvalidationRetainsCAndSchedulesOnlyInvalidB(string invalidation)
    {
        using var scenario = new Scenario();
        var original = scenario.RunPreTester();
        scenario.Report("BetaTests", "GammaTests");
        var gate = new ConductorParallelAcceptanceAttemptCompletionGateForTests();
        var driver = scenario.Driver(gate);
        scenario.Tick(driver);
        var pending = gate.RequiredHandleForTests(scenario.Goal.Id.Value);
        Assert.Equal(["GammaTests"], Classes(pending.Attempt.FocusedEvidenceRequest!));
        if (invalidation.StartsWith("newer-", StringComparison.Ordinal)) scenario.RecordNewerBeta(invalidation[6..]);
        else scenario.Invalidate(original, invalidation);

        scenario.Tick(driver);
        Assert.Equal(1, gate.HeldCount);
        pending.CompleteForTests();
        scenario.Tick(driver);

        Assert.Equal(["GammaTests"], Classes(scenario.Requests[1]));
        Assert.Equal(ReviewFindingState.Open, scenario.Finding.State);
        Assert.False(FindingReceiptClosureDiagnosis.Evaluate(scenario.Goal, scenario.Tester, CandidateSha).IsClosable);
        Assert.Contains(scenario.Verification.FindingEvidenceReceipts!, receipt =>
            receipt.Request.Selections.Count == 1 && receipt.Request.Selections[0].TestClass == "GammaTests" && receipt.Passed);
        Assert.Equal("reused-pre-tester-invalidated:pending-execution", scenario.Finding.EvidenceOutcome!.DecisionReason);
        var retainedC = scenario.Verification.FindingEvidenceReceipts!.Single(receipt =>
            receipt.Request.Selections.Count == 1 && receipt.Request.Selections[0].TestClass == "GammaTests");
        var retainedCBytes = JsonSerializer.Serialize(retainedC);

        scenario.Tick(driver);
        var retry = gate.RequiredHandleForTests(scenario.Goal.Id.Value);
        Assert.Equal(["BetaTests"], Classes(retry.Attempt.FocusedEvidenceRequest!));
        scenario.Tick(driver);
        Assert.Equal(1, gate.HeldCount);
        Assert.Equal(2, scenario.Requests.Count);
        Assert.Equal(ReviewFindingState.Open, scenario.Finding.State);
        retry.CompleteForTests();
        scenario.Tick(driver);
        Assert.Equal(3, scenario.Requests.Count);
        Assert.Equal(["BetaTests"], Classes(scenario.Requests[2]));
        Assert.Equal(ReviewFindingState.Resolved, scenario.Finding.State);
        var repairedB = scenario.Verification.FindingEvidenceReceipts!.Single(receipt =>
            receipt.Request.Selections.Count == 1 && receipt.Request.Selections[0].TestClass == "BetaTests" && receipt.Passed);
        Assert.NotEqual(retainedC.ReceiptId, repairedB.ReceiptId);
        Assert.Equal(retainedCBytes, JsonSerializer.Serialize(scenario.Verification.FindingEvidenceReceipts!
            .Single(receipt => receipt.ReceiptId == retainedC.ReceiptId)));
        Assert.Equal(new[] { retainedC.ReceiptId, repairedB.ReceiptId }.Order(),
            scenario.Finding.EvidenceOutcome!.SourceReceiptIds!.Order());
        Assert.Contains(repairedB.RequestDispositions!, disposition =>
            disposition.RequestIdentity == ConductorDriver.BuildFindingEvidenceIdentity(
                new([new(Project, "BetaTests"), new(Project, "GammaTests")])));
        scenario.Tick(driver);
        Assert.Equal(3, scenario.Requests.Count);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void InvalidatedComposition_ChargesRetainedRunBeforeAdvancingToLaterRequest()
    {
        using var scenario = new Scenario();
        var original = scenario.RunPreTester();
        scenario.Report("BetaTests", "GammaTests", includeLaterRequest: true);
        var gate = new ConductorParallelAcceptanceAttemptCompletionGateForTests();
        var driver = scenario.Driver(gate);
        scenario.Tick(driver);
        var pending = gate.RequiredHandleForTests(scenario.Goal.Id.Value);
        Assert.Equal(["GammaTests"], Classes(pending.Attempt.FocusedEvidenceRequest!));
        scenario.Invalidate(original, "changed-trx");
        pending.CompleteForTests();
        scenario.Tick(driver);
        Assert.Equal("reused-pre-tester-invalidated:pending-execution", scenario.Finding.EvidenceOutcome!.DecisionReason);

        for (var failure = 0; failure < 2; failure++)
        {
            scenario.NextResult = request => new(request, false, false, "Source discovery unavailable", [],
                Rejection: new(FocusedEvidenceRejectionCode.SourceDiscoveryFailure, request, "Source discovery unavailable"));
            scenario.Tick(scenario.Driver());
            Assert.Equal(failure + 3, scenario.Requests.Count);
            Assert.Equal(["BetaTests"], Classes(scenario.Requests[^1]));
            scenario.Reload();
        }

        driver = scenario.Driver();
        scenario.Tick(driver);

        Assert.Equal(5, scenario.Requests.Count);
        Assert.Equal(["DeltaTests"], Classes(scenario.Requests[^1]));
        var identity = FindingRoundBatchCoordinator.Identity(driver, scenario.Goal, scenario.Tester)!;
        var observations = FindingRoundBatchAccounting.Read(scenario.Goal, identity);
        var unavailable = Assert.Single(observations.Where(item => item.Kind == "unavailable"));
        Assert.Equal(new[] { "mixed" }, unavailable.FindingIds);
        Assert.Contains("completed 3 runs", unavailable.Eligibility, StringComparison.Ordinal);
        Assert.DoesNotContain(scenario.Goal.Timeline, item => item.Kind == ProgressKind.TaskRetried);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void InFlightNewerRedRetainsCAndRoutesOriginalBetaFailureToDeveloper()
    {
        using var scenario = new Scenario();
        var original = scenario.RunPreTester();
        var originalBytes = File.ReadAllText(original.ResultPath);
        scenario.Report("BetaTests", "GammaTests");
        var gate = new ConductorParallelAcceptanceAttemptCompletionGateForTests();
        var driver = scenario.Driver(gate);
        scenario.Tick(driver);
        var pending = gate.RequiredHandleForTests(scenario.Goal.Id.Value);
        Assert.Equal(["GammaTests"], Classes(pending.Attempt.FocusedEvidenceRequest!));
        var red = scenario.RecordNewerBeta("red");
        var redBytes = JsonSerializer.Serialize(red);

        scenario.Tick(driver);
        Assert.Equal(1, gate.HeldCount);
        Assert.Equal(ReviewFindingState.Open, scenario.Finding.State);
        pending.CompleteForTests();
        scenario.Tick(driver);
        var retainedC = scenario.Verification.FindingEvidenceReceipts!.Single(receipt =>
            receipt.Request.Selections.Count == 1 && receipt.Request.Selections[0].TestClass == "GammaTests");
        Assert.True(retainedC.Passed);
        var retainedCBytes = JsonSerializer.Serialize(retainedC);
        Assert.Equal("reused-pre-tester-invalidated:pending-execution", scenario.Finding.EvidenceOutcome!.DecisionReason);
        var batch = FindingRoundBatchCoordinator.Identity(driver, scenario.Goal, scenario.Tester)!;

        scenario.Tick(driver);

        // An already executed RED follows the existing Developer route; invalid artifacts
        // and unusable results in the theory above still require a fresh B-only repair.
        Assert.Equal(0, gate.HeldCount);
        Assert.Equal(2, scenario.Requests.Count);
        Assert.Equal(["GammaTests"], Classes(scenario.Requests[1]));
        Assert.Equal(ReviewFindingState.Open, scenario.Finding.State);
        Assert.NotEqual(WorkTaskStatus.Completed, scenario.Tester.Status);
        Assert.False(FindingReceiptClosureDiagnosis.Evaluate(scenario.Goal, scenario.Tester, CandidateSha).IsClosable);
        Assert.Equal(redBytes, JsonSerializer.Serialize(scenario.Verification.FindingEvidenceReceipts!
            .Single(receipt => receipt.ReceiptId == red.ReceiptId)));
        Assert.Equal(retainedCBytes, JsonSerializer.Serialize(scenario.Verification.FindingEvidenceReceipts!
            .Single(receipt => receipt.ReceiptId == retainedC.ReceiptId)));
        Assert.Equal(originalBytes, File.ReadAllText(original.ResultPath));
        var developer = scenario.Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var retry = Assert.Single(scenario.Goal.Timeline.Where(item => item.Kind == ProgressKind.TaskRetried));
        Assert.Equal(developer.Id, retry.TaskId);
        Assert.Contains("ACTIONABLE_CANDIDATE_RED", retry.Message, StringComparison.Ordinal);
        Assert.Contains($"candidate_sha={CandidateSha};", retry.Message, StringComparison.Ordinal);
        Assert.Contains($"receipt_id={red.ReceiptId};", retry.Message, StringComparison.Ordinal);
        Assert.Contains("BetaTests.Fails", retry.Message, StringComparison.Ordinal);
        var observations = FindingRoundBatchAccounting.Read(scenario.Goal, batch);
        var covered = Assert.Single(observations.Where(item => item.Kind == "covered"));
        Assert.Equal(new[] { "mixed" }, covered.FindingIds);
        Assert.Equal(red.ReceiptId, covered.ReceiptId);
        Assert.DoesNotContain(observations, item => item.Kind == "executed");
        Assert.True(FindingRoundBatchAccounting.IsClosed(scenario.Goal, batch));
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    [InlineData("red")]
    [InlineData("rejected")]
    [InlineData("incomplete")]
    public void InFlightBetaVetoCannotRegainCoverageThroughSurvivingAlpha(string outcome)
    {
        using var scenario = new Scenario();
        var original = scenario.RunPreTester();
        var originalBytes = File.ReadAllText(original.ResultPath);
        Assert.Equal(["AlphaTests", "BetaTests"], original.OriginalFocusedReceipt!.Request.Selections
            .Select(selection => selection.TestClass));
        scenario.Report("AlphaTests", "BetaTests", third: "GammaTests");
        var gate = new ConductorParallelAcceptanceAttemptCompletionGateForTests();
        var driver = scenario.Driver(gate);
        scenario.Tick(driver);
        var pending = gate.RequiredHandleForTests(scenario.Goal.Id.Value);
        Assert.Equal(["GammaTests"], Classes(pending.Attempt.FocusedEvidenceRequest!));
        var veto = scenario.RecordNewerBeta(outcome);
        var vetoBytes = JsonSerializer.Serialize(veto);
        scenario.Tick(driver);
        Assert.Equal(1, gate.HeldCount);
        Assert.Single(scenario.Requests);
        Assert.Equal(ReviewFindingState.Open, scenario.Finding.State);

        pending.CompleteForTests();
        scenario.Tick(driver);

        Assert.Equal(2, scenario.Requests.Count);
        Assert.Equal(["GammaTests"], Classes(scenario.Requests[1]));
        Assert.Equal(ReviewFindingState.Open, scenario.Finding.State);
        Assert.NotEqual(WorkTaskStatus.Completed, scenario.Tester.Status);
        Assert.False(FindingReceiptClosureDiagnosis.Evaluate(scenario.Goal, scenario.Tester, CandidateSha).IsClosable);
        Assert.Equal("reused-pre-tester-invalidated:pending-execution", scenario.Finding.EvidenceOutcome!.DecisionReason);
        Assert.Equal(FindingEvidenceOutcomeReason.Unknown, scenario.Finding.EvidenceOutcome.ResultReason);
        var retainedC = scenario.Verification.FindingEvidenceReceipts!.Single(receipt =>
            receipt.Request.Selections.Count == 1 && receipt.Request.Selections[0].TestClass == "GammaTests");
        Assert.True(retainedC.Passed);
        Assert.Equal(JsonSerializer.Serialize(original.OriginalFocusedReceipt), JsonSerializer.Serialize(
            scenario.Verification.FindingEvidenceReceipts!.Single(receipt => receipt.ReceiptId == original.FocusedEvidenceReceiptId)));
        Assert.Equal(vetoBytes, JsonSerializer.Serialize(scenario.Verification.FindingEvidenceReceipts!
            .Single(receipt => receipt.ReceiptId == veto.ReceiptId)));
        Assert.Equal(originalBytes, File.ReadAllText(original.ResultPath));

        scenario.Tick(driver);
        Assert.Equal(ReviewFindingState.Open, scenario.Finding.State);
        Assert.Equal(2, scenario.Requests.Count);
        if (outcome == "red")
        {
            Assert.Equal(0, gate.HeldCount);
            var developer = scenario.Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            Assert.Contains(scenario.Goal.Timeline, item => item.Kind == ProgressKind.TaskRetried &&
                item.TaskId == developer.Id && item.Message.Contains("ACTIONABLE_CANDIDATE_RED", StringComparison.Ordinal) &&
                item.Message.Contains($"receipt_id={veto.ReceiptId};", StringComparison.Ordinal));
        }
        else
        {
            var repair = gate.RequiredHandleForTests(scenario.Goal.Id.Value);
            Assert.Equal(["BetaTests"], Classes(repair.Attempt.FocusedEvidenceRequest!));
            scenario.Tick(driver);
            Assert.Equal(1, gate.HeldCount);
            Assert.Equal(2, scenario.Requests.Count);
            repair.CompleteForTests();
            scenario.Tick(driver);
            Assert.Equal(3, scenario.Requests.Count);
            Assert.Equal(["BetaTests"], Classes(scenario.Requests[2]));
            Assert.Equal(ReviewFindingState.Resolved, scenario.Finding.State);
            Assert.Contains(original.FocusedEvidenceReceiptId!, scenario.Finding.EvidenceOutcome!.SourceReceiptIds!);
            Assert.Contains(retainedC.ReceiptId, scenario.Finding.EvidenceOutcome.SourceReceiptIds!);
        }
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    [InlineData("red")]
    [InlineData("rejected")]
    [InlineData("incomplete")]
    public void NewerNonGreenBetaDefeatsOlderPreTesterGreen(string outcome)
    {
        using var scenario = new Scenario();
        scenario.RunPreTester();
        scenario.Report("BetaTests", "GammaTests");
        scenario.RecordNewerBeta(outcome);
        scenario.NextResult = request => scenario.Unsuccessful(request, "rejected");
        scenario.Tick(scenario.Driver());
        Assert.Equal(["BetaTests", "GammaTests"], Classes(scenario.Requests[1]));
        Assert.Equal(ReviewFindingState.Open, scenario.Finding.State);
    }

    private static string[] Classes(string request) => request.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(selection => selection.Split(':', 2)[1].Trim()).ToArray();

    private sealed class Scenario : IDisposable
    {
        public string Root { get; } = ConductorDriverTests.CreateTempDirectory();
        public AgentOrchestratorKernel Kernel { get; private set; } = new();
        public Goal Goal { get; private set; }
        public TaskSpec Tester => Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        public List<string> Requests { get; } = [];
        public Func<string, FocusedEvidenceRunResult>? NextResult { get; set; }
        public bool RecordBatchAccounting { get; set; } = true;
        public string Candidate { get; set; } = CandidateSha;
        public AcceptanceGateEngineSettings Settings { get; set; } = new();
        public TaskVerificationRecord Verification => Tester.VerificationHistory.Last();
        public ReviewFinding Finding => Verification.MergedReviewFindings!.Single(finding => finding.StableId == "mixed");
        private string Attempts => Path.Combine(Root, "attempts");
        private int _tick;

        public Scenario()
        {
            Goal = Kernel.CreateGoal("Verify the scoped change", [new TaskSpec(TaskId.New(), "Plan", AgentRole.Planner),
                new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer), new TaskSpec(TaskId.New(), "Test", AgentRole.Tester),
                new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer)]);
            Kernel.ActivateGoal(Goal.Id, DefaultAgents());
            PassVerification(Kernel, Goal, Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Planner));
            var developer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            DispatchTask(Kernel, Goal, developer, workingDirectory: Root, baseCommit: "aaa1111");
            Kernel.RecordTaskVerification(Goal.Id, developer.Id, new TaskVerificationRecord("test.exe", Root, 0,
                string.Join(Environment.NewLine, "WORKER_RESULT:", "files: tests/AlphaTests.cs", "commands: none",
                    "tests: deferred - AlphaTests, BetaTests", "commit: none", "blockers: none", "assigned_scope_complete: true",
                    "model_fit: test/model - adequate - fixture", "skills: none", "confidence: high", "END_WORKER_RESULT"), "",
                DateTimeOffset.UtcNow, WorkerResultPresent: true, HasCommittedChanges: true));
            Kernel.RecordDispatchResultCommit(Goal.Id, developer.Id, CandidateSha);
            var worktree = GoalWorktrees.WorktreePath(Root, Goal.Id);
            Directory.CreateDirectory(worktree);
            File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: fixture");
            var project = Path.Combine(worktree, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            foreach (var name in new[] { "AlphaTests", "BetaTests", "GammaTests" })
                File.WriteAllText(Path.Combine(project, name + ".cs"), "public class " + name + " { [Xunit.Fact] public void Runs() {} }");
        }

        public FocusedEvidenceRunResult Green(string request) =>
            ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(Root, request, Candidate) with
            { OutcomeReason = FindingEvidenceOutcomeReason.ValidEvidence };

        public FocusedEvidenceRunResult Unsuccessful(string request, string outcome)
        {
            var green = Green(request);
            return outcome switch
            {
                "red" => CandidateRedFindingEvidence(request, Candidate, Classes(request).Single() + ".Fails"),
                "rejected" => new FocusedEvidenceRunResult(request, false, false, "rejected", []),
                "incomplete" => green with { Passed = false },
                "not-run" => green with { Arms = green.Arms!.Select(arm => arm.Arm == FindingEvidenceArm.Candidate
                    ? arm with { NotRunSelections = [new(Project, "GammaTests")] } : arm).ToArray() },
                _ => throw new ArgumentException(outcome)
            };
        }

        public ConductorParallelAcceptanceAttempt RunPreTester()
        {
            Tick(Driver());
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(Attempts, TrunkBranchName.Default, runInline: true,
                acquireStableSlotLease: (_, _) => null);
            var attempt = Assert.Single(coordinator.ReadFocusedCoverageAttempts(Goal.Id));
            Assert.NotNull(attempt.OriginalFocusedReceipt);
            Assert.Equal("green", PreTesterEvidenceIndexLines.Latest(Goal, Tester.Id, Candidate)!.Outcome);
            return attempt;
        }

        public void Report(string first, string second, bool negativeControl = false, string? third = null,
            bool includeLaterRequest = false)
        {
            var finding = EvidenceFindingWithRequest("Need combined evidence", id: "mixed", category: FindingCategory.TestEvidence,
                classes: third is null ? [first, second] : [first, second, third]);
            if (negativeControl) finding = finding with { EvidenceRequest = finding.EvidenceRequest! with
                { NegativeControl = FindingEvidenceNegativeControl.RevertSrc } };
            var findings = includeLaterRequest
                ? new[] { finding, EvidenceFindingWithRequest("Need separate evidence", id: "later",
                    category: FindingCategory.TestEvidence, project: "Core.Tests", classes: ["DeltaTests"]) }
                : [finding];
            DispatchTask(Kernel, Goal, Tester, "test", workingDirectory: Root, baseCommit: Candidate);
            Assert.Equal(Root, Tester.LastDispatch!.WorkingDirectory);
            var output = string.Join(Environment.NewLine, "WORKER_RESULT:", "files: none", "commands: inspect evidence",
                "tests: deferred - acceptance owns execution", "commit: none", "blockers: none",
                $"findings: {JsonSerializer.Serialize(findings)}", "touched_anchors: []", "verdict: needs-work",
                "model_fit: test/model - adequate - fixture", "skills: none", "confidence: high", "END_WORKER_RESULT");
            Kernel.RecordDispatchExecutionResult(Goal.Id, Tester.Id, new TaskVerificationRecord("test", Root, 0, output, "",
                DateTimeOffset.UtcNow, StandardOutputPath: Path.Combine(Root, "worker.out.log"), WorkerResultPresent: true, ReviewedCommit: Candidate));
        }

        public FindingEvidenceReceipt RecordNewerBeta(string outcome)
        {
            var request = new FindingEvidenceRequest([new(Project, "BetaTests")]);
            var evidence = Unsuccessful(Project + ":BetaTests", outcome);
            Assert.True(WorkerResultBlockers.TryFindReviewFindingRound(Verification, out var round, out _));
            var fingerprint = ConductorDriver.BuildFindingRoundFingerprint(Tester, round);
            var receipt = new FindingEvidenceReceipt("newer-beta-" + outcome, Candidate, request, evidence.Accepted, evidence.IsValidEvidence,
                evidence.Summary, evidence.Arms?.Select(ConductorDriver.CreateFindingEvidenceArmReceipt).ToArray(),
                [new("mixed", ConductorDriver.BuildFindingEvidenceIdentity(request), "executed-standalone", "newer-beta",
                    FindingEvidenceRequestDecision.Executed, Candidate, fingerprint, SourceReceiptIds: ["newer-beta-" + outcome])],
                fingerprint, ConductorDriver.BuildFindingEvidenceExecutionBasisIdentity(Settings));
            Kernel.RecordFindingEvidenceOutcome(Goal.Id, Tester.Id, "mixed",
                new(true, receipt.ReceiptId, ResultReason: FindingEvidenceOutcomeReason.Unknown), receipt);
            return receipt;
        }

        public void Invalidate(ConductorParallelAcceptanceAttempt attempt, string control)
        {
            var trx = attempt.OriginalFocusedReceipt!.Arms!.Single(arm => arm.Arm == FindingEvidenceArm.Candidate).TestResultPaths!.Single();
            switch (control)
            {
                case "candidate": Candidate = "ccc3333"; break;
                case "basis": Settings = new AcceptanceGateEngineSettings { EnforceStructuralCoverage = true }; break;
                case "missing-trx": File.Delete(trx); break;
                case "changed-trx": File.AppendAllText(trx, "<!-- changed but still green -->"); break;
                case "missing-result": File.Delete(attempt.ResultPath); break;
                case "changed-result":
                    var result = JsonNode.Parse(File.ReadAllText(attempt.ResultPath))!;
                    result["focusedEvidence"]!["summary"] = "changed green result";
                    File.WriteAllText(attempt.ResultPath, result.ToJsonString()); break;
                case "legacy":
                    var metadata = JsonNode.Parse(File.ReadAllText(attempt.MetadataPath))!;
                    metadata.AsObject().Remove("originalFocusedReceipt");
                    metadata.AsObject().Remove("focusedExecutionBasisIdentity");
                    File.WriteAllText(attempt.MetadataPath, metadata.ToJsonString()); break;
            }
        }

        public ConductorDriver Driver(ConductorParallelAcceptanceAttemptCompletionGateForTests? gate = null)
        {
            var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => NoPreReviewContext(Candidate), getLandingFileScopes: _ => [],
                getFindingEvidenceEngineSettings: _ => Settings,
                focusedEvidenceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(Attempts, TrunkBranchName.Default,
                    runInline: gate is null, attemptCompletionGateForTests: gate, acquireStableSlotLease: (_, _) => null),
                runFocusedEvidence: (_, request) =>
                {
                    Requests.Add(request);
                    var result = NextResult?.Invoke(request) ?? Green(request);
                    NextResult = null;
                    return result;
                }, dispatchAndStart: _ => DispatchStartOutcome.Started(),
                retryTaskWithCause: (goal, task, message, round, cause) => Kernel.RetryTask(goal, task, message, retryRoundKind: round, retryCause: cause),
                recordFindingEvidenceRequest: (goal, task, message) =>
                {
                    if (RecordBatchAccounting || !message.StartsWith("finding-evidence batch-", StringComparison.Ordinal))
                        Kernel.RecordFindingEvidenceRequest(goal, task, message);
                },
                recordFindingEvidenceRun: (goal, task, message) => Kernel.RecordFindingEvidenceRun(goal, task, message),
                recordFindingEvidenceRequestDispositions: (goal, task, dispositions) => Kernel.RecordFindingEvidenceRequestDispositions(goal, task, dispositions),
                recordFindingEvidenceOutcome: (goal, task, id, outcome, receipt) => Kernel.RecordFindingEvidenceOutcome(goal, task, id, outcome, receipt), executionDirectory: Root);
            driver.OverrideDeveloperCompletionStructuralPreflightForTests(_ => new DeveloperCompletionStructuralFindings(false, "clean"), TimeSpan.FromSeconds(5));
            return driver;
        }

        public void Tick(ConductorDriver driver)
        {
            driver.BeginTick(Kernel, ++_tick);
            driver.AdvanceOnce(Goal, ConductorAutonomyPolicy.Permissive);
        }

        public void Reload()
        {
            Kernel = AgentOrchestratorKernel.FromSnapshot(Kernel.ExportSnapshot());
            Goal = Kernel.GetGoal(Goal.Id);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
