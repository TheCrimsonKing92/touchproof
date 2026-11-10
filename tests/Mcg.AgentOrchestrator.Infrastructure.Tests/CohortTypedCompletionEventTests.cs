using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: unique temporary attempts; liveness, console redirection and QoS are seamed.
public sealed class CohortTypedCompletionEventTests
{
    public static IEnumerable<object?[]> OutcomeCases()
    {
        foreach (var outcome in Enum.GetValues<AcceptanceCohortGateOutcome>())
            yield return [outcome, JsonNamingPolicy.KebabCaseLower.ConvertName(outcome.ToString())];
        yield return [null, "no-receipt"];
    }

    [Theory]
    [MemberData(nameof(OutcomeCases))]
    public void Reconcile_CohortResult_EmitsTypedOutcomeReceiptAndEscapedDetail(
        AcceptanceCohortGateOutcome? outcome, string verdict)
    {
        using var fixture = new AttemptFixture();
        var receipt = outcome is { } recorded ? Receipt(recorded) : null;
        var result = CohortChildOutcome.From(receipt, false, fixture.Attempt.IdentityValue);
        fixture.Publish(result);

        var line = fixture.Reconcile();
        var expectedPrefix = fixture.CompletionPrefix + $" verdict={verdict}" +
            (receipt is null ? "" : $" receipt={receipt.ReceiptId}") + " detail=";
        Assert.StartsWith(expectedPrefix, line);
        Assert.DoesNotContain("verdict=unknown", line);
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\n', line);
        var rendered = line[expectedPrefix.Length..];
        Assert.InRange(rendered.Length, 2, 512);
        Assert.Equal(result.Detail, JsonSerializer.Deserialize<string>(rendered));
        Assert.DoesNotContain('=', rendered);
        Assert.Contains("\\u003d", rendered);
        if (receipt is null) Assert.DoesNotContain(" receipt=", line);
        Assert.True(ConductorGroupedGateAttemptCoordinator.HasFinishedResult(fixture.Attempt));
        Assert.False(ConductorGroupedGateAttemptCoordinator.IsSlotsBusyDeferral(fixture.Attempt));
    }

    [Fact]
    public void Reconcile_LongCohortDetail_EmitsBoundedValidJson()
    {
        using var fixture = new AttemptFixture();
        var receipt = Receipt(AcceptanceCohortGateOutcome.InfrastructureFailure) with
        { InfrastructureDetail = string.Concat(Enumerable.Repeat("😀\r\n\"\\=", 600)) };
        var result = CohortChildOutcome.From(receipt, false, fixture.Attempt.IdentityValue);
        fixture.Publish(result);
        var line = fixture.Reconcile();
        var rendered = line[(line.IndexOf(" detail=", StringComparison.Ordinal) + " detail=".Length)..];
        Assert.Contains("verdict=infrastructure-failure receipt=r1 detail=", line);
        Assert.InRange(rendered.Length, 2, 512);
        var decoded = Assert.IsType<string>(JsonSerializer.Deserialize<string>(rendered));
        Assert.EndsWith("…", decoded);
        Assert.StartsWith(decoded[..^1], Assert.IsType<string>(result.Detail));
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\n', line);
    }

    [Theory]
    [InlineData(AcceptanceCohortGateOutcome.Passed)]
    [InlineData(AcceptanceCohortGateOutcome.Failed)]
    public void Reconcile_BinaryReceipt_KeepsLegacyParentDecisionInputs(AcceptanceCohortGateOutcome outcome)
    {
        var receipt = Receipt(outcome);
        var legacy = Assert.IsType<ConductorGroupedGateOutcome>(
            ConductorDriver.ToGroupedGateOutcome(outcome, receipt.ReceiptId));
        var typed = CohortChildOutcome.From(receipt, false, receipt.Identity.Value);
        Assert.Equal(legacy.Verdict, typed.Verdict);
        Assert.Equal(legacy.ReceiptId, typed.ReceiptId);
        using var oldFixture = new AttemptFixture();
        using var typedFixture = new AttemptFixture();
        oldFixture.Publish(legacy);
        typedFixture.Publish(typed);

        Assert.Equal(ConductorGroupedGateAttemptCoordinator.HasFinishedResult(oldFixture.Attempt),
            ConductorGroupedGateAttemptCoordinator.HasFinishedResult(typedFixture.Attempt));
        Assert.True(ConductorGroupedGateAttemptCoordinator.HasFinishedResult(typedFixture.Attempt));
        Assert.False(ConductorGroupedGateAttemptCoordinator.IsSlotsBusyDeferral(oldFixture.Attempt));
        Assert.False(ConductorGroupedGateAttemptCoordinator.IsSlotsBusyDeferral(typedFixture.Attempt));
        var oldEvent = oldFixture.Reconcile();
        var typedEvent = typedFixture.Reconcile();
        Assert.Equal(oldFixture.CompletionPrefix + $" verdict={legacy.Verdict} receipt=r1", oldEvent);
        Assert.StartsWith(oldEvent + " detail=", typedEvent);
    }

