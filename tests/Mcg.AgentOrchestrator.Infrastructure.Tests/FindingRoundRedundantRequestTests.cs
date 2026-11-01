using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: private kernel, fake runner/dispatch, and a unique temporary TRX/attempt root.
public sealed class FindingRoundRedundantRequestTests
{
    private const string ShaA = "abc1234";
    private const string ShaB = "def5678";
    private const string First = "Fixture.AlphaTests.First";
    private const string Second = "Fixture.AlphaTests.Second";

    [Fact]
    public void RedSuperset_SkipsTwoRequestsAndRoutesWithCoveringReceipt()
    {
        using var scenario = new Scenario(["AlphaTests", "AlphaTests.First", "AlphaTests.Second"],
            (_, _) => [new(First), new(Second)]);

        var result = scenario.Advance();

        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Single(scenario.Runs);
        var receipt = Assert.Single(scenario.Receipts);
        var retry = Assert.Single(scenario.Retries);
        Assert.Equal(scenario.Reviewer.Id, retry.Task);
        Assert.Contains(First, retry.Message, StringComparison.Ordinal);
        Assert.Contains(Second, retry.Message, StringComparison.Ordinal);
        Assert.Contains(receipt.ReceiptId, retry.Message, StringComparison.Ordinal);
        Assert.Equal(2, scenario.Skips.Length);
        Assert.All(scenario.Skips, item =>
        {
            Assert.Contains($"candidate_sha={ShaA};", item.Message, StringComparison.Ordinal);
            Assert.Contains($"source_receipt_ids={receipt.ReceiptId};", item.Message, StringComparison.Ordinal);
        });
        var verification = Assert.Single(scenario.Reviewer.VerificationHistory.Where(record =>
            (record.FindingEvidenceReceipts ?? []).Any(item => item.ReceiptId == receipt.ReceiptId)));
        var dispositions = verification.FindingEvidenceRequestDispositions!
            .Where(item => item.Reason == FindingRoundRedCoverage.Reason).ToArray();
        Assert.Equal(2, dispositions.Length);
        Assert.All(dispositions, item =>
        {
            Assert.Equal(FindingEvidenceRequestDecision.CoveredByReceipt, item.Decision);
            Assert.Equal(new[] { receipt.ReceiptId }, item.SourceReceiptIds);
        });
        Assert.Empty(scenario.Escalations);
    }

