using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit.Sdk;
using static ConductorDriverTests;
using static Mcg.AgentOrchestrator.Core.FindingEvidenceRequestReceiptDecision;
using static Mcg.AgentOrchestrator.Core.FindingEvidenceRequestReceiptHold;

// Parallel-safe: each case owns its kernel, attempt directory and state repository.
public sealed class FindingEvidenceRequestReceiptRoutingTests
{
    private const string Candidate = "abc1234";
    private const string Round = "receipt-round";
    private const string OriginalId = "original-evidence";
    private const string RerunId = "rerun-evidence";
    private const string Guard =
        "candidate_sha=abc1234; finding_round=receipt-round; request_identity=finding-request-298a1cb21e03eb7e; execution_basis=none";
    private const string BaselineMessage = $"finding-evidence disposition=baseline-arm-absent; {Guard}; receipt_id={OriginalId}";
    private const string RequestedMessage = $"finding-evidence disposition=candidate-rerun-requested; {Guard}; original_receipt_id={OriginalId}";
    private const string GreenMessage = $"finding-evidence disposition=candidate-rerun-green; {Guard}; receipt_id={RerunId}; original_receipt_id={OriginalId}";

    [Theory]
    [InlineData("reworded")]
    [InlineData("injected-request")]
    [InlineData("injected-terminal")]
    public void DisplayText_DoesNotChangeRoutingOrSuppressRequests(string scenario) =>
        AssertDisplayContract(scenario, fixture => fixture.ResumeAndCountRequests());

    [Theory]
    [InlineData("reworded")]
    [InlineData("injected-request")]
    [InlineData("injected-terminal")]
    public void ProseInferenceCopy_FailsTheDisplayContract(string scenario) =>
        Assert.Throws<EqualException>(() => AssertDisplayContract(scenario, RouteUsingOldProseInference));

    [Theory]
    [InlineData(CandidateRerunGreen, FailedGoalFindingObservationKind.FindingEvidenceDeliveryRecorded, "receipt_ids=rerun-evidence")]
    [InlineData(CandidateRerunRed, FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired, "candidate_receipt_id=original-evidence")]
    [InlineData(CandidateRerunUnusable, FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired, "candidate_receipt_id=original-evidence")]
    public void TerminalReceipts_UseLatestDecisionAndEvidence(
        FindingEvidenceRequestReceiptDecision terminal, FailedGoalFindingObservationKind expected, string evidence)
    {
        using var fixture = new RoutingFixture();
        fixture.WriteTyped(fixture.Receipt(BaselineArmAbsent, OriginalId), "Baseline check was unavailable.");
        fixture.WriteTyped(fixture.Receipt(terminal == CandidateRerunGreen ? CandidateRerunRed : CandidateRerunGreen, "older"),
            "An earlier rerun completed.");
        fixture.WriteTyped(fixture.Receipt(terminal, RerunId), "The latest rerun completed.");

        Assert.True(fixture.Resume(fixture.CreateDriver(), out var decision));

        Assert.Equal(expected, decision.Kind);
        Assert.Contains(evidence, decision.Evidence, StringComparison.Ordinal);
        Assert.Equal(0, fixture.FocusedRuns);
        Assert.DoesNotContain(fixture.Receipts, receipt => receipt.Decision == CandidateRerunRequested);
    }

