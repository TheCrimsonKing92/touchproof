using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

using static ConductorDriverTests;

public sealed class ReusedFindingEvidenceRequestReceiptTests
{
    [Fact]
    public void ReusedGreenRecord_CarriesTypedReceipt() =>
        AssertRecordedReceipt(FindingEvidenceRequestReceiptDecision.ReusedGreen);

    [Fact]
    public void ReusedRedRecord_CarriesTypedReceipt() =>
        AssertRecordedReceipt(FindingEvidenceRequestReceiptDecision.ReusedRed);

    [Theory]
    [InlineData(FindingEvidenceRequestReceiptDecision.ReusedGreen)]
    [InlineData(FindingEvidenceRequestReceiptDecision.ReusedRed)]
    public void RewordedDisplay_LeavesTypedReceiptUnchanged(FindingEvidenceRequestReceiptDecision decision)
    {
        var fixture = new ReuseFixture();
        fixture.RecordReuse(decision);
        var original = fixture.FindReuse(decision);
        Assert.NotNull(original);
        Assert.Same(original, fixture.Goal.Timeline[^1]);
        var receipt = Assert.IsType<FindingEvidenceRequestReceipt>(original.FindingEvidenceRequest);

        const string reworded = "Applied evidence from an earlier focused run.";
        var snapshot = fixture.Kernel.ExportGoalSnapshot(fixture.Goal.Id);
        var timeline = snapshot.Timeline.ToArray();
        timeline[^1] = timeline[^1] with { Message = reworded };
        var restored = new AgentOrchestratorKernel();
        restored.ReplaceGoalWithSnapshot(snapshot with { Timeline = timeline });

        var recorded = FindReuseRecord(restored.GetGoal(fixture.Goal.Id).Timeline, fixture.Requester.Id, decision);
        Assert.NotNull(recorded);
        Assert.Equal(reworded, recorded.Message);
        Assert.Equal(receipt, recorded.FindingEvidenceRequest);
    }

    [Theory]
    [InlineData(FindingEvidenceRequestReceiptDecision.ReusedGreen, true)]
    [InlineData(FindingEvidenceRequestReceiptDecision.ReusedGreen, false)]
    [InlineData(FindingEvidenceRequestReceiptDecision.ReusedRed, true)]
    [InlineData(FindingEvidenceRequestReceiptDecision.ReusedRed, false)]
    public void UnrelatedTextNamingReusePhraseAndGuard_YieldsNoReceipt(
        FindingEvidenceRequestReceiptDecision decision, bool otherTask)
    {
        var fixture = new ReuseFixture();
        var message = $"{fixture.HistoricalMessage(decision)}; {fixture.RequestGuard(decision)}";
        if (otherTask)
            fixture.Kernel.RecordFindingEvidenceRequest(fixture.Goal.Id, fixture.DeveloperId, message);
        else
            fixture.Kernel.RecordTaskNote(fixture.Goal.Id, fixture.Requester.Id, message);

        var recorded = fixture.Goal.Timeline[^1];
        Assert.Equal(otherTask ? ProgressKind.FindingEvidenceRequestRecorded : ProgressKind.TaskNote, recorded.Kind);
        Assert.Equal(otherTask ? fixture.DeveloperId : fixture.Requester.Id, recorded.TaskId);
        Assert.Equal(message, recorded.Message);
        Assert.Null(recorded.FindingEvidenceRequest);
        Assert.Null(fixture.ReadLegacy(recorded, decision));
        Assert.Null(fixture.FindReuse(decision));
    }

    [Theory]
    [InlineData(FindingEvidenceRequestReceiptDecision.ReusedGreen)]
    [InlineData(FindingEvidenceRequestReceiptDecision.ReusedRed)]
    public void HistoricalProseReuseRecord_DisplaysUnchangedAndYieldsNoReceipt(FindingEvidenceRequestReceiptDecision decision)
    {
        var fixture = new ReuseFixture();
        var message = fixture.HistoricalMessage(decision);
        fixture.Kernel.RecordFindingEvidenceRequest(fixture.Goal.Id, fixture.Requester.Id, message);

        var recorded = fixture.Goal.Timeline[^1];
        Assert.Equal(ProgressKind.FindingEvidenceRequestRecorded, recorded.Kind);
        Assert.Equal(message, recorded.Message);
        Assert.Null(recorded.FindingEvidenceRequest);
        Assert.Null(fixture.ReadLegacy(recorded, decision));
        Assert.Null(fixture.FindReuse(decision));
    }