    [Fact]
    public void UnexecutedSelection_RunsAfterRestart()
    {
        using var scenario = new Scenario(["AlphaTests", "AlphaTests.Unseen", "AlphaTests.Second"],
            (run, _) => run == 0 ? [new(First), new(Second)] : [new("Fixture.AlphaTests.Unseen")]);

        Assert.IsType<ConductorAdvanceOutcome.Held>(scenario.Advance().Outcome);
        Assert.Single(scenario.Runs);
        Assert.Single(scenario.Skips);
        scenario.RestartDriver();
        scenario.Advance();

        Assert.Equal(2, scenario.Runs.Count);
        Assert.Contains("AlphaTests.Unseen", scenario.Runs[1].Request, StringComparison.Ordinal);
        Assert.Single(scenario.Retries);
        Assert.Single(scenario.Skips); // Durable marker prevents a duplicate record on re-entry.
        Assert.Empty(scenario.Escalations);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SharedTestsPassed_LaterSelectionsRun(bool redOverall)
    {
        using var scenario = new Scenario(["AlphaTests", "AlphaTests.First", "AlphaTests.Second"],
            (run, _) => run == 0
                ? redOverall ? [new("Fixture.AlphaTests.Other"), new(First, "Passed"), new(Second, "Passed")]
                    : [new(First, "Passed"), new(Second, "Passed")]
                : run == 1 ? [new(First)] : [new(Second)]);

        for (var tick = 0; tick < 3; tick++) scenario.Advance();

        Assert.Equal(3, scenario.Runs.Count);
        Assert.Contains("AlphaTests.First", scenario.Runs[1].Request, StringComparison.Ordinal);
        Assert.Contains("AlphaTests.Second", scenario.Runs[2].Request, StringComparison.Ordinal);
        Assert.Empty(scenario.Skips);
        Assert.Single(scenario.Retries);
        Assert.Empty(scenario.Escalations);
    }

    [Fact]
    public void CandidateChanges_PreviouslyCoveredSelectionRunsAgain()
    {
        using var scenario = new Scenario(["AlphaTests", "BetaTests.Unseen", "AlphaTests.Second"],
            (run, request) => run == 0 ? [new(First), new(Second)]
                : request.Contains("BetaTests", StringComparison.Ordinal) ? [new("Fixture.BetaTests.Unseen")]
                : request.Contains("AlphaTests.Second", StringComparison.Ordinal) ? [new(Second)] : [new(First)]);
        scenario.Advance();
        Assert.Single(scenario.Skips);
        scenario.Candidate = ShaB;
        scenario.RestartDriver();

        for (var tick = 0; tick < 4 && scenario.Retries.Count == 0; tick++) scenario.Advance();

        Assert.Contains(scenario.Runs, run => run.Sha == ShaB &&
            run.Request.Contains("AlphaTests.Second", StringComparison.Ordinal));
        Assert.All(scenario.Skips, item => Assert.Contains($"candidate_sha={ShaA};", item.Message, StringComparison.Ordinal));
        Assert.Single(scenario.Retries);
        Assert.Empty(scenario.Escalations);
    }

    [Fact]
    public void ActionableRed_RoutesImmediatelyWithoutRedundantRecords()
    {
        using var scenario = new Scenario(["AlphaTests", "AlphaTests.First", "AlphaTests.Second"],
            (_, _) => [new(First), new(Second)], actionable: true);

        scenario.Advance();

        Assert.Single(scenario.Runs);
        var retry = Assert.Single(scenario.Retries);
        Assert.Equal(scenario.Developer.Id, retry.Task);
        Assert.StartsWith("ACTIONABLE_CANDIDATE_RED", retry.Message, StringComparison.Ordinal);
        Assert.Empty(scenario.Skips);
        Assert.Equal(2, scenario.Goal.Timeline.Count(item =>
            item.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
            item.Message.Contains("reason=superseded-by-actionable-red;", StringComparison.Ordinal)));
        Assert.Empty(scenario.Escalations);
    }

    [Fact]
    public void MultipleReceipts_CoverUnionAndRetainEverySource()
    {
        using var scenario = new Scenario(["AlphaTests.First", "AlphaTests.Second", "BetaTests.Unseen"],
            (run, _) => run == 0 ? [new(First)] : [new(Second)]);
        scenario.Advance();
        scenario.Advance();
        var receipts = scenario.Receipts;
        Assert.Equal(2, receipts.Length);
        var pending = Batch("AlphaTests.First", "AlphaTests.Second");

        var coverage = scenario.Evaluate(pending);

        Assert.Empty(coverage.Remaining);
        Assert.Equal(receipts.Select(item => item.ReceiptId).Order(),
            Assert.Single(coverage.Skipped).Receipts.Select(item => item.ReceiptId).Order());
        Assert.Contains(First, coverage.Evidence, StringComparison.Ordinal);
        Assert.Contains(Second, coverage.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void EarlierPass_DoesNotEraseLaterFailureForTheSameTest()
    {
        using var scenario = new Scenario(["AlphaTests", "AlphaTests.First", "BetaTests.Unseen"],
            (run, _) => run == 0 ? [new(Second), new(First, "Passed")] : [new(First)]);
        scenario.Advance();
        scenario.Advance();
        var receipts = scenario.Receipts;
        Assert.Equal(2, receipts.Length);

        var coverage = scenario.Evaluate(Batch("AlphaTests.First"));

        Assert.Empty(coverage.Remaining);
        Assert.Equal(new[] { receipts[1].ReceiptId },
            Assert.Single(coverage.Skipped).Receipts.Select(item => item.ReceiptId));
    }

    [Fact]
    public void ParameterizedSelection_CombinesFailuresWithoutUsingPassingCasesAsCoverage()
    {
        const string caseOne = "Fixture.AlphaTests.Case(value: 1)";
        const string caseTwo = "Fixture.AlphaTests.Case(value: 2)";
        using var scenario = new Scenario(["AlphaTests", "AlphaTests.Case", "BetaTests.Unseen"],
            (run, _) => run == 0 ? [new(caseOne), new(caseTwo, "Passed")]
                : [new(caseOne, "Passed"), new(caseTwo)]);
        scenario.Advance();
        Assert.Single(scenario.Evaluate(Batch("AlphaTests.Case")).Remaining);
        scenario.Advance();
        var receipts = scenario.Receipts;
        Assert.Equal(2, receipts.Length);

        var coverage = scenario.Evaluate(Batch("AlphaTests.Case"));

        Assert.Empty(coverage.Remaining);
        Assert.Equal(receipts.Select(item => item.ReceiptId).Order(),
            Assert.Single(coverage.Skipped).Receipts.Select(item => item.ReceiptId).Order());
    }

    [Fact]
    public void ChangedCandidateThenReturned_DiscardsEarlierCoverage()
    {
        using var scenario = new Scenario(["AlphaTests", "BetaTests.Unseen"], (_, _) => [new(First)]);
        scenario.Advance();
        Assert.Single(scenario.Evaluate(Batch("AlphaTests.First")).Skipped);
        scenario.RecordCandidate(ShaB);
        scenario.RecordCandidate(ShaA);

        var coverage = scenario.Evaluate(Batch("AlphaTests.First"));

        Assert.Empty(coverage.Skipped);
        Assert.Single(coverage.Remaining);
        Assert.Empty(coverage.RedReceipts);
    }

    [Fact]
    public void DifferentRound_DoesNotReuseCoverage()
    {
        using var scenario = new Scenario(["AlphaTests", "BetaTests.Unseen"], (_, _) => [new(First)]);
        scenario.Advance();

        var coverage = scenario.Evaluate(Batch("AlphaTests.First"), fingerprint: "another-round");

        Assert.Empty(coverage.Skipped);
        Assert.Single(coverage.Remaining);
    }

    [Theory]
    [InlineData("FullyQualifiedName~AlphaTests&Category!=Slow")]
    [InlineData("BetaTests.Unseen")]
    [InlineData("AlphaTests")]
    public void UnknownOrIncompleteSelection_DoesNotGrantCoverage(string selector)
    {
        using var scenario = new Scenario(["AlphaTests.First", "BetaTests.Unseen"], (_, _) => [new(First)]);
        scenario.Advance();

        var coverage = scenario.Evaluate(Batch(selector));

        Assert.Empty(coverage.Skipped);
        Assert.Single(coverage.Remaining);
    }

    [Fact]
    public void ModifiedTrx_DoesNotGrantCoverage()
    {
        using var scenario = new Scenario(["AlphaTests", "BetaTests.Unseen"], (_, _) => [new(First)]);
        scenario.Advance();
        Assert.Single(scenario.Evaluate(Batch("AlphaTests.First")).Skipped);
        var path = Assert.Single(Assert.Single(scenario.Receipts).Arms!
            .Single(arm => arm.Arm == FindingEvidenceArm.Candidate).TestResultPaths!);
        File.AppendAllText(path, " ");

        var coverage = scenario.Evaluate(Batch("AlphaTests.First"));

        Assert.Empty(coverage.Skipped);
        Assert.Single(coverage.Remaining);
    }

    [Theory]
    [InlineData("Failed", true)]
    [InlineData("Passed", false)]
    public void ParameterizedSelection_RequiresEveryCaseToBeRed(string outcome, bool covered)
    {
        using var scenario = new Scenario(["AlphaTests", "BetaTests.Unseen"],
            (_, _) => [new("Fixture.AlphaTests.Case(value: 1)"), new("Fixture.AlphaTests.Case(value: 2)", outcome)]);
        scenario.Advance();

        var coverage = scenario.Evaluate(Batch("AlphaTests.Case"));

        Assert.Equal(covered ? 1 : 0, coverage.Skipped.Count);
        Assert.Equal(covered ? 0 : 1, coverage.Remaining.Count);
    }

    [Fact]
    public void SelectorInTheoryArgument_DoesNotProveExecution()
    {
        using var scenario = new Scenario(["AlphaTests", "BetaTests.Unseen"],
            (_, _) => [new("Fixture.AlphaTests.Other(value: AlphaTests.Case)")]);
        scenario.Advance();

        var coverage = scenario.Evaluate(Batch("AlphaTests.Case"));

        Assert.Empty(coverage.Skipped);
        Assert.Single(coverage.Remaining);
    }

    private static FindingEvidenceBatch Batch(params string[] filters)
    {
        var finding = EvidenceFindingWithRequest("Pending", "pending", category: FindingCategory.SpecDefect, classes: filters);
        var request = finding.EvidenceRequest!;
        var identity = ConductorDriver.BuildFindingEvidenceIdentity(request);
        var group = new FindingEvidenceRequestGroup(identity, "pending", request, [finding]);
        return new(identity, "pending", request, [finding], [group]);
    }

    private sealed record Row(string Identity, string Outcome = "Failed");

    private sealed class Scenario : IDisposable
    {
        private readonly AgentOrchestratorKernel _kernel;
        private readonly string _root = ConductorDriverTests.CreateTempDirectory();
        private readonly Func<int, string, Row[]> _results;
        private ConductorDriver _driver = null!;

        internal Scenario(string[] filters, Func<int, string, Row[]> results, bool actionable = false)
        {
            _results = results;
            (_kernel, Goal) = SoftwareGoal();
            Developer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            Reviewer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in Goal.Tasks.Where(task => task != Reviewer))
                PassVerification(_kernel, Goal, task, hasCommittedChanges: task == Developer);
            var findings = filters.Select((filter, index) => EvidenceFindingWithRequest("Needs evidence", $"request-{index}",
                category: actionable ? FindingCategory.TestEvidence : FindingCategory.SpecDefect, classes: [filter])).ToArray();
            FailReviewerNeedsWork(_kernel, Goal, Reviewer, "Pending evidence", findings: findings);
            RestartDriver();
        }

        internal Goal Goal { get; }
        internal TaskSpec Developer { get; }
        internal TaskSpec Reviewer { get; }
        internal string Candidate { get; set; } = ShaA;
        internal List<(string Sha, string Request)> Runs { get; } = [];
        internal List<(TaskId Task, string Message)> Retries { get; } = [];
        internal List<string> Escalations { get; } = [];
        internal ProgressEvent[] Skips => Goal.Timeline.Where(item => item.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
            item.Message.Contains("reason=redundant-candidate-red;", StringComparison.Ordinal)).ToArray();
        internal FindingEvidenceReceipt[] Receipts => Reviewer.VerificationHistory
            .SelectMany(record => record.FindingEvidenceReceipts ?? []).DistinctBy(receipt => receipt.ReceiptId).ToArray();

        internal ConductorAdvanceResult Advance() => _driver.AdvanceOnce(Goal, ConductorAutonomyPolicy.Permissive);
        internal void RecordCandidate(string sha) => _kernel.RecordFindingEvidenceRequest(Goal.Id, Reviewer.Id, $"candidate_sha={sha};");
        internal FindingRoundRedCoverage.Coverage Evaluate(FindingEvidenceBatch pending, string? fingerprint = null)
        {
            var receipt = Receipts[0];
            return FindingRoundRedCoverage.Evaluate([pending], Reviewer, Goal.Timeline, Candidate,
                fingerprint ?? receipt.FindingRoundFingerprint!, receipt.ExecutionBasisIdentity!);
        }

        internal void RestartDriver() => _driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(Candidate),
            focusedEvidenceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                _root, TrunkBranchName.Default, runInline: true, acquireStableSlotLease: (_, _) => null),
            runFocusedEvidence: (_, request) => Run(request),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                Retries.Add((taskId, message));
                return _kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            writeEscalation: (_, _, message) => Escalations.Add(message),
            recordFindingEvidenceRequest: (goalId, taskId, message) => _kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceRun: (goalId, taskId, message) => _kernel.RecordFindingEvidenceRun(goalId, taskId, message),
            recordFindingEvidenceRequestDispositions: (goalId, taskId, dispositions) =>
                _kernel.RecordFindingEvidenceRequestDispositions(goalId, taskId, dispositions),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                _kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        private FocusedEvidenceRunResult Run(string request)
        {
            var rows = _results(Runs.Count, request);
            Runs.Add((Candidate, request));
            var path = Path.Combine(_root, $"run-{Runs.Count}.trx");
            WriteTrx(path, rows);
            var failures = rows.Where(row => row.Outcome == "Failed").Select(row => row.Identity).ToArray();
            var passed = failures.Length == 0;
            var check = new AcceptanceCheckResult("candidate", passed, passed ? 0 : 1, "Synthetic execution",
                TestResultPaths: [path], FailingTestIdentities: failures, ExecutedTestCount: rows.Length);
            return new(request, true, passed, "Synthetic execution", [check], Arms:
            [
                new(FindingEvidenceArm.Candidate, Candidate, passed ? FindingEvidenceArmDisposition.Green : FindingEvidenceArmDisposition.Red,
                    true, passed, "candidate", [check]),
                new(FindingEvidenceArm.Baseline, "1234567", FindingEvidenceArmDisposition.Green, true, true, "baseline", [])
            ], OutcomeReason: passed ? FindingEvidenceOutcomeReason.ValidEvidence : FindingEvidenceOutcomeReason.CandidateRed);
        }

        private static void WriteTrx(string path, Row[] rows)
        {
            XNamespace trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            new XDocument(new XElement(trx + "TestRun",
                new XElement(trx + "TestDefinitions", rows.Select((row, index) =>
                    new XElement(trx + "UnitTest", new XAttribute("id", index),
                        new XElement(trx + "TestMethod", new XAttribute("className", Method(row)[..Method(row).LastIndexOf('.')]),
                            new XAttribute("name", Method(row)[(Method(row).LastIndexOf('.') + 1)..]))))),
                new XElement(trx + "Results", rows.Select((row, index) => new XElement(trx + "UnitTestResult",
                    new XAttribute("testId", index), new XAttribute("testName", row.Identity), new XAttribute("outcome", row.Outcome)))),
                new XElement(trx + "ResultSummary", new XElement(trx + "Counters", new XAttribute("total", rows.Length),
                    new XAttribute("executed", rows.Length), new XAttribute("passed", rows.Count(row => row.Outcome == "Passed")),
                    new XAttribute("failed", rows.Count(row => row.Outcome == "Failed")))))).Save(path);
        }

        private static string Method(Row row) => AcceptanceTrxTestIdentityResolver.NormalizeSelector(row.Identity);

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
