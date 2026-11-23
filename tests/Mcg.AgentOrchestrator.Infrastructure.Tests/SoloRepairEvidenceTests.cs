using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static SoloAttributedFailureRouteFixture;

// Parallel-safe: each fixture owns its Git repository, verifier and artifacts.
public sealed class SoloRepairEvidenceTests
{
    private const string FirstCase = "(value: 1)";

    [Fact]
    public async Task ExecutedInheritedRows_RetainExactSelectionAndProof()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce(baselineFails: true, firstCase: FirstCase);
        fixture.Record(fixture.Summary);

        var partition = Read(fixture);
        Assert.Null(partition.Introduced);
        Assert.Equal(new[] { First + FirstCase, Long }, partition.Findings.Select(row => row.Identity).Order().ToArray());
        Assert.All(partition.Findings, finding =>
        {
            Assert.True(finding.Inherited, finding.Reason);
            Assert.Equal(fixture.Context.MainSha, finding.Baseline);
            Assert.Equal(fixture.Summary.AttemptId, finding.Attempt);
            Assert.Equal(fixture.Summary.SoloFailureReceipt!.Evidence, finding.Proof);
            Assert.Contains(AcceptanceTrxTestIdentityResolver.NormalizeSelector(finding.Identity), finding.Request!);
            Assert.NotEmpty(finding.CandidateArtifacts);
            Assert.NotEmpty(finding.BaselineArtifacts);
        });
    }

    [Theory]
    [InlineData("stale-tree")]
    [InlineData("stale-main")]
    [InlineData("foreign-attempt")]
    [InlineData("foreign-inner-proof")]
    [InlineData("bad-digest")]
    [InlineData("outside-root")]
    [InlineData("unexecuted")]
    [InlineData("reused-attempt")]
    [InlineData("apparatus")]
    [InlineData("sibling-case")]
    public async Task UntrustedRow_RemainsUnresolved(string defect)
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        var firstIdentity = defect == "sibling-case" ? First + FirstCase : First;
        await fixture.Produce(baselineFails: true, firstCase: defect == "sibling-case" ? FirstCase : null);
        var summary = fixture.Summary;
        var receipt = summary.SoloFailureReceipt!;
        var context = fixture.Context;
        switch (defect)
        {
            case "stale-tree":
                context = context with { CandidateTreeSha = new string('1', 40) };
                break;
            case "stale-main":
                context = context with { MainSha = new string('2', 40) };
                break;
            case "foreign-attempt":
                summary = summary with { SoloFailureReceipt = receipt with { AttemptId = "foreign-attempt" } };
                break;
            case "foreign-inner-proof":
                fixture.ReplaceProof(proof => proof with { Identity = proof.Identity with { GoalId = "foreign-goal" } });
                summary = fixture.Summary;
                break;
            case "bad-digest":
                File.AppendAllText(receipt.Evidence.Path, " ");
                break;
            case "outside-root":
                var outside = Path.Combine(fixture.Root, "outside.trx");
                WriteTrx(outside, false, First, Long);
                fixture.ReplaceProof(proof => proof with
                {
                    Checks = proof.Checks.Select(check => check with
                    {
                        Artifacts = [new(outside, SoloAcceptanceFailureEvidenceWriter.Digest(File.ReadAllBytes(outside)))]
                    }).ToArray()
                });
                summary = fixture.Summary;
                break;
            case "unexecuted":
            case "reused-attempt":
                fixture.ReplaceProof(proof => proof with
                {
                    Checks = proof.Checks.Select(check => check with
                    {
                        Check = defect == "unexecuted"
                            ? check.Check with { ExecutedTestCount = 0 }
                            : check.Check with { TestResultIsExplicitCrossAttemptReuse = true }
                    }).ToArray()
                });
                summary = fixture.Summary;
                break;
            case "apparatus":
                fixture.ReplaceProof(proof => proof with
                {
                    Baseline = proof.Baseline with { Disposition = FindingEvidenceArmDisposition.ApparatusFailure }
                });
                summary = fixture.Summary;
                break;
            case "sibling-case":
                var sibling = Path.Combine(receipt.ResultsRoot, "sibling.trx");
                WriteTrx(sibling, false, First + "(value: 2)");
                var digest = SoloAcceptanceFailureEvidenceWriter.Digest(File.ReadAllBytes(sibling));
                fixture.ReplaceProof(proof => proof with
                {
                    BaselineChecks = proof.BaselineChecks.Select(check => proof.Bindings.Any(binding =>
                        binding.TestIdentity == firstIdentity && binding.BaselineCheckName == check.Check.Name)
                        ? check with { Artifacts = [new(sibling, digest)] }
                        : check).ToArray()
                });
                summary = fixture.Summary;
                break;
        }
        fixture.Record(summary);

        var partition = Read(fixture, context);
        Assert.Null(partition.Introduced);
        var unresolved = partition.Findings.Where(row => !row.Inherited).ToArray();
        Assert.NotEmpty(unresolved);
        Assert.All(unresolved, row => Assert.False(string.IsNullOrWhiteSpace(row.Reason)));
        if (defect == "sibling-case")
        {
            Assert.Equal(firstIdentity, Assert.Single(unresolved).Identity);
            Assert.Equal(Long, Assert.Single(partition.Findings.Where(row => row.Inherited)).Identity);
        }
        else
        {
            Assert.Equal(partition.Findings.Count, unresolved.Length);
        }
    }

    [Fact]
    public async Task MixedCheck_PartitionsInheritedIntroducedAndUnresolvedRows()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce(mixed: true);
        fixture.ReplaceProof(proof => proof with
        {
            Bindings = proof.Bindings.Where(binding => binding.TestIdentity != Unknown).ToArray()
        });
        fixture.Record(fixture.Summary);

        var partition = Read(fixture);
        Assert.Single(partition.Findings.Select(row => row.Check).Distinct());
        Assert.Equal(First, Assert.Single(partition.Findings.Where(row => row.Inherited)).Identity);
        Assert.Equal(Long, Assert.Single(partition.Introduced!.FailingTests));
        Assert.DoesNotContain(First, partition.Introduced.ReproductionDetail!);
        var unresolved = Assert.Single(partition.Findings.Where(row => !row.Inherited && row.Feedback is null));
        Assert.Equal(Unknown, unresolved.Identity);
        Assert.NotNull(unresolved.Request);
        Assert.NotEmpty(unresolved.CandidateArtifacts);
    }

    [Theory]
    [InlineData("passed", true, true, 1, "passed")]
    [InlineData("failed", false, true, 1, "failed")]
    [InlineData("sibling", true, true, 2, "unresolved")]
    [InlineData("unexecuted", true, false, 1, "unresolved")]
    public void Validation_RequiresTheExecutedOriginalRow(string scenario, bool passed, bool executed,
        int observedCase, string expected)
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        var trx = Path.Combine(fixture.Root, scenario + ".trx");
        WriteTrx(trx, passed, First + $"(value: {observedCase})");
        var finding = SoloRepairReplayTests.Finding() with { Identity = First + "(value: 1)" };
        var result = FocusedResult(finding, finding.Main, trx, passed, executed, First + $"(value: {observedCase})");

        var validation = SoloAcceptanceFailureReceiptReader.ReadRepairCheck(result,
            FindingEvidenceArm.Candidate, finding.Main, finding, fixture.Root);

        Assert.Equal(expected, validation.Outcome);
        if (executed) Assert.NotEmpty(validation.Artifacts);
        else Assert.Empty(validation.Artifacts);
    }

    internal static FocusedEvidenceRunResult FocusedResult(SoloRepairFinding finding, string sha, string trx,
        bool passed, bool executed, string observedIdentity, FindingEvidenceArm arm = FindingEvidenceArm.Candidate)
    {
        var check = new AcceptanceCheckResult(finding.BaselineCheck!, passed, passed ? 0 : 1, null,
            TestResultPaths: [trx], ExecutedTestCount: executed ? 1 : 0,
            FailingTestIdentities: passed ? [] : [observedIdentity]);
        return new(finding.Request!, true, passed, "focused test receipt", [check], Arms:
            [new(arm, sha, passed ? FindingEvidenceArmDisposition.Green : FindingEvidenceArmDisposition.Red,
                true, passed, "focused test arm", [check])]);
    }

    internal static SoloRepairPartition Read(SoloAttributedFailureRouteFixture fixture,
        SoloAcceptanceConsumptionContext? context = null) =>
        Assert.IsType<SoloRepairPartition>(SoloAcceptanceFailureReceiptReader.ReadRepairPartition(fixture.Goal,
            context ?? fixture.Context));
}