    [Theory]
    [InlineData("task")]
    [InlineData("sha")]
    [InlineData("round")]
    [InlineData("request-guard")]
    [InlineData("rerun-guard")]
    public void ForeignReceipts_IgnorePayloadAndItsHistoricalLookingText(string field)
    {
        using var fixture = new RoutingFixture();
        fixture.WriteTyped(fixture.Receipt(BaselineArmAbsent, OriginalId), BaselineMessage);
        FindingEvidenceRequestReceipt ChangeIdentity(FindingEvidenceRequestReceipt receipt) => field switch
        {
            "task" => receipt with { RequestingTaskId = "different-task" },
            "sha" => receipt with { CandidateSha = "def5678" },
            "round" => receipt with { FindingRoundFingerprint = "different-round" },
            "request-guard" => receipt with { RequestGuard = "different-request" },
            "rerun-guard" => receipt with { RerunGuard = "different-rerun" },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        fixture.WriteTyped(ChangeIdentity(fixture.Receipt(CandidateRerunRequested)), RequestedMessage);
        fixture.WriteTyped(ChangeIdentity(fixture.Receipt(CandidateRerunGreen, RerunId)), GreenMessage);

        var result = fixture.ResumeAndCountRequests();

        Assert.Equal((true, FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired, 1), result);
        Assert.Equal(1, fixture.FocusedRuns);
    }

    [Fact]
    public void CommittedRequest_ReconcilesWithoutWritingAnotherRequest()
    {
        using var fixture = new RoutingFixture();
        fixture.WriteTyped(fixture.Receipt(BaselineArmAbsent, OriginalId), "Baseline unavailable.");
        fixture.WriteTyped(fixture.Receipt(CandidateRerunRequested), "Queued for focused testing.");

        var result = fixture.ResumeAndCountRequests();

        Assert.Equal((true, FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired, 0), result);
        Assert.Equal(1, fixture.FocusedRuns);
        Assert.Single(fixture.Receipts, receipt => receipt.Decision == CandidateRerunRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedTerminal_BeforeInboxAcknowledgement_ReplaysWithoutAnotherRun(bool rerunFault)
    {
        using var uninterrupted = new RoutingFixture(rerunFault);
        var expected = uninterrupted.ResolveMissingBaseline(uninterrupted.CreateDriver());
        Assert.Equal(FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired, expected.Kind);

        using var interrupted = new RoutingFixture(rerunFault);
        var terminal = rerunFault ? CandidateRerunUnusable : CandidateRerunRed;
        var statePath = Path.Combine(interrupted.Root, "state.db");
        var repository = new SqliteOrchestratorStateRepository(statePath);
        var driver = interrupted.CreateDriver(receipt =>
        {
            if (receipt.Decision == terminal)
                CommitBeforeAcknowledgement(repository, interrupted.Kernel);
        });

        Assert.Equal("Interrupted after state commit, before inbox acknowledgement.",
            Assert.Throws<IOException>(() => interrupted.ResolveMissingBaseline(driver)).Message);
        Assert.Equal(2, interrupted.FocusedRuns);

        interrupted.Reload(await new SqliteOrchestratorStateRepository(statePath).LoadAsync());
        Assert.Collection(interrupted.Receipts,
            baseline =>
            {
                Assert.Equal(BaselineArmAbsent, baseline.Decision);
                Assert.Equal(UnconfirmedRedRouting, baseline.Hold);
                Assert.NotEmpty(baseline.ReceiptId!);
            },
            requested =>
            {
                Assert.Equal(CandidateRerunRequested, requested.Decision);
                Assert.Null(requested.Hold);
                Assert.Null(requested.ReceiptId);
                Assert.Equal(interrupted.Receipts[0].ReceiptId, requested.OriginalReceiptId);
            },
            recorded =>
            {
                Assert.Equal(terminal, recorded.Decision);
                Assert.Equal(Escalation, recorded.Hold);
                Assert.Equal(interrupted.Receipts[0].ReceiptId, recorded.OriginalReceiptId);
                if (rerunFault)
                    Assert.Null(recorded.ReceiptId);
                else
                    Assert.NotEmpty(recorded.ReceiptId!);
            });
        Assert.All(interrupted.Receipts, receipt =>
        {
            Assert.Equal(interrupted.Task.Id.Value, receipt.RequestingTaskId);
            Assert.Equal(Candidate, receipt.CandidateSha);
            Assert.Equal(Round, receipt.FindingRoundFingerprint);
            Assert.Equal(Guard, receipt.RequestGuard);
            Assert.Equal(FindingEvidenceRequestReceipt.BuildRerunGuard(Guard), receipt.RerunGuard);
        });

        Assert.True(interrupted.Resume(interrupted.CreateDriver(), out var replayed));

        Assert.Equal(expected, replayed);
        Assert.Equal(2, interrupted.FocusedRuns);
        Assert.Single(interrupted.Receipts, receipt => receipt.Decision == CandidateRerunRequested);
        Assert.Single(interrupted.Receipts, receipt => receipt.Decision == terminal);
    }

    [Theory]
    [InlineData("green", CandidateRerunGreen)]
    [InlineData("red", CandidateRerunRed)]
    [InlineData("unusable", CandidateRerunUnusable)]
    public void ProseOnlyTimeline_RecoversSameDecisionAndReceiptId(
        string disposition, FindingEvidenceRequestReceiptDecision terminal)
    {
        using var legacy = new RoutingFixture();
        legacy.WriteProse(BaselineMessage);
        legacy.WriteProse(RequestedMessage);
        legacy.WriteProse($"finding-evidence disposition=candidate-rerun-{disposition}; {Guard}; " +
            $"receipt_id={RerunId}; original_receipt_id={OriginalId}");
        Assert.Collection(legacy.Goal.Timeline
                .Where(evt => evt.Kind == ProgressKind.FindingEvidenceRequestRecorded)
                .Select(evt => FindingEvidenceRequestReceiptLegacyReader.TryRead(evt, legacy.Task.Id, Candidate, Round, Guard)),
            receipt => Assert.Equal(legacy.Receipt(BaselineArmAbsent, OriginalId), receipt),
            receipt => Assert.Equal(legacy.Receipt(CandidateRerunRequested), receipt),
            receipt => Assert.Equal(legacy.Receipt(terminal, RerunId), receipt));

        using var typed = new RoutingFixture();
        typed.WriteTyped(typed.Receipt(BaselineArmAbsent, OriginalId), "Baseline unavailable.");
        typed.WriteTyped(typed.Receipt(terminal, RerunId), "Rerun completed.");
        Assert.True(typed.Resume(typed.CreateDriver(), out var expected));

        Assert.True(legacy.Resume(legacy.CreateDriver(), out var recovered));

        Assert.Equal(expected.Kind, recovered.Kind);
        Assert.Equal(expected.Evidence, recovered.Evidence);
        Assert.Equal(0, legacy.FocusedRuns);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void MissingOrEmptyReceiptId_ThrowsExistingInvalidDataError(bool typed, bool terminal, bool empty)
    {
        using var fixture = new RoutingFixture();
        if (terminal)
            fixture.WriteProse(BaselineMessage);
        var disposition = terminal ? "candidate-rerun-green" : "baseline-arm-absent";
        var decision = terminal ? CandidateRerunGreen : BaselineArmAbsent;
        var qualifier = terminal ? "" : "candidate ";
        if (typed)
        {
            var plausibleDisplay = $"finding-evidence disposition={disposition}; {Guard}; receipt_id=display-only";
            fixture.WriteTyped(fixture.Receipt(decision, empty ? "" : null), plausibleDisplay);
        }
        else
        {
            var fields = empty ? "; receipt_id=" : "";
            fixture.WriteProse($"finding-evidence disposition={disposition}; {Guard}{fields}; reason=unavailable");
        }

        var exception = Assert.Throws<InvalidDataException>(() => fixture.Resume(fixture.CreateDriver(), out _));

        Assert.Equal($"Recorded {disposition} decision has {(empty ? "an empty" : "no")} {qualifier}receipt id.", exception.Message);
        Assert.Equal(0, fixture.FocusedRuns);
    }

    private static void CommitBeforeAcknowledgement(SqliteOrchestratorStateRepository repository, AgentOrchestratorKernel kernel)
    {
        repository.SaveAsync(kernel).GetAwaiter().GetResult();
        throw new IOException("Interrupted after state commit, before inbox acknowledgement.");
    }

    private static void AssertDisplayContract(string scenario, Func<RoutingFixture,
        (bool Resumed, FailedGoalFindingObservationKind Decision, int NewRequests)> route)
    {
        using var fixture = new RoutingFixture();
        if (scenario == "reworded")
        {
            fixture.WriteTyped(fixture.Receipt(BaselineArmAbsent, OriginalId), "The baseline result is unavailable.");
            fixture.WriteTyped(fixture.Receipt(CandidateRerunGreen, RerunId), "The follow-up execution passed.");
            Assert.Equal((true, FailedGoalFindingObservationKind.FindingEvidenceDeliveryRecorded, 0), route(fixture));
            return;
        }

        fixture.WriteTyped(fixture.Receipt(BaselineArmAbsent, OriginalId), BaselineMessage);
        var injected = scenario == "injected-request" ? RequestedMessage : GreenMessage;
        fixture.WriteProse($"Unrelated request quoting {injected}");
        fixture.Goal.Append(new ProgressEvent(fixture.Goal.Id, fixture.Task.Id, ProgressKind.TaskNote, injected, DateTimeOffset.UnixEpoch));
        var preTester = new PreTesterEvidenceEntry("started", Candidate, "pre-tester", [], [], null, []);
        fixture.Kernel.RecordFindingEvidenceRequest(fixture.Goal.Id, fixture.Task.Id, preTester, injected);
        Assert.Equal((true, FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired, 1), route(fixture));
    }

    private static (bool, FailedGoalFindingObservationKind, int) RouteUsingOldProseInference(RoutingFixture fixture)
    {
        // Restore the previous routing predicates on the same timeline and hold the runner's red result fixed.
        var events = fixture.Goal.Timeline.Where(evt =>
            evt.Kind == ProgressKind.FindingEvidenceRequestRecorded && evt.TaskId == fixture.Task.Id);
        if (!events.Any(evt => evt.Message.Contains("disposition=baseline-arm-absent;", StringComparison.Ordinal) &&
                              evt.Message.Contains(Guard, StringComparison.Ordinal)))
            return (false, FailedGoalFindingObservationKind.None, 0);

        var terminal = events.LastOrDefault(evt => evt.Message.Contains(Guard, StringComparison.Ordinal) &&
            (evt.Message.Contains("disposition=candidate-rerun-green;", StringComparison.Ordinal) ||
             evt.Message.Contains("disposition=candidate-rerun-red;", StringComparison.Ordinal) ||
             evt.Message.Contains("disposition=candidate-rerun-unusable;", StringComparison.Ordinal)));
        if (terminal is not null)
            return (true, terminal.Message.Contains("disposition=candidate-rerun-green;", StringComparison.Ordinal)
                ? FailedGoalFindingObservationKind.FindingEvidenceDeliveryRecorded
                : FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired, 0);

        var requested = events.Any(evt => evt.Message.Contains($"disposition=candidate-rerun-requested; {Guard}", StringComparison.Ordinal));
        return (true, FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired, requested ? 0 : 1);
    }

    private sealed class RoutingFixture : IDisposable
    {
        private readonly FindingEvidenceBatch _batch;
        private readonly ConductorFocusedEvidenceRequestContext _context = new(Round, "receipt-routing-batch", []);
        private readonly bool _rerunFault;

        public RoutingFixture(bool rerunFault = false)
        {
            Kernel = new AgentOrchestratorKernel();
            Task = new TaskSpec(TaskId.New(), "Review focused evidence", AgentRole.Reviewer);
            Goal = Kernel.CreateGoal("Route typed receipts", [Task]);
            var finding = EvidenceFindingWithRequest("Run focused evidence", "receipt-route");
            _batch = new("receipt-routing-batch", "Infrastructure.Tests:ConductorDriverTests",
                finding.EvidenceRequest!, [finding], []);
            _rerunFault = rerunFault;
            Root = ConductorDriverTests.CreateTempDirectory();
        }

        public string Root { get; }
        public AgentOrchestratorKernel Kernel { get; private set; }
        public Goal Goal { get; private set; }
        public TaskSpec Task { get; private set; }
        public int FocusedRuns { get; private set; }
        public FindingEvidenceRequestReceipt[] Receipts => Goal.Timeline
            .Select(evt => evt.FindingEvidenceRequest).OfType<FindingEvidenceRequestReceipt>().ToArray();

        public FindingEvidenceRequestReceipt Receipt(FindingEvidenceRequestReceiptDecision decision, string? id = null) =>
            new(Guard, FindingEvidenceRequestReceipt.BuildRerunGuard(Guard), Task.Id.Value, Candidate, Round, decision,
                id, decision == BaselineArmAbsent ? null : OriginalId,
                decision switch
                {
                    BaselineArmAbsent => UnconfirmedRedRouting,
                    CandidateRerunRed or CandidateRerunUnusable => Escalation,
                    _ => null
                });

        public void WriteTyped(FindingEvidenceRequestReceipt receipt, string message) =>
            Kernel.RecordFindingEvidenceRequest(Goal.Id, Task.Id, receipt, message);

        public void WriteProse(string message) => Kernel.RecordFindingEvidenceRequest(Goal.Id, Task.Id, message);

        public ConductorDriver CreateDriver(Action<FindingEvidenceRequestReceipt>? afterRecord = null)
        {
            var driver = MakeDriver(
                getLandingFileScopes: _ => [],
                focusedEvidenceAttemptCoordinator: new(Path.Combine(Root, "attempts"), TrunkBranchName.Default,
                    runInline: true, acquireStableSlotLease: (_, _) => null),
                executionDirectory: Root,
                runFocusedEvidence: (_, request) =>
                {
                    FocusedRuns++;
                    if (_rerunFault && FocusedRuns == 2)
                        throw new InvalidOperationException("Focused rerun failed.");
                    return CandidateOnlyRed(request);
                });
            driver.FindingEvidenceRequestReceiptRecorder = (_, _, receipt, message) =>
            {
                WriteTyped(receipt, message);
                afterRecord?.Invoke(receipt);
            };
            return driver;
        }

        public FailedGoalFindingObservation ResolveMissingBaseline(ConductorDriver driver)
        {
            var evidence = CandidateOnlyRed(_batch.Request);
            var arms = evidence.Arms!.Select(ConductorDriver.CreateFindingEvidenceArmReceipt).ToArray();
            Assert.False(driver.TryResolveMissingBaseline(Goal, Task, ConductorAutonomyPolicy.Permissive,
                _batch, Candidate, Round, _context, evidence, arms, out _, out _, out _, out var decision,
                existingCandidateReceiptId: OriginalId));
            return decision;
        }

        public bool Resume(ConductorDriver driver, out FailedGoalFindingObservation decision) =>
            driver.TryResumeUnconfirmedCandidateRed(Goal, Task, ConductorAutonomyPolicy.Permissive,
                _batch, Candidate, Round, _context, out decision);

        public (bool, FailedGoalFindingObservationKind, int) ResumeAndCountRequests()
        {
            var priorRequests = Receipts.Count(receipt => receipt.Decision == CandidateRerunRequested);
            var resumed = Resume(CreateDriver(), out var decision);
            return (resumed, decision.Kind,
                Receipts.Count(receipt => receipt.Decision == CandidateRerunRequested) - priorRequests);
        }

        public void Reload(AgentOrchestratorKernel kernel)
        {
            Kernel = kernel;
            Goal = kernel.GetGoal(Goal.Id);
            Task = Goal.Tasks.Single();
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private static FocusedEvidenceRunResult CandidateOnlyRed(string request)
        {
            var red = CandidateRedFindingEvidence(request, Candidate, "OutsideTests.FailingMethod");
            return red with { Arms = red.Arms!.Where(arm => arm.Arm == FindingEvidenceArm.Candidate).ToArray() };
        }
    }
}