    [Theory]
    [InlineData("BaselineArmAbsent", "UnconfirmedRedRouting", 0, FindingEvidenceRequestReceiptDecision.BaselineArmAbsent, FindingEvidenceRequestReceiptHold.UnconfirmedRedRouting)]
    [InlineData("CandidateRerunRequested", null, 1, FindingEvidenceRequestReceiptDecision.CandidateRerunRequested, null)]
    [InlineData("CandidateRerunGreen", null, 2, FindingEvidenceRequestReceiptDecision.CandidateRerunGreen, null)]
    [InlineData("CandidateRerunRed", "Escalation", 3, FindingEvidenceRequestReceiptDecision.CandidateRerunRed, FindingEvidenceRequestReceiptHold.Escalation)]
    [InlineData("CandidateRerunUnusable", "Escalation", 4, FindingEvidenceRequestReceiptDecision.CandidateRerunUnusable, FindingEvidenceRequestReceiptHold.Escalation)]
    [InlineData("PendingExecutionGate", "PendingExecutionGate", 5, FindingEvidenceRequestReceiptDecision.PendingExecutionGate, FindingEvidenceRequestReceiptHold.PendingExecutionGate)]
    public void StoredReceiptsForEarlierDecisions_StillLoad(
        string storedDecision, string? storedHold, int ordinal,
        FindingEvidenceRequestReceiptDecision decision, FindingEvidenceRequestReceiptHold? hold)
    {
        var stored = $$"""
            {
              "RequestGuard": "guard",
              "RerunGuard": "candidate-rerun:guard",
              "RequestingTaskId": "task",
              "CandidateSha": "abc1234",
              "FindingRoundFingerprint": "round",
              "Decision": "{{storedDecision}}",
              "ReceiptId": null,
              "OriginalReceiptId": null,
              "Hold": {{JsonSerializer.Serialize(storedHold)}}
            }
            """;
        var receipt = JsonSerializer.Deserialize<FindingEvidenceRequestReceipt>(stored);
        Assert.Equal(new FindingEvidenceRequestReceipt(
            "guard", "candidate-rerun:guard", "task", "abc1234", "round", decision, null, null, hold), receipt);
        Assert.Equal(ordinal, (int)decision);
        Assert.Equal($"\"{storedDecision}\"", JsonSerializer.Serialize(decision));

        var snapshot = new ProgressEventSnapshot("goal", "task", ProgressKind.FindingEvidenceRequestRecorded,
            "stored request", DateTimeOffset.UnixEpoch, FindingEvidenceRequest: receipt);
        Assert.Equal(snapshot, JsonSerializer.Deserialize<ProgressEventSnapshot>(JsonSerializer.Serialize(snapshot)));
    }

    [Fact]
    public void ReuseDecisions_SerializeAsAppendedMemberNames()
    {
        Assert.Equal("\"ReusedGreen\"", JsonSerializer.Serialize(FindingEvidenceRequestReceiptDecision.ReusedGreen));
        Assert.Equal("\"ReusedRed\"", JsonSerializer.Serialize(FindingEvidenceRequestReceiptDecision.ReusedRed));
        Assert.Equal(6, (int)FindingEvidenceRequestReceiptDecision.ReusedGreen);
        Assert.Equal(7, (int)FindingEvidenceRequestReceiptDecision.ReusedRed);
    }

    internal static ProgressEvent? FindReuseRecord(
        IReadOnlyList<ProgressEvent> timeline, TaskId taskId, FindingEvidenceRequestReceiptDecision decision) =>
        timeline.LastOrDefault(evt =>
            evt.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
            evt.TaskId == taskId &&
            evt.FindingEvidenceRequest is { } receipt &&
            receipt.Decision == decision && receipt.RequestingTaskId == taskId.Value);

    private static void AssertRecordedReceipt(FindingEvidenceRequestReceiptDecision decision)
    {
        var fixture = new ReuseFixture();
        fixture.RecordReuse(decision);

        var recorded = fixture.FindReuse(decision);
        Assert.NotNull(recorded);
        Assert.Equal(fixture.HistoricalMessage(decision), recorded.Message);
        var guard = fixture.RequestGuard(decision);
        Assert.Equal(new FindingEvidenceRequestReceipt(
            guard, $"candidate-rerun:{guard}", fixture.Requester.Id.Value, ReuseFixture.CandidateSha,
            fixture.Fingerprint, decision, ReuseFixture.ReusedReceiptId, OriginalReceiptId: null, Hold: null),
            recorded.FindingEvidenceRequest);
    }

