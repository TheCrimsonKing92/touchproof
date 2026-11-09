using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: each host writes only its own temporary directory; console and QoS are seamed.
public sealed class MergeTrainChildTypedOutcomeTests
{
    [Theory]
    [InlineData(MergeTrainGateOutcome.InfrastructureFailure, "infrastructure-failure", "gate failed\r\n outcome=incomplete\t\"detail\"")]
    [InlineData(MergeTrainGateOutcome.Invalidated, "invalidated", "identity changed")]
    [InlineData(MergeTrainGateOutcome.Passed, "passed", "gate passed")]
    [InlineData(MergeTrainGateOutcome.Failed, "failed", "gate failed")]
    [InlineData(null, "no-receipt", "no receipt\noriginal detail")]
    [InlineData(null, "no-receipt", "")]
    public void Host_TrainResult_PublishesTypedOutcomeAndVerbatimDetail(
        MergeTrainGateOutcome? outcome, string verdict, string detail)
    {
        using var fixture = new AttemptFixture();
        var receipt = outcome is { } recorded ? Receipt(recorded) : null;
        // Non-passed results carry evidence only in RecordedReceipt, never in the landing receipt.
        var result = new ConductorMergeTrainRunResult(null, new Dictionary<string, ConductorAdvanceResult>(), [], detail)
        { RecordedReceipt = receipt };
        var bodyCalls = 0;
        var exitCode = ConductorGroupedGateAttemptHost.Run(fixture.Attempt.MetadataPath,
            _ => { bodyCalls++; return MergeTrainChildOutcome.From(result); },
            (_, _) => new NoopRestoration(), (_, _, _) => true);

        Assert.Equal(1, bodyCalls);
        Assert.Equal(0, exitCode);
        Assert.Equal("0", File.ReadAllText(fixture.Attempt.ExitCodePath));
        using var document = JsonDocument.Parse(File.ReadAllText(fixture.Attempt.ResultPath));
        var root = document.RootElement;
        Assert.Equal(fixture.Attempt.IdentityValue, root.GetProperty("IdentityValue").GetString());
        Assert.Equal("completed", root.GetProperty("Status").GetString());
        Assert.Equal(verdict, root.GetProperty("Verdict").GetString());
        Assert.Equal(detail, root.GetProperty("Detail").GetString());
        if (receipt is null) Assert.False(root.TryGetProperty("ReceiptId", out _));
        else Assert.Equal(receipt.ReceiptId, root.GetProperty("ReceiptId").GetString());
    }

    [Theory]
    [InlineData(MergeTrainGateOutcome.Passed)]
    [InlineData(MergeTrainGateOutcome.Failed)]
    public void Mapper_BinaryReceipt_KeepsExistingVerdictAndReceipt(MergeTrainGateOutcome outcome)
    {
        var receipt = Receipt(outcome);
        var result = new ConductorMergeTrainRunResult(null, new Dictionary<string, ConductorAdvanceResult>(), [], "detail")
        { RecordedReceipt = receipt };
        var actual = MergeTrainChildOutcome.From(result);
        var existing = Assert.IsType<ConductorGroupedGateOutcome>(ConductorDriver.ToGroupedGateOutcome(outcome, receipt.ReceiptId));
        Assert.Equal(existing.Verdict, actual.Verdict);
        Assert.Equal(existing.ReceiptId, actual.ReceiptId);
    }

    [Fact]
    public void Mapper_DeferredWithoutReceipt_KeepsSharedOutcomeAndPublishedShape()
    {
        using var fixture = new AttemptFixture();
        var result = new ConductorMergeTrainRunResult(null, new Dictionary<string, ConductorAdvanceResult>(), [],
            "outcome=deferred-slots-busy attempts=1");
        var actual = MergeTrainChildOutcome.From(result);
        Assert.Same(CohortStableSlotAcquisitionRounds.DeferredGroupedOutcome, actual);
        Assert.Equal(0, ConductorGroupedGateAttemptHost.Run(fixture.Attempt.MetadataPath,
            _ => actual, (_, _) => new NoopRestoration(), (_, _, _) => true));
        Assert.Equal("{\"IdentityValue\":\"gate-identity\",\"Status\":\"completed\",\"Verdict\":\"deferred-slots-busy\",\"ReceiptId\":\"\"}",
            File.ReadAllText(fixture.Attempt.ResultPath));
        Assert.True(ConductorGroupedGateAttemptCoordinator.IsSlotsBusyDeferral(fixture.Attempt));
    }

    [Fact]
    public void Mapper_DeferredWithRecordedFailure_PreservesReceiptPrecedence()
    {
        var receipt = Receipt(MergeTrainGateOutcome.Failed);
        var result = new ConductorMergeTrainRunResult(null, new Dictionary<string, ConductorAdvanceResult>(), [],
            "outcome=deferred-slots-busy attempts=1") { RecordedReceipt = receipt };
        var actual = MergeTrainChildOutcome.From(result);
        Assert.Equal("failed", actual.Verdict);
        Assert.Equal(receipt.ReceiptId, actual.ReceiptId);
        Assert.Equal(result.Detail, actual.Detail);
    }

    private static MergeTrainReceipt Receipt(MergeTrainGateOutcome outcome)
    {
        var members = new[] { "first", "second" }.Select(id => new MergeTrainMemberBinding(
            new GoalId(id), new string('a', 40), new string('b', 40), [], [], ChangeRiskTier.Behavior,
            ConductorTransitionDecision.Auto, "Clean", "NoConflictsDetected", new string('c', 40))).ToArray();
        return new("r1", MergeTrainIdentity.Create(members, new string('d', 40), new string('e', 40), "manifest"),
            outcome, DateTimeOffset.UnixEpoch, 0, [], null, []);
    }

    private sealed class NoopRestoration : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class AttemptFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"train-typed-outcome-{Guid.NewGuid():N}");
        internal ConductorGroupedGateAttempt Attempt { get; }

        internal AttemptFixture()
        {
            Attempt = new ConductorGroupedGateAttempt("typed-outcome", "train", [], "main", "tree",
                "manifest", "gate-identity", DateTimeOffset.UnixEpoch, 0, 91001,
                Path.Combine(_root, "attempt.json"), Path.Combine(_root, "result.json"),
                Path.Combine(_root, "exit"), Path.Combine(_root, "out.log"), Path.Combine(_root, "err.log"),
                _root, ConductorAutonomyPolicy.Conservative.ToJson());
            ConductorGroupedGateAttemptCoordinator.Save(Attempt);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
