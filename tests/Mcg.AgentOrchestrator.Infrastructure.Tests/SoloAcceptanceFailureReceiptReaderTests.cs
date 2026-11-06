using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every fixture owns its repositories and retained evidence files.
public sealed class SoloAcceptanceFailureReceiptReaderTests
{
    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task Read_PassedCollisionsAndSkippedRows_ValidatesOnlyFailedFact()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce();
        var artifact = fixture.UseCollisionEvidence();
        fixture.Complete();

        var read = SoloAcceptanceFailureReceiptReader.Read(fixture.Goal, fixture.Context);

        AssertValidated(read, artifact.Path);
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData("Failed")]
    [InlineData("Passed")]
    public async Task Read_CandidateRerun_RequiresRetainedFailedRow(string outcome)
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce();
        var artifact = fixture.UseCollisionEvidence();
        var rerun = fixture.WriteLiteralTrx("rerun.trx", $$"""
            <TestRun id="rerun">
              <TestDefinitions><UnitTest id="f"><TestMethod className="Sample.Tests.SoloTests" name="Fails" /></UnitTest></TestDefinitions>
              <Results><UnitTestResult testId="f" testName="Fails" outcome="{{outcome}}" /></Results>
            </TestRun>
            """);
        fixture.ReplaceProof(proof => proof with { Checks = proof.Checks.Select(item => item with
        {
            CandidateReruns = new Dictionary<string, SoloAcceptanceArtifact> { [SoloAttributedFailureRouteFixture.First] = rerun },
            Check = item.Check with { FailingTestAttributions = item.Check.FailingTestAttributions!
                .Select(attr => attr with { CandidateRerun = new("Failed", rerun.Path, null) }).ToArray() }
        }).ToArray() });
        fixture.Complete();

        var read = SoloAcceptanceFailureReceiptReader.Read(fixture.Goal, fixture.Context);

        if (outcome == "Failed") AssertValidated(read, artifact.Path);
        else
        {
            Assert.Null(read.Failure);
            Assert.Equal("solo-candidate-rerun-unconfirmed", read.Reason);
        }
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData("Failed")]
    [InlineData("Passed")]
    [InlineData("NotExecuted")]
    public async Task Read_FailingIdentityCollision_RefusesWithIdentityAndOutcomes(string outcome)
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce();
        fixture.UseCollisionEvidence();
        var document = XDocument.Parse(SoloAttributedFailureRouteFixture.CollisionTrx);
        document.Root!.Element("Results")!.Add(new XElement("UnitTestResult",
            new XAttribute("testId", "f"), new XAttribute("testName", "Fails"), new XAttribute("outcome", outcome)));
        var collision = fixture.WriteLiteralTrx("ambiguous.trx", document.ToString());
        fixture.ReplaceProof(proof => proof with { Checks = proof.Checks.Select(item => item with
            { Artifacts = [collision] }).ToArray() });
        fixture.Complete();

        var read = SoloAcceptanceFailureReceiptReader.Read(fixture.Goal, fixture.Context);

        Assert.Null(read.Failure);
        Assert.Contains("solo-trx-ambiguous-row", read.Reason);
        Assert.Contains("identity=" + SoloAttributedFailureRouteFixture.First, read.Reason);
        Assert.Contains("outcomes=Failed," + outcome, read.Reason);
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData(4, 6, null)]
    [InlineData(4, null, null)]
    [InlineData(6, 6, "executed rows=4; recorded=6")]
    [InlineData(2, 6, "executed rows=4; recorded=2")]
    [InlineData(4, 4, "discovered rows=6; recorded=4")]
    [InlineData(4, 7, "discovered rows=6; recorded=7")]
    public async Task Read_RowCounts_UsesExecutedAndDiscoveredRows(
        int executed, int? discovered, string? refusal)
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce();
        var artifact = fixture.UseCollisionEvidence();
        fixture.ReplaceProof(proof => proof with { Checks = proof.Checks.Select(item => item with
            { Check = item.Check with { ExecutedTestCount = executed, DiscoveredTestCount = discovered } }).ToArray() });
        fixture.Complete();

        var read = SoloAcceptanceFailureReceiptReader.Read(fixture.Goal, fixture.Context);

        if (refusal is null) AssertValidated(read, artifact.Path);
        else
        {
            Assert.Null(read.Failure);
            Assert.Contains("solo-incomplete-check-rows: " + refusal, read.Reason);
        }
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_CrossArtifactCollision_RefusesOnlyFailingIdentity(bool failingIdentity)
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce();
        var artifact = fixture.UseCollisionEvidence();
        var extra = fixture.WriteLiteralTrx("extra.trx", failingIdentity ? """
            <TestRun id="extra">
              <TestDefinitions><UnitTest id="f"><TestMethod className="Sample.Tests.SoloTests" name="Fails" /></UnitTest></TestDefinitions>
              <Results><UnitTestResult testId="f" testName="Fails" outcome="Passed" /></Results>
            </TestRun>
            """ : """
            <TestRun id="extra">
              <TestDefinitions><UnitTest id="p"><TestMethod className="Sample.Tests.SoloTests" name="Theory" /></UnitTest></TestDefinitions>
              <Results><UnitTestResult testId="p" testName="Theory(value: &quot;truncated&quot;···)" outcome="Passed" /></Results>
            </TestRun>
            """);
        fixture.ReplaceProof(proof => proof with { Checks = proof.Checks.Select(item => item with
        {
            Artifacts = [artifact, extra],
            Check = item.Check with { ExecutedTestCount = 5, DiscoveredTestCount = 7 }
        }).ToArray() });
        fixture.Complete();

        var read = SoloAcceptanceFailureReceiptReader.Read(fixture.Goal, fixture.Context);

        if (!failingIdentity) AssertValidated(read, artifact.Path);
        else
        {
            Assert.Null(read.Failure);
            Assert.Contains("solo-duplicate-test-row: identity=" + SoloAttributedFailureRouteFixture.First, read.Reason);
            Assert.Contains("outcomes=Failed,Passed", read.Reason);
        }
    }

    private static void AssertValidated(SoloAcceptanceFailureRead read, string path)
    {
        Assert.Equal("solo-evidence-validated", read.Reason);
        var failure = Assert.IsType<AttributedFailure>(read.Failure);
        Assert.Equal(SoloAttributedFailureRouteFixture.First, Assert.Single(failure.FailingTests));
        Assert.Contains("reproduced " + SoloAttributedFailureRouteFixture.First + ": exact first line", failure.ReproductionDetail);
        Assert.Contains(path, failure.ReproductionDetail);
        Assert.DoesNotContain("second line", failure.ReproductionDetail);
    }
}
