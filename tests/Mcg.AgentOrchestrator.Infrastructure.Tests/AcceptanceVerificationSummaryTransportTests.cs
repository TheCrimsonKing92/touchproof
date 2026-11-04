using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure controlled results; no provider, process or shared store.
public sealed class AcceptanceVerificationSummaryTransportTests
{
    [Fact]
    public void RecordCopyDerivesRequiredAndAdvisoryFailuresFromReplacementChecks()
    {
        var original = new AcceptanceVerificationSummary(false,
            [new("old required", false, 1, null), new("old advisory", false, 1, null, Advisory: true)]);
        var required = new AcceptanceCheckResult("new required", false, 1, null);
        var advisory = new AcceptanceCheckResult("new advisory", false, 1, null, Advisory: true);
        var copy = original with { UnmetCriteria = [required, advisory, new("passed", true, 0, null)] };

        Assert.Same(required, Assert.Single(copy.RequiredUnmetCriteria));
        Assert.Same(advisory, Assert.Single(copy.AdvisoryUnmetCriteria));
        Assert.Equal("old required", Assert.Single(original.RequiredUnmetCriteria).Name);
        Assert.Equal("old advisory", Assert.Single(original.AdvisoryUnmetCriteria).Name);
    }

    [Fact]
    public void SoloReceiptSurvivesNativeConversionNormalizationAndSerialization()
    {
        var receipt = new SoloAcceptanceFailureReceipt(1, "goal", "attempt", "candidate", "tree", "main", "baseline",
            "root", new("proof.json", "digest"));
        var native = new AcceptanceVerificationResult(false, false, 1, "failure")
            { AttemptId = "attempt", SoloFailureReceipt = receipt };
        var summary = AcceptanceVerificationSummary.FromVerification(native, [], ["solo tests"], "candidate", "main",
            ["candidate.trx"], [new("solo tests", AcceptanceFailureOrigin.Introduced, "baseline proof")], null);
        var normalized = ConductorDriver.NormalizeNamedFailedChecksForRetry(summary);
        var restored = JsonSerializer.Deserialize<AcceptanceVerificationSummary>(JsonSerializer.Serialize(normalized))!;
        Assert.Equal(receipt, restored.SoloFailureReceipt);
        Assert.True(restored.RequiresSoloFailureReceipt);
        Assert.Equal("attempt", restored.AttemptId);
        Assert.Single(restored.RequiredUnmetCriteria);
        Assert.Equal(new[] { "solo tests" }, restored.FailedChecks);
    }

    [Fact]
    public void LostSoloReceiptRetainsEvidenceObligationAcrossSavedSummaryReload()
    {
        var receipt = new SoloAcceptanceFailureReceipt(1, "goal", "attempt", "candidate", "tree", "main", "baseline",
            "root", new("proof.json", "digest"));
        var native = new AcceptanceVerificationResult(false, false, 1, "failure")
            { AttemptId = "attempt", SoloFailureReceipt = receipt };
        var summary = AcceptanceVerificationSummary.FromVerification(native, [], ["solo tests"], "candidate", "main",
            null, [new("solo tests", AcceptanceFailureOrigin.Introduced, "baseline proof")], null);
        var lostReceipt = summary with { SoloFailureReceipt = null };
        var restored = JsonSerializer.Deserialize<AcceptanceVerificationSummary>(JsonSerializer.Serialize(lostReceipt))!;

        Assert.Null(restored.SoloFailureReceipt);
        Assert.True(restored.RequiresSoloFailureReceipt);
        Assert.Equal("attempt", restored.AttemptId);
    }

