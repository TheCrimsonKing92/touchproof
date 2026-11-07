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
    public async Task Read_CrossArtifactCollision_RefusesFlakeOnlyForFailingIdentity(bool failingIdentity)
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
            Assert.Contains("identity=" + SoloAttributedFailureRouteFixture.First, read.Reason);
            Assert.Contains("outcome=" + AcceptanceLaneRerunEvidence.Flake, read.Reason);
            Assert.Contains("outcomes=Failed,Passed", read.Reason);
            Assert.DoesNotContain("solo-duplicate-test-row", read.Reason);
        }
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Read_RepeatedFailures_RetainsFirstMessageAndPath(int rerunCount)
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce();
        var artifact = fixture.UseCollisionEvidence();
        var reruns = Enumerable.Range(1, rerunCount)
            .Select(index => WriteExecution(fixture, $"rerun-{index}.trx", "Failed")).ToArray();
        fixture.ReplaceProof(proof => proof with { Checks = proof.Checks.Select(item => item with
            { Artifacts = new[] { artifact }.Concat(reruns).ToArray() }).ToArray() });
        fixture.Complete();

        var read = SoloAcceptanceFailureReceiptReader.Read(fixture.Goal, fixture.Context);

        AssertValidated(read, artifact.Path);
        foreach (var rerun in reruns) Assert.DoesNotContain(rerun.Path, read.Failure!.ReproductionDetail);
        Assert.DoesNotContain("rerun line", read.Failure!.ReproductionDetail);
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData(4, 6, null)]
    [InlineData(4, null, null)]
    [InlineData(3, 6, "executed rows=4; recorded=3")]
    [InlineData(4, 5, "discovered rows=6; recorded=5")]
    [InlineData(5, 7, "executed rows=4; recorded=5")]
    [InlineData(4, 7, "discovered rows=6; recorded=7")]
    public async Task Read_RepeatedFailureCounts_RequiresPerArtifactMaxima(
        int executed, int? discovered, string? refusal)
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce();
        var artifact = fixture.UseCollisionEvidence();
        var rerun = WriteExecution(fixture, "rerun.trx", "Failed");
        fixture.ReplaceProof(proof => proof with { Checks = proof.Checks.Select(item => item with
        {
            Artifacts = [artifact, rerun],
            Check = item.Check with { ExecutedTestCount = executed, DiscoveredTestCount = discovered }
        }).ToArray() });
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
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public async Task Read_MixedExecutions_RefusesFlakeRegardlessOfOrderAndCount(
        bool passedFirst, int failedExecutions)
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce();
        var artifact = fixture.UseCollisionEvidence();
        var passed = WriteExecution(fixture, "passed.trx", "Passed");
        var artifacts = new List<SoloAcceptanceArtifact> { artifact };
        if (failedExecutions > 1) artifacts.Add(WriteExecution(fixture, "failed.trx", "Failed"));
        if (passedFirst) artifacts.Insert(0, passed);
        else artifacts.Add(passed);
        fixture.ReplaceProof(proof => proof with { Checks = proof.Checks.Select(item => item with
            { Artifacts = artifacts }).ToArray() });
        fixture.Complete();

        var read = SoloAcceptanceFailureReceiptReader.Read(fixture.Goal, fixture.Context);

        Assert.Null(read.Failure);
        Assert.Contains("identity=" + SoloAttributedFailureRouteFixture.First, read.Reason);
        Assert.Contains("outcome=" + AcceptanceLaneRerunEvidence.Flake, read.Reason);
        Assert.DoesNotContain("solo-duplicate-test-row", read.Reason);
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData("NotExecuted", false)]
    [InlineData("Failed", true)]
    public async Task Read_UnsupportedRepetitions_RetainsDuplicateRefusal(
        string outcome, bool sameArtifact)
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce();
        var artifact = fixture.UseCollisionEvidence();
        var extra = sameArtifact ? artifact : WriteExecution(fixture, "extra.trx", outcome);
        fixture.ReplaceProof(proof => proof with { Checks = proof.Checks.Select(item => item with
            { Artifacts = [artifact, extra] }).ToArray() });
        fixture.Complete();

        var read = SoloAcceptanceFailureReceiptReader.Read(fixture.Goal, fixture.Context);

        Assert.Null(read.Failure);
        Assert.Contains("solo-duplicate-test-row: identity=" + SoloAttributedFailureRouteFixture.First, read.Reason);
        Assert.Contains("outcomes=Failed," + outcome, read.Reason);
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData("Failed")]
    [InlineData("Passed")]
    [InlineData("NotExecuted")]
    public async Task Read_AmbiguousRerunArtifact_RetainsTrxAmbiguityRefusal(string outcome)
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce();
        var artifact = fixture.UseCollisionEvidence();
        var rerun = WriteExecution(fixture, "rerun.trx", "Failed", outcome);
        fixture.ReplaceProof(proof => proof with { Checks = proof.Checks.Select(item => item with
            { Artifacts = [artifact, rerun] }).ToArray() });
        fixture.Complete();

        var read = SoloAcceptanceFailureReceiptReader.Read(fixture.Goal, fixture.Context);

        Assert.Null(read.Failure);
        Assert.Contains("solo-trx-ambiguous-row", read.Reason);
        Assert.Contains("identity=" + SoloAttributedFailureRouteFixture.First, read.Reason);
        Assert.Contains("outcomes=Failed," + outcome, read.Reason);
    }

    private static SoloAcceptanceArtifact WriteExecution(
        SoloAttributedFailureRouteFixture fixture, string filename, params string[] outcomes)
    {
        var document = new XDocument(new XElement("TestRun", new XAttribute("id", filename),
            new XElement("TestDefinitions", new XElement("UnitTest", new XAttribute("id", "f"),
                new XElement("TestMethod", new XAttribute("className", "Sample.Tests.SoloTests"), new XAttribute("name", "Fails")))),
            new XElement("Results", outcomes.Select(outcome => new XElement("UnitTestResult",
                new XAttribute("testId", "f"), new XAttribute("testName", "Fails"), new XAttribute("outcome", outcome),
                new XElement("Output", new XElement("ErrorInfo", new XElement("Message", "rerun line"))))))));
        return fixture.WriteLiteralTrx(filename, document.ToString());
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