    [Fact]
    public void Reconcile_LegacyCompleted_KeepsUnknownWithoutReceiptOrDetail()
    {
        using var fixture = new AttemptFixture();
        File.WriteAllText(fixture.Attempt.ResultPath,
            "{\"IdentityValue\":\"gate-identity\",\"Status\":\"completed\"}");
        Assert.Equal(fixture.CompletionPrefix + " verdict=unknown", fixture.Reconcile());
        Assert.False(ConductorGroupedGateAttemptCoordinator.HasFinishedResult(fixture.Attempt));
        File.WriteAllText(fixture.Attempt.ExitCodePath, "0");
        Assert.True(ConductorGroupedGateAttemptCoordinator.HasFinishedResult(fixture.Attempt));
        Assert.False(ConductorGroupedGateAttemptCoordinator.IsSlotsBusyDeferral(fixture.Attempt));
    }

    [Theory]
    [MemberData(nameof(OutcomeCases))]
    public void Reader_DefinedOutcome_RecognizesOrdinalName(
        AcceptanceCohortGateOutcome? outcome, string verdict)
    {
        Assert.True(CohortChildOutcome.IsPublishedVerdict(verdict));
        Assert.Equal(verdict, GroupedGateChildCompletionText.RecognizeVerdict(verdict));
        Assert.False(CohortChildOutcome.IsPublishedVerdict(verdict.ToUpperInvariant()));
        if (outcome is not (AcceptanceCohortGateOutcome.Passed or AcceptanceCohortGateOutcome.Failed))
            Assert.Equal("unknown", GroupedGateChildCompletionText.RecognizeVerdict(verdict.ToUpperInvariant()));
    }

    [Theory]
    [InlineData(null, "unknown")]
    [InlineData("skipped", "unknown")]
    [InlineData("", "unknown")]
    [InlineData("PaSsEd", "passed")]
    [InlineData("FaIlEd", "failed")]
    public void Reconcile_ExistingVerdictRules_KeepCaseAndUnknownBehavior(string? verdict, string expected)
    {
        using var fixture = new AttemptFixture();
        File.WriteAllText(fixture.Attempt.ResultPath, JsonSerializer.Serialize(new
        { fixture.Attempt.IdentityValue, Status = "completed", Verdict = verdict }));
        Assert.Equal(fixture.CompletionPrefix + $" verdict={expected}", fixture.Reconcile());
    }

    private static AcceptanceCohortReceipt Receipt(AcceptanceCohortGateOutcome outcome)
    {
        var members = new[] { "first", "second" }.Select(id => new AcceptanceCohortMemberBinding(
            new GoalId(id), new string('a', 40), new string('b', 40), [$"src/{id}.cs"], [$"path:{id}"],
            ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto, "Clean", "NoConflictsDetected")).ToArray();
        return new("r1", AcceptanceCohortIdentity.Create(members, new string('c', 40), new string('d', 40), "manifest"),
            outcome, DateTimeOffset.UnixEpoch, 0, [], 1, [], InfrastructureReasonCode: "io-failure",
            InfrastructureDetail: "gate\r\nfailed\t\"quoted\" \\path\0\u0085\u2028\u2029 outcome=passed");
    }

    private sealed class NoopRestoration : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class AttemptFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"cohort-typed-event-{Guid.NewGuid():N}");
        internal ConductorGroupedGateAttempt Attempt { get; private set; }
        internal string CompletionPrefix => "ACCEPTANCE_COHORT_CHILD_COMPLETED kind=cohort attempt=typed-event " +
            $"members=first+second identity=gate-identity owner={Attempt.OwnerProcessId} " +
            "reason=child-result-published result=completed";

        internal AttemptFixture()
        {
            Attempt = new ConductorGroupedGateAttempt("typed-event", "cohort",
                [new("first", "branch", "candidate", [], [], ChangeRiskTier.Behavior),
                 new("second", "branch", "candidate", [], [], ChangeRiskTier.Behavior)],
                "main", "tree", "manifest", "gate-identity", DateTimeOffset.UnixEpoch, 0, 91001,
                Path.Combine(_root, "attempt.json"), Path.Combine(_root, "result.json"),
                Path.Combine(_root, "exit"), Path.Combine(_root, "out.log"), Path.Combine(_root, "err.log"),
                _root, ConductorAutonomyPolicy.Conservative.ToJson());
            ConductorGroupedGateAttemptCoordinator.Save(Attempt);
        }

        internal void Publish(ConductorGroupedGateOutcome outcome)
        {
            var bodyCalls = 0;
            Assert.Equal(0, ConductorGroupedGateAttemptHost.Run(Attempt.MetadataPath,
                _ => { bodyCalls++; return outcome; }, (_, _) => new NoopRestoration(), (_, _, _) => true));
            Assert.Equal(1, bodyCalls);
            Attempt = ConductorGroupedGateAttemptCoordinator.Read(Attempt.MetadataPath);
        }

        internal string Reconcile()
        {
            var events = new List<string>();
            var reconciledAt = DateTimeOffset.UnixEpoch.AddHours(1);
            var coordinator = new ConductorGroupedGateAttemptCoordinator(_root,
                isProcessAlive: _ => false, utcNow: () => reconciledAt, eventSink: events.Add);
            var reconciled = coordinator.TryReconcileDead(Attempt, "child-result-published");
            Assert.Equal("Reconciled", reconciled.Outcome);
            Assert.Equal(reconciledAt, reconciled.ReconciledAt);
            Assert.Equal("child-result-published", reconciled.Detail);
            return Assert.Single(events);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
