using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: unique temporary files; console redirection and execution QoS are seamed.
public sealed class CohortChildTypedOutcomeTests
{
    public static IEnumerable<object?[]> OutcomeCases()
    {
        foreach (var outcome in Enum.GetValues<AcceptanceCohortGateOutcome>())
            yield return [outcome, JsonNamingPolicy.KebabCaseLower.ConvertName(outcome.ToString())];
        yield return [null, "no-receipt"];
    }

    [Theory]
    [MemberData(nameof(OutcomeCases))]
    public void Host_CohortResult_PublishesTypedOutcomeAndDetail(
        AcceptanceCohortGateOutcome? outcome, string verdict)
    {
        using var fixture = new AttemptFixture();
        var receipt = outcome is { } recorded ? Receipt(recorded) : null;
        var bodyCalls = 0;
        var exitCode = ConductorGroupedGateAttemptHost.Run(fixture.Attempt.MetadataPath,
            attempt =>
            {
                bodyCalls++;
                return CohortChildOutcome.From(receipt, slotsBusyDeferred: false, attempt.IdentityValue);
            }, (_, _) => new NoopRestoration(), (_, _, _) => true);

        Assert.Equal(1, bodyCalls);
        Assert.Equal(0, exitCode);
        Assert.Equal("0", File.ReadAllText(fixture.Attempt.ExitCodePath));
        using var document = JsonDocument.Parse(File.ReadAllText(fixture.Attempt.ResultPath));
        var root = document.RootElement;
        Assert.Equal(fixture.Attempt.IdentityValue, root.GetProperty("IdentityValue").GetString());
        Assert.Equal("completed", root.GetProperty("Status").GetString());
        Assert.Equal(verdict, root.GetProperty("Verdict").GetString());
        if (receipt is null)
        {
            Assert.False(root.TryGetProperty("ReceiptId", out _));
            Assert.Equal("outcome=no-receipt cohort=gate-identity", root.GetProperty("Detail").GetString());
        }
        else
        {
            Assert.Equal(receipt.ReceiptId, root.GetProperty("ReceiptId").GetString());
            Assert.Equal($"outcome={verdict} receipt=r1 attribution=Indeterminate gateExit=7 " +
                "infrastructureReason=io-failure invalidation=none failedChecks=check-one,check-two " +
                "infrastructureDetail=gate\r\nfailed\t\"quoted\" outcome=failed", root.GetProperty("Detail").GetString());
            Assert.Equal(CohortChildOutcome.Detail(receipt), root.GetProperty("Detail").GetString());
        }
        Assert.False(ConductorGroupedGateAttemptCoordinator.IsSlotsBusyDeferral(fixture.Attempt));
    }

    [Theory]
    [InlineData(AcceptanceCohortGateOutcome.InfrastructureFailure, "infrastructure-failure")]
    [InlineData(AcceptanceCohortGateOutcome.Invalidated, "invalidated")]
    public void Mapper_NonBinaryReceipt_UsesContractName(
        AcceptanceCohortGateOutcome outcome, string expected)
    {
        Assert.Equal(expected, CohortChildOutcome.From(Receipt(outcome), false, "cohort").Verdict);
    }

    [Fact]
    public void Host_DeferredWithoutReceipt_KeepsSharedOutcomeAndShape()
    {
        using var fixture = new AttemptFixture();
        var outcome = CohortChildOutcome.From(null, slotsBusyDeferred: true, fixture.Attempt.IdentityValue);
        Assert.Same(CohortStableSlotAcquisitionRounds.DeferredGroupedOutcome, outcome);
        Assert.Equal(0, ConductorGroupedGateAttemptHost.Run(fixture.Attempt.MetadataPath,
            _ => outcome, (_, _) => new NoopRestoration(), (_, _, _) => true));
        Assert.Equal("{\"IdentityValue\":\"gate-identity\",\"Status\":\"completed\",\"Verdict\":\"deferred-slots-busy\",\"ReceiptId\":\"\"}",
            File.ReadAllText(fixture.Attempt.ResultPath));
        Assert.True(ConductorGroupedGateAttemptCoordinator.IsSlotsBusyDeferral(fixture.Attempt));
    }