    private sealed class ReuseFixture
    {
        internal const string CandidateSha = "abc1234";
        internal const string ReusedReceiptId = "receipt-from-an-earlier-round";
        private readonly ConductorDriver _driver;
        private readonly ReviewFinding _finding;
        private readonly string _redRequestIdentity;

        internal ReuseFixture()
        {
            (Kernel, Goal) = SoftwareGoal();
            Requester = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            var developer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            DeveloperId = developer.Id;
            foreach (var task in Goal.Tasks.Where(task => task != Requester))
                PassVerification(Kernel, Goal, task, hasCommittedChanges: task == developer);
            _finding = EvidenceFindingWithRequest("Reuse focused evidence", "reuse-finding",
                classes: [nameof(ReusedFindingEvidenceRequestReceiptTests)]);
            FailReviewerNeedsWork(Kernel, Goal, Requester, "Needs focused evidence", findings: [_finding]);
            Assert.True(WorkerResultBlockers.TryFindReviewFindingRound(
                Requester.LastVerification, out var round, out _, Requester.RequiredRole));
            Fingerprint = ConductorDriver.BuildFindingRoundFingerprint(Requester.Id, Requester.LastVerification!.CompletedAt, round);
            _redRequestIdentity = ConductorDriver.BuildFindingEvidenceIdentity(_finding.EvidenceRequest!);
            _driver = MakeDriver(
                recordFindingEvidenceRequest: (goalId, taskId, message) => Kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceOutcome: (goalId, taskId, findingId, outcome, receipt) =>
                    Kernel.RecordFindingEvidenceOutcome(goalId, taskId, findingId, outcome, receipt));
            _driver.FindingEvidenceRequestReceiptRecorder = (goalId, taskId, receipt, message) =>
                Kernel.RecordFindingEvidenceRequest(goalId, taskId, receipt, message);
        }

        internal AgentOrchestratorKernel Kernel { get; }
        internal Goal Goal { get; }
        internal TaskSpec Requester { get; }
        internal TaskId DeveloperId { get; }
        internal string Fingerprint { get; }

        internal void RecordReuse(FindingEvidenceRequestReceiptDecision decision)
        {
            var green = decision == FindingEvidenceRequestReceiptDecision.ReusedGreen;
            var reused = new FindingEvidenceReceipt(ReusedReceiptId, CandidateSha, _finding.EvidenceRequest!,
                Accepted: true, Passed: green, Summary: "Earlier evidence", FindingRoundFingerprint: "previous-round");
            if (green)
            {
                _driver.ReattachReusableGreenFindingEvidence(Goal, Requester, _finding,
                    new FindingEvidenceOutcome(true, ReusedReceiptId, ResultReason: FindingEvidenceOutcomeReason.ValidEvidence), reused);
            }
            else
            {
                _driver.ReattachReusableRedFindingEvidence(Goal, Requester, _finding, _redRequestIdentity, reused);
            }
        }

        internal string RequestGuard(FindingEvidenceRequestReceiptDecision decision)
        {
            // The green request identity hashes the fixture's finding id and project/class selection.
            var identity = decision == FindingEvidenceRequestReceiptDecision.ReusedGreen
                ? "finding-request-262de4ea41727359"
                : _redRequestIdentity;
            return $"candidate_sha={CandidateSha}; finding_round={Fingerprint}; request_identity={identity}";
        }

        internal string HistoricalMessage(FindingEvidenceRequestReceiptDecision decision)
        {
            var disposition = decision == FindingEvidenceRequestReceiptDecision.ReusedGreen ? "reused-green" : "reused-red";
            return $"finding-evidence disposition={disposition}; role=Reviewer; task_id={Requester.Id}; " +
                $"finding_id={_finding.StableId}; candidate_sha={CandidateSha}; receipt_id={ReusedReceiptId}";
        }

        internal ProgressEvent? FindReuse(FindingEvidenceRequestReceiptDecision decision) =>
            FindReuseRecord(Goal.Timeline, Requester.Id, decision);

        internal FindingEvidenceRequestReceipt? ReadLegacy(ProgressEvent evt, FindingEvidenceRequestReceiptDecision decision) =>
            FindingEvidenceRequestReceiptLegacyReader.TryRead(evt, Requester.Id, CandidateSha, Fingerprint, RequestGuard(decision));
    }
}
