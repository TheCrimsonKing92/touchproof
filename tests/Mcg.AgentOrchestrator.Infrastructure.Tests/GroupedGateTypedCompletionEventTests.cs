using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: unique temporary attempt files, deterministic liveness, no global console or QoS changes.
public sealed class GroupedGateTypedCompletionEventTests
{
    [Theory]
    [InlineData("infrastructure-failure", "r1")]
    [InlineData("invalidated", "r1")]
    [InlineData("no-receipt", null)]
    [InlineData("passed", "r1")]
    [InlineData("failed", "r1")]
    public void Reconcile_TypedResult_EmitsVerdictReceiptAndEscapedDetail(string verdict, string? receipt)
    {
        using var fixture = new AttemptFixture();
        const string detail = "gate\r\nfailed\t\"quoted\" \\path\0\u0085\u2028\u2029 outcome=passed verdict=failed";
        fixture.Publish(new(verdict, receipt) { Detail = detail });

        var line = fixture.Reconcile();
        var expectedPrefix = $"ACCEPTANCE_COHORT_CHILD_COMPLETED kind=train attempt=typed-event " +
            $"members=first+second identity=gate-identity owner={Environment.ProcessId} reason=child-result-published " +
            $"result=completed verdict={verdict}" + (receipt is null ? "" : $" receipt={receipt}");
        Assert.StartsWith(expectedPrefix + " detail=", line);
        Assert.DoesNotContain("verdict=unknown", line);
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\n', line);
        if (receipt is null) Assert.DoesNotContain("receipt=", line);
        var rendered = line[(expectedPrefix.Length + " detail=".Length)..];
        Assert.InRange(rendered.Length, 2, 512);
        Assert.Equal(detail, JsonSerializer.Deserialize<string>(rendered));
        Assert.DoesNotContain('=', rendered);
        Assert.Contains("\\u003d", rendered);
        Assert.DoesNotContain('\u2028', rendered);
        Assert.DoesNotContain('\u2029', rendered);
        Assert.False(ConductorGroupedGateAttemptCoordinator.IsSlotsBusyDeferral(fixture.Attempt));
    }

    [Fact]
    public void Reconcile_LongDetail_EmitsBoundedValidJsonWithTruncationMarker()
    {
        using var fixture = new AttemptFixture();
        fixture.Publish(new("infrastructure-failure", "r1")
        { Detail = string.Concat(Enumerable.Repeat("😀\r\n\"\\=", 600)) });
        var line = fixture.Reconcile();
        var rendered = line[(line.IndexOf(" detail=", StringComparison.Ordinal) + " detail=".Length)..];
        Assert.InRange(rendered.Length, 2, 512);
        Assert.EndsWith("…\"", rendered);
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\n', line);
        Assert.EndsWith("…", JsonSerializer.Deserialize<string>(rendered));
    }

    [Theory]
    [InlineData(508, "😀", false)]
    [InlineData(509, "😀", true)]
    [InlineData(508, "😀\n", true)]
    [InlineData(510, "\n", true)]
    [InlineData(509, "\n", true)]
    [InlineData(510, "", false)]
    public void Formatter_BoundaryDetail_PreservesUnicodeAndCompleteEscapes(
        int prefixLength, string suffix, bool truncated)
    {
        var detail = new string('a', prefixLength) + suffix;
        var rendered = GroupedGateChildCompletionText.RenderDetail(detail);
        Assert.InRange(rendered.Length, 2, 512);
        var decoded = Assert.IsType<string>(JsonSerializer.Deserialize<string>(rendered));
        if (truncated)
        {
            Assert.EndsWith("…", decoded);
            Assert.StartsWith(decoded[..^1], detail);
            Assert.False(char.IsHighSurrogate(decoded[^2]));
        }
        else Assert.Equal(detail, decoded);
    }

    [Fact]
    public void Reconcile_EmptyDetail_PublishesEmptyQuotedValue()
    {
        using var fixture = new AttemptFixture();
        fixture.Publish(new("no-receipt", null) { Detail = "" });
        Assert.EndsWith("result=completed verdict=no-receipt detail=\"\"", fixture.Reconcile());
    }

    [Fact]
    public void Reconcile_LegacyResult_KeepsUnknownWithoutReceiptOrDetail()
    {
        using var fixture = new AttemptFixture();
        File.WriteAllText(fixture.Attempt.ResultPath,
            "{\"IdentityValue\":\"gate-identity\",\"Status\":\"completed\"}");
        var line = fixture.Reconcile();
        Assert.EndsWith("result=completed verdict=unknown", line);
        Assert.DoesNotContain("receipt=", line);
        Assert.DoesNotContain("detail=", line);
        Assert.False(ConductorGroupedGateAttemptCoordinator.IsSlotsBusyDeferral(fixture.Attempt));
    }

    [Theory]
    [InlineData("skipped", "unknown")]
    [InlineData("", "unknown")]
    [InlineData("INFRASTRUCTURE-FAILURE", "unknown")]
    [InlineData("PaSsEd", "passed")]
    [InlineData("FaIlEd", "failed")]
    public void Reconcile_ExistingVerdictRules_KeepCaseAndUnknownBehavior(string verdict, string expected)
    {
        using var fixture = new AttemptFixture();
        fixture.Publish(new(verdict, "r1"));
        Assert.EndsWith($"result=completed verdict={expected} receipt=r1", fixture.Reconcile());
        Assert.False(ConductorGroupedGateAttemptCoordinator.IsSlotsBusyDeferral(fixture.Attempt));
    }

    private sealed class NoopRestoration : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class AttemptFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"grouped-typed-event-{Guid.NewGuid():N}");
        internal ConductorGroupedGateAttempt Attempt { get; private set; }

        internal AttemptFixture()
        {
            Attempt = new ConductorGroupedGateAttempt("typed-event", "train",
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
            Assert.Equal(0, ConductorGroupedGateAttemptHost.Run(Attempt.MetadataPath,
                _ => outcome, (_, _) => new NoopRestoration(), (_, _, _) => true));
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