    [Fact]
    public void Mapper_ReceiptAndDeferral_PreservesReceiptPrecedence()
    {
        var receipt = Receipt(AcceptanceCohortGateOutcome.InfrastructureFailure);
        var actual = CohortChildOutcome.From(receipt, slotsBusyDeferred: true, "cohort");
        Assert.Equal("infrastructure-failure", actual.Verdict);
        Assert.Equal(receipt.ReceiptId, actual.ReceiptId);
        Assert.Equal(CohortChildOutcome.Detail(receipt), actual.Detail);
    }

    [Fact]
    public void Mapper_InvalidationAndMissingEvidence_PreservesReasonAndNoneFields()
    {
        var receipt = Receipt(AcceptanceCohortGateOutcome.Invalidated) with
        {
            GateExitCode = null, FailedChecks = [], InfrastructureReasonCode = null, InfrastructureDetail = ""
        };
        receipt = receipt with
        {
            Invalidation = new("i1", receipt.Identity.Value, AcceptanceCohortInvalidationReason.BindingChanged,
                "binding changed", DateTimeOffset.UnixEpoch, receipt.Identity.ObservedMainRevision,
                receipt.Identity.CombinedTreeRevision, receipt.Identity.ManifestIdentity, receipt.Identity.Members)
        };
        Assert.Equal("outcome=invalidated receipt=r1 attribution=Indeterminate gateExit=none " +
            "infrastructureReason=none invalidation=BindingChanged failedChecks=none",
            CohortChildOutcome.From(receipt, false, "cohort").Detail);
    }

    [Fact]
    public void Host_UndefinedOutcome_PublishesFailure()
    {
        using var fixture = new AttemptFixture();
        var bodyCalls = 0;
        Assert.Equal(1, ConductorGroupedGateAttemptHost.Run(fixture.Attempt.MetadataPath,
            attempt =>
            {
                bodyCalls++;
                return CohortChildOutcome.From(Receipt((AcceptanceCohortGateOutcome)int.MaxValue),
                    false, attempt.IdentityValue);
            }, (_, _) => new NoopRestoration(), (_, _, _) => true));
        Assert.Equal(1, bodyCalls);
        using var document = JsonDocument.Parse(File.ReadAllText(fixture.Attempt.ResultPath));
        Assert.Equal("failed", document.RootElement.GetProperty("Status").GetString());
        Assert.Contains("Unknown cohort gate outcome", document.RootElement.GetProperty("Error").GetString());
        Assert.False(document.RootElement.TryGetProperty("Verdict", out _));
    }

    private static AcceptanceCohortReceipt Receipt(AcceptanceCohortGateOutcome outcome)
    {
        var members = new[] { "first", "second" }.Select(id => new AcceptanceCohortMemberBinding(
            new GoalId(id), new string('a', 40), new string('b', 40), [$"src/{id}.cs"], [$"path:{id}"],
            ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto, "Clean", "NoConflictsDetected")).ToArray();
        return new("r1", AcceptanceCohortIdentity.Create(members, new string('c', 40), new string('d', 40), "manifest"),
            outcome, DateTimeOffset.UnixEpoch, 0, ["check-one", "check-two"], 7, [],
            AcceptanceCohortAttributionOutcome.Indeterminate, InfrastructureReasonCode: "io-failure",
            InfrastructureDetail: "gate\r\nfailed\t\"quoted\" outcome=failed");
    }

    private sealed class NoopRestoration : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class AttemptFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"cohort-typed-outcome-{Guid.NewGuid():N}");
        internal ConductorGroupedGateAttempt Attempt { get; }

        internal AttemptFixture()
        {
            Attempt = new ConductorGroupedGateAttempt("typed-outcome", "cohort", [], "main", "tree",
                "manifest", "gate-identity", DateTimeOffset.UnixEpoch, 0, 91001,
                Path.Combine(_root, "attempt.json"), Path.Combine(_root, "result.json"),
                Path.Combine(_root, "exit"), Path.Combine(_root, "out.log"), Path.Combine(_root, "err.log"),
                _root, ConductorAutonomyPolicy.Conservative.ToJson());
            ConductorGroupedGateAttemptCoordinator.Save(Attempt);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
