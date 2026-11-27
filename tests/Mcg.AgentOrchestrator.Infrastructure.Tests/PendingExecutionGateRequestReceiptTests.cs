using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class PendingExecutionGateRequestReceiptTests
{
    private const string Candidate = "abc1234";
    private const string Disposition = "finding-evidence disposition=pending-execution-gate;";

    [Fact]
    public void RecordedRequest_CarriesPendingExecutionGateReceipt()
    {
        using var fixture = new PendingGateFixture();
        var receipt = fixture.Receipt;

        Assert.Equal(fixture.TesterId.Value, receipt.RequestingTaskId);
        Assert.Equal(Candidate, receipt.CandidateSha);
        Assert.False(string.IsNullOrWhiteSpace(receipt.FindingRoundFingerprint));
        Assert.StartsWith($"candidate_sha={Candidate}; finding_round={receipt.FindingRoundFingerprint}; request_identity=",
            receipt.RequestGuard);
        Assert.Equal(FindingEvidenceRequestReceipt.BuildRerunGuard(receipt.RequestGuard), receipt.RerunGuard);
        Assert.Equal(FindingEvidenceRequestReceiptDecision.PendingExecutionGate, receipt.Decision);
        Assert.Equal(FindingEvidenceRequestReceiptHold.PendingExecutionGate, receipt.Hold);
        Assert.Null(receipt.ReceiptId);
        Assert.Null(receipt.OriginalReceiptId);
        Assert.Equal(fixture.HistoricalMessage, fixture.DisplayMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatchingTypedReceipt_RecordsNoSecondRequest(bool newerNonmatchingReceipt)
    {
        using var fixture = new PendingGateFixture();
        fixture.RecordTyped(fixture.Receipt, fixture.HistoricalMessage);
        if (newerNonmatchingReceipt)
            fixture.RecordTyped(fixture.Receipt with { CandidateSha = "def5678" }, "Newer request for another candidate.");

        Assert.Empty(fixture.ObserveRequests());
    }

    [Theory]
    [InlineData("sha")]
    [InlineData("round")]
    [InlineData("request-guard")]
    public void ChangedIdentity_RecordsNewRequest(string changedField)
    {
        using var fixture = new PendingGateFixture();
        var stored = changedField switch
        {
            "sha" => fixture.Receipt with { CandidateSha = "def5678" },
            "round" => fixture.Receipt with { FindingRoundFingerprint = "other-round" },
            "request-guard" => fixture.Receipt with { RequestGuard = "other-guard" },
            _ => throw new ArgumentOutOfRangeException(nameof(changedField))
        };
        fixture.RecordTyped(stored, fixture.HistoricalMessage);

        Assert.Equal(fixture.Receipt, Assert.Single(fixture.ObserveRequests()).Receipt);
    }

    [Theory]
    [InlineData("quoted-request")]
    [InlineData("task-note")]
    [InlineData("pre-tester")]
    public void UnrelatedTextNamingDispositionAndGuard_DoesNotSuppressRequest(string source)
    {
        using var fixture = new PendingGateFixture();
        var message = fixture.HistoricalMessage;
        switch (source)
        {
            case "quoted-request":
                fixture.RecordProse($"Unrelated request quoting {message}");
                break;
            case "task-note":
                fixture.Goal.Append(new ProgressEvent(fixture.Goal.Id, fixture.TesterId, ProgressKind.TaskNote,
                    message, DateTimeOffset.UnixEpoch));
                break;
            case "pre-tester":
                fixture.Kernel.RecordFindingEvidenceRequest(fixture.Goal.Id, fixture.TesterId,
                    new PreTesterEvidenceEntry("started", Candidate, "pre-tester", [], [], null, []), message);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(source));
        }

        Assert.Equal(fixture.Receipt, Assert.Single(fixture.ObserveRequests()).Receipt);
    }

    [Fact]
    public void RewordedDisplayWithUnchangedTypedReceipt_SuppressesRequest()
    {
        using var fixture = new PendingGateFixture();
        fixture.RecordTyped(fixture.Receipt, "Focused evidence is waiting for the execution gate.");

        Assert.Empty(fixture.ObserveRequests());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HistoricalProseRecord_DecodesAndSuppressesDuplicate(bool hasTrailingField)
    {
        using var fixture = new PendingGateFixture();
        fixture.RecordProse(hasTrailingField ? fixture.HistoricalMessage : fixture.HistoricalHeader);

        var recorded = fixture.Goal.Timeline[^1];
        Assert.Null(recorded.FindingEvidenceRequest);
        Assert.Equal(fixture.Receipt, fixture.ReadLegacy(recorded));
        Assert.Empty(fixture.ObserveRequests());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TypedPayload_IsNeverReadThroughProsePath(bool differentDecision)
    {
        using var fixture = new PendingGateFixture();
        var stored = differentDecision
            ? fixture.Receipt with { Decision = FindingEvidenceRequestReceiptDecision.CandidateRerunRequested, Hold = null }
            : fixture.Receipt with { CandidateSha = "def5678" };
        fixture.RecordTyped(stored, fixture.HistoricalMessage);

        var recorded = fixture.Goal.Timeline[^1];
        Assert.Equal(stored, recorded.FindingEvidenceRequest);
        Assert.Null(fixture.ReadLegacy(recorded));
        Assert.Equal(fixture.Receipt, Assert.Single(fixture.ObserveRequests()).Receipt);
    }

    [Theory]
    [InlineData(" {guard}; role=Tester; task_id=task; finding_ids=finding", ProgressKind.FindingEvidenceRequestRecorded)]
    [InlineData(" role=Tester; task_id=task; finding_ids=prefix{guard}", ProgressKind.FindingEvidenceRequestRecorded)]
    [InlineData(" role=Tester; task_id=task; finding_ids=finding; {guard}suffix", ProgressKind.FindingEvidenceRequestRecorded)]
    [InlineData(" role=Tester; task_id=task; {guard}", ProgressKind.FindingEvidenceRequestRecorded)]
    [InlineData(" role=Tester; task_id=task; finding_ids=finding; {guard}", ProgressKind.TaskNote)]
    public void LegacyReader_RejectsGuardOutsideRecordedPosition(string fields, ProgressKind kind)
    {
        const string guard = "candidate_sha=abc1234; finding_round=round; request_identity=request";
        var taskId = TaskId.New();
        var evt = new ProgressEvent(GoalId.New(), taskId, kind,
            Disposition + fields.Replace("{guard}", guard, StringComparison.Ordinal), DateTimeOffset.UnixEpoch);

        Assert.Null(FindingEvidenceRequestReceiptLegacyReader.TryRead(evt, taskId, Candidate, "round", guard));
    }

    private sealed class PendingGateFixture : IDisposable
    {
        private readonly string _root;
        private readonly TaskId _developerId;
        private readonly string _findingId;

        public PendingGateFixture()
        {
            (Kernel, Goal) = SoftwareGoal();
            var developer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            var tester = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            _developerId = developer.Id;
            TesterId = tester.Id;
            PassVerification(Kernel, Goal, developer, hasCommittedChanges: true);
            DispatchTask(Kernel, Goal, tester, "test");
            var finding = EvidenceFindingWithRequest(
                "The candidate needs focused evidence before a Developer retry.",
                id: "pending-once", category: FindingCategory.Correctness, classes: ["ConductorDriverTests"]);
            _findingId = finding.StableId;
            var stdout = string.Join(Environment.NewLine,
                "WORKER_RESULT:", "files: none", "commands: inspect focused behavior",
                "tests: deferred - acceptance owns execution", "commit: none", "blockers: none",
                $"findings: {JsonSerializer.Serialize(new[] { finding })}",
                "touched_anchors: []", "verdict: needs-work",
                "model_fit: fixture/model - adequate - source verification",
                "skills: none", "confidence: high", "END_WORKER_RESULT");
            Kernel.RecordDispatchExecutionResult(Goal.Id, TesterId, new TaskVerificationRecord(
                "test", "C:\\tmp", 0, stdout, "", DateTimeOffset.UtcNow,
                StandardOutputPath: "C:\\tmp\\tester.out.log", WorkerResultPresent: true));

            _root = ConductorDriverTests.CreateTempDirectory();
            try
            {
                // The probe opens a finding batch. Leave the test goal and its attempt directory fresh
                // so the next observation reaches the pending-gate lookup instead of resuming that batch.
                var probe = new AgentOrchestratorKernel();
                probe.ReplaceGoalWithSnapshot(Kernel.ExportGoalSnapshot(Goal.Id));
                (Receipt, DisplayMessage) = Assert.Single(ObserveRequests(probe, "probe"));
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public TaskId TesterId { get; }
        public FindingEvidenceRequestReceipt Receipt { get; }
        public string DisplayMessage { get; }
        public string HistoricalHeader =>
            $"{Disposition} role=Tester; task_id={TesterId}; finding_ids={_findingId}; {Receipt.RequestGuard}";
        public string HistoricalMessage => $"{HistoricalHeader}; deferred_developer_task_id={_developerId}";

        public void RecordTyped(FindingEvidenceRequestReceipt receipt, string message) =>
            Kernel.RecordFindingEvidenceRequest(Goal.Id, TesterId, receipt, message);

        public void RecordProse(string message) => Kernel.RecordFindingEvidenceRequest(Goal.Id, TesterId, message);

        public FindingEvidenceRequestReceipt? ReadLegacy(ProgressEvent evt) =>
            FindingEvidenceRequestReceiptLegacyReader.TryRead(evt, TesterId, Candidate,
                Receipt.FindingRoundFingerprint, Receipt.RequestGuard);

        public IReadOnlyList<(FindingEvidenceRequestReceipt Receipt, string Message)> ObserveRequests() =>
            ObserveRequests(Kernel, "candidate");

        private IReadOnlyList<(FindingEvidenceRequestReceipt Receipt, string Message)> ObserveRequests(
            AgentOrchestratorKernel kernel, string attemptDirectory)
        {
            var requests = new List<(FindingEvidenceRequestReceipt Receipt, string Message)>();
            const int processId = 7102;
            var launches = 0;
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(_root, attemptDirectory), TrunkBranchName.Default, isProcessAlive: pid => pid == processId,
                launchOwnedProcess: _ =>
                {
                    launches++;
                    return new ConductorParallelAcceptanceOwnedProcessLaunchResult(processId);
                },
                acquireStableSlotLease: (_, _) => null);
            var driver = MakeDriver(
                getPreReviewEvidenceContext: _ => NoPreReviewContext(Candidate),
                focusedEvidenceAttemptCoordinator: coordinator,
                runFocusedEvidence: (_, request) => PassingPreReviewEvidence(request),
                recordFindingEvidenceRequest: (goalId, taskId, message) =>
                    kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                    kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));
            driver.FindingEvidenceRequestReceiptRecorder = (_, _, receipt, message) => requests.Add((receipt, message));

            Assert.True(driver.TryObserveVerifyingFindingRecovery(kernel.GetGoal(Goal.Id),
                ConductorAutonomyPolicy.Permissive, [], out var observation));
            Assert.Equal(FailedGoalFindingObservationKind.FindingEvidencePending, observation.Kind);
            Assert.Equal(1, launches);
            return requests;
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
