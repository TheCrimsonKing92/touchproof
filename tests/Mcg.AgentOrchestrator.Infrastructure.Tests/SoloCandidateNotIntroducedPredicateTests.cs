using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static AttributedFailureDeveloperRouteTests;

// Parallel-safe: each fixture owns its repositories, database and retained evidence.
public sealed class SoloCandidateNotIntroducedPredicateTests
{
    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData("apparatus", "unusable-check")]
    [InlineData("passed", "passed-check")]
    [InlineData("advisory", "advisory-check")]
    [InlineData("reuse", "cross-attempt-reuse")]
    [InlineData("foreign-attempt", "foreign-test-attempt")]
    [InlineData("null-identities", "failing-identities-absent")]
    [InlineData("empty-identities", "failing-identities-absent")]
    [InlineData("missing-attribution", "attributions-missing")]
    [InlineData("missing-identity", "attributions-mismatch")]
    [InlineData("unexpected-identity", "attributions-mismatch")]
    [InlineData("duplicate-identity", "attributions-mismatch")]
    [InlineData("inherited", "non-introduced-origin")]
    [InlineData("unattributed", "non-introduced-origin")]
    [InlineData("unconfirmed", "non-introduced-origin")]
    public async Task Refusal_NamesPredicate_AndPropagatesVerbatim(string fault, string predicate)
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce();
        Mutate(fixture, fault);
        fixture.Complete();
        await fixture.Restart();

        var read = SoloAcceptanceFailureReceiptReader.Read(fixture.Goal, fixture.Context);
        Assert.Null(read.Failure);
        Assert.StartsWith("solo-candidate-not-introduced: predicate=" + predicate + ";", read.Reason);
        Assert.Contains("check=solo tests;", read.Reason);
        if (fault is "null-identities" or "empty-identities")
        {
            Assert.Contains("tests=(unavailable)", read.Reason);
            Assert.Contains("identities=" + (fault == "null-identities" ? "null" : "empty"), read.Reason);
        }
        else Assert.Contains(SoloAttributedFailureRouteFixture.First, read.Reason);
        if (fault is "apparatus" or "passed" or "advisory" or "reuse" or "foreign-attempt")
            Assert.Contains($"test={SoloAttributedFailureRouteFixture.First} origin=Introduced", read.Reason);
        switch (fault)
        {
            case "apparatus":
                Assert.Contains("classification=" + AcceptanceFailureClassifications.FocusedSelectionApparatusFailure, read.Reason);
                break;
            case "foreign-attempt":
                Assert.Contains("testAttempt=foreign; receiptAttempt=" + fixture.Summary.AttemptId, read.Reason);
                break;
            case "missing-identity":
                Assert.Contains("missing=[" + SoloAttributedFailureRouteFixture.Long + "]", read.Reason);
                break;
            case "unexpected-identity":
                Assert.Contains("unexpected=[Foreign.Tests.Fails]", read.Reason);
                break;
            case "duplicate-identity":
                Assert.Contains("duplicate=[" + SoloAttributedFailureRouteFixture.First + "]", read.Reason);
                break;
            case "inherited": case "unattributed": case "unconfirmed":
                foreach (var attr in fixture.Proof().Checks.Single().Check.FailingTestAttributions!)
                    Assert.Contains($"test={attr.TestIdentity} origin={attr.Origin} attribution-evidence=\"{attr.Evidence}\"", read.Reason);
                Assert.Contains(fault == "unconfirmed" ? "rerun=Passed|fixture-rerun.trx|(absent)" : "rerun=(absent)", read.Reason);
                break;
        }

        var driver = fixture.Driver();
        driver.BeginTick(fixture.Kernel, 2);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(
            driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive).Outcome);
        var decision = Assert.IsType<PolicyDecisionRecord>(held.Decision);
        Assert.Contains(read.Reason, held.Reason);
        Assert.Equal(held.Reason, decision.Reason);
        Assert.Equal(read.Reason, decision.DiscriminatingEvidence);
        Assert.Empty(fixture.Starts);
        Assert.Empty(MarkerRetries(fixture.Goal));
        Assert.Equal(0, fixture.Goal.AutomaticAcceptanceRetryCount);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task SixRequiredCases_HavePairwiseDistinctReasons()
    {
        var reasons = new List<string>();
        foreach (var fault in new[] { "inherited", "unattributed", "unconfirmed", "passed", "advisory", "missing-attribution" })
        {
            using var fixture = new SoloAttributedFailureRouteFixture();
            await fixture.Produce();
            Mutate(fixture, fault);
            fixture.Complete();
            var read = SoloAcceptanceFailureReceiptReader.Read(fixture.Goal, fixture.Context);
            Assert.Null(read.Failure);
            reasons.Add(read.Reason);
        }
        Assert.Equal(6, reasons.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task MixedOrigins_PreserveEvidenceAssociationsAndSourceOrder()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce();
        fixture.ReplaceProof(proof => proof with
        {
            Checks = proof.Checks.Select(item => item with { Check = item.Check with
            {
                FailingTestAttributions =
                [
                    new(SoloAttributedFailureRouteFixture.Long, AcceptanceTestFailureOrigin.Inherited, "baseline failed Long"),
                    new(SoloAttributedFailureRouteFixture.First, AcceptanceTestFailureOrigin.UnconfirmedIntroduced,
                        "baseline passed First", new("Passed", "rerun-first.trx", "rerun detail"))
                ]
            } }).ToArray()
        });
        fixture.Complete();

        var read = SoloAcceptanceFailureReceiptReader.Read(fixture.Goal, fixture.Context);
        Assert.Null(read.Failure);
        Assert.EndsWith($"test={SoloAttributedFailureRouteFixture.Long} origin=Inherited " +
            "attribution-evidence=\"baseline failed Long\" rerun=(absent); " +
            $"test={SoloAttributedFailureRouteFixture.First} origin=UnconfirmedIntroduced " +
            "attribution-evidence=\"baseline passed First\" rerun=Passed|rerun-first.trx|rerun detail", read.Reason);
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData(false, "unusable-check")]
    [InlineData(true, "passed-check")]
    public async Task OverlappingFaults_ReportFirstPredicate(bool usable, string predicate)
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce();
        fixture.ReplaceProof(proof => proof with
        {
            Checks = proof.Checks.Select(item => item with { Check = item.Check with
            {
                Passed = true, ExitCode = usable ? 0 : 1, Advisory = true,
                TestResultIsExplicitCrossAttemptReuse = true, TestResultAttemptId = "foreign",
                FailingTestIdentities = null, FailingTestAttributions = null
            } }).ToArray()
        });
        fixture.Complete();

        var read = SoloAcceptanceFailureReceiptReader.Read(fixture.Goal, fixture.Context);
        Assert.Null(read.Failure);
        Assert.StartsWith("solo-candidate-not-introduced: predicate=" + predicate + ";", read.Reason);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task MissingAttributionEvidence_IsExplicitlyMarkedAbsent()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce();
        fixture.ReplaceProof(proof => proof with
        {
            Checks = proof.Checks.Select(item => item with { Check = item.Check with
            {
                FailingTestAttributions = item.Check.FailingTestAttributions!.Select(attr => attr with
                {
                    Origin = AcceptanceTestFailureOrigin.Unattributed, Evidence = null!, CandidateRerun = null
                }).ToArray()
            } }).ToArray()
        });
        fixture.Complete();

        var read = SoloAcceptanceFailureReceiptReader.Read(fixture.Goal, fixture.Context);
        Assert.Null(read.Failure);
        Assert.Contains($"test={SoloAttributedFailureRouteFixture.First} origin=Unattributed " +
            "attribution-evidence=\"(absent)\" rerun=(absent)", read.Reason);
    }

    private static void Mutate(SoloAttributedFailureRouteFixture fixture, string fault) =>
        fixture.ReplaceProof(proof => proof with
        {
            Checks = proof.Checks.Select(item => item with { Check = fault switch
            {
                "apparatus" => item.Check with { FailureClassification = AcceptanceFailureClassifications.FocusedSelectionApparatusFailure },
                "passed" => item.Check with { Passed = true, ExitCode = 0 },
                "advisory" => item.Check with { Advisory = true },
                "reuse" => item.Check with { TestResultIsExplicitCrossAttemptReuse = true },
                "foreign-attempt" => item.Check with { TestResultAttemptId = "foreign" },
                "null-identities" => item.Check with { FailingTestIdentities = null },
                "empty-identities" => item.Check with { FailingTestIdentities = [] },
                "missing-attribution" => item.Check with { FailingTestAttributions = null },
                "missing-identity" => item.Check with { FailingTestAttributions = item.Check.FailingTestAttributions!
                    .Where(attr => attr.TestIdentity == SoloAttributedFailureRouteFixture.First).ToArray() },
                "unexpected-identity" => item.Check with { FailingTestAttributions = item.Check.FailingTestAttributions!
                    .Append(new("Foreign.Tests.Fails", AcceptanceTestFailureOrigin.Introduced, "foreign attribution")).ToArray() },
                "duplicate-identity" => item.Check with { FailingTestAttributions = item.Check.FailingTestAttributions!
                    .Append(item.Check.FailingTestAttributions!.Single(attr => attr.TestIdentity == SoloAttributedFailureRouteFixture.First)).ToArray() },
                "inherited" or "unattributed" or "unconfirmed" => item.Check with
                {
                    FailingTestAttributions = item.Check.FailingTestAttributions!.Select(attr => attr with
                    {
                        Origin = fault switch
                        {
                            "inherited" => AcceptanceTestFailureOrigin.Inherited,
                            "unattributed" => AcceptanceTestFailureOrigin.Unattributed,
                            _ => AcceptanceTestFailureOrigin.UnconfirmedIntroduced
                        },
                        Evidence = "fixture attribution for " + fault + ": " + attr.TestIdentity,
                        CandidateRerun = fault == "unconfirmed" ? new("Passed", "fixture-rerun.trx") : null
                    }).ToArray()
                },
                _ => throw new ArgumentOutOfRangeException(nameof(fault), fault, "Unknown predicate fixture.")
            } }).ToArray()
        });
}