    [Fact]
    public void OlderSummaryWithAttemptAndAttributionDoesNotDeclareSoloEvidence()
    {
        const string saved = """
            {"Passed":false,"UnmetCriteria":[],"AttemptId":"background-attempt",
             "CheckAttributions":[{"CheckName":"tests","Origin":0,"Evidence":"main green"}]}
            """;
        var restored = JsonSerializer.Deserialize<AcceptanceVerificationSummary>(saved)!;

        Assert.Equal("background-attempt", restored.AttemptId);
        Assert.Single(restored.CheckAttributions!);
        Assert.Null(restored.SoloFailureReceipt);
        Assert.False(restored.RequiresSoloFailureReceipt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_checks_SerializationPreservesAllNamesInReceiptOrder(bool passed)
    {
        var checks = new[]
        {
            new AcceptanceCheckResult("unit", passed, passed ? 0 : 1, "raw check output"),
            new AcceptanceCheckResult("build", true, 0, null)
        };
        var native = new AcceptanceVerificationResult(passed, false, passed ? 0 : 1, null, Checks: checks);
        var summary = AcceptanceVerificationSummary.FromVerification(native,
            passed ? [] : [checks[0]], passed ? [] : [checks[0].Name], "candidate", "main", null, null, null);

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var serialized = JsonSerializer.Serialize(summary, options);
        var restored = JsonSerializer.Deserialize<AcceptanceVerificationSummary>(serialized, options)!;

        Assert.Equal(new[] { "unit", "build" }, restored.CheckNames);
        Assert.Equal(passed ? [] : new[] { "unit" }, restored.FailedChecks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeResult_ConversionPreservesBindingAndOmitsPassingFailureDetail(bool passed)
    {
        var check = new AcceptanceCheckResult("candidate tests", false, 1, "executed RED");
        var attributions = new[] { new AcceptanceCheckAttribution(check.Name, AcceptanceFailureOrigin.Inherited,
            "bound baseline receipt", AcceptanceFailureCause.EnvironmentalApparatus) };
        var native = new AcceptanceVerificationResult(passed, false, passed ? 0 : 1, "native output tail")
            { AttemptId = "native-attempt" };
        var summary = AcceptanceVerificationSummary.FromVerification(native, passed ? [] : [check],
            passed ? [] : [check.Name], "candidate-sha", "main-sha", ["candidate.trx"], attributions, "baseline attestation");

        Assert.Equal(passed, summary.Passed);
        Assert.Equal(passed ? null : native.OutputTail, summary.FailureDetail);
        AssertTransport(summary, attributions);
        Assert.Equal(passed ? 0 : 1, summary.RequiredUnmetCriteria.Count);
        var classified = ConductorDriver.ClassifyInheritedBaselineApparatus(summary);
        Assert.Equal(passed ? null : native.OutputTail, classified.FailureDetail);
        AssertTransport(classified, attributions);
        if (passed)
        {
            Assert.Same(summary, classified);
            Assert.Empty(classified.UnmetCriteria);
        }
        else
        {
            Assert.Equal(AcceptanceFailureClassifications.InheritedBaselineApparatus,
                Assert.Single(classified.RequiredUnmetCriteria).FailureClassification);
            Assert.Null(check.FailureClassification);
        }
    }

    [Fact]
    public void NamedFailure_NormalizationPreservesAttemptAndCandidateTransport()
    {
        var attributions = new[] { new AcceptanceCheckAttribution("candidate tests", AcceptanceFailureOrigin.Inherited,
            "bound baseline receipt", AcceptanceFailureCause.EnvironmentalApparatus) };
        var native = new AcceptanceVerificationResult(false, false, 1, "native output tail")
            { AttemptId = "native-attempt" };
        var summary = AcceptanceVerificationSummary.FromVerification(native, [], ["candidate tests"],
            "candidate-sha", "main-sha", ["candidate.trx"], attributions, "baseline attestation");
        Assert.Empty(summary.RequiredUnmetCriteria);
        var normalized = ConductorDriver.NormalizeNamedFailedChecksForRetry(summary);
        var failure = Assert.Single(normalized.RequiredUnmetCriteria);
        Assert.Equal("acceptance failed checks", failure.Name);
        Assert.Equal(AcceptanceFailureClassifications.InheritedBaselineApparatus, failure.FailureClassification);
        Assert.Equal(native.OutputTail, normalized.FailureDetail);
        AssertTransport(normalized, attributions);
        Assert.Equal(new[] { "candidate tests" }, normalized.FailedChecks);
    }

    private static void AssertTransport(AcceptanceVerificationSummary summary,
        IReadOnlyList<AcceptanceCheckAttribution> attributions)
    {
        Assert.Equal("native-attempt", summary.AttemptId);
        Assert.Equal("candidate-sha", summary.BranchHeadSha);
        Assert.Equal("main-sha", summary.MainHeadSha);
        Assert.Equal(new[] { "candidate.trx" }, summary.TestResultPaths);
        Assert.Equal(attributions, summary.CheckAttributions);
        Assert.Equal("baseline attestation", summary.BaselineAttestation);
    }
}
