using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class SoloFlakePartitionTests
{
    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task MixedEvidenceRoutesDeterministicFailure(bool declareFlake, bool repeatSurvivor)
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce();
        var first = SoloAttributedFailureRouteFixture.First;
        var flake = SoloAttributedFailureRouteFixture.Long;
        var candidate = Assert.Single(fixture.Proof().Checks.Where(item => item.Check.FailingTestIdentities?.Contains(first) == true));
        var original = Assert.Single(candidate.Artifacts);
        var survivorRow = repeatSurvivor ? """
            <UnitTestResult testId="f" testName="Fails" outcome="Failed">
              <Output><ErrorInfo><Message>rerun line</Message></ErrorInfo></Output>
            </UnitTestResult>
            """ : "";
        var rerun = fixture.WriteLiteralTrx("mixed-rerun.trx", $"""
            <TestRun id="mixed-rerun">
              <TestDefinitions>
                <UnitTest id="l"><TestMethod className="Sample.Tests.SoloTests" name="Long" /></UnitTest>
                <UnitTest id="f"><TestMethod className="Sample.Tests.SoloTests" name="Fails" /></UnitTest>
              </TestDefinitions>
              <Results><UnitTestResult testId="l" testName="Long" outcome="Passed" />{survivorRow}</Results>
            </TestRun>
            """);
        fixture.ReplaceProof(proof => proof with
        {
            Checks = proof.Checks.Select(item => item.Check.Name != candidate.Check.Name ? item : item with
            {
                Artifacts = [original, rerun],
                Check = item.Check with
                {
                    ExecutedTestCount = 2,
                    DiscoveredTestCount = 2,
                    FailingTestIdentities = declareFlake ? [first, flake] : [first],
                    FailingTestAttributions = item.Check.FailingTestAttributions!
                        .Where(attribution => declareFlake || attribution.TestIdentity == first).ToArray()
                }
            }).ToArray(),
            Bindings = proof.Bindings.Where(binding => binding.TestIdentity != flake).ToArray()
        });
        fixture.Complete();

        var read = SoloAcceptanceFailureReceiptReader.Read(fixture.Goal, fixture.Context);

        Assert.Equal("solo-evidence-validated", read.Reason);
        var failure = Assert.IsType<AttributedFailure>(read.Failure);
        Assert.Equal(first, Assert.Single(failure.FailingTests));
        Assert.Equal(flake, Assert.Single(read.DroppedFlakeIdentities));
        Assert.Equal($"reproduced {first}: exact first line (trx: {original.Path})", failure.ReproductionDetail);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task AllFlakesRetainDiagnostic()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce();
        var artifact = fixture.UseCollisionEvidence();
        var rerun = fixture.WriteLiteralTrx("flake-rerun.trx", """
            <TestRun id="flake-rerun">
              <TestDefinitions><UnitTest id="f"><TestMethod className="Sample.Tests.SoloTests" name="Fails" /></UnitTest></TestDefinitions>
              <Results><UnitTestResult testId="f" testName="Fails" outcome="Passed" /></Results>
            </TestRun>
            """);
        fixture.ReplaceProof(proof => proof with { Checks = proof.Checks.Select(item => item with
            { Artifacts = [artifact, rerun] }).ToArray() });
        fixture.Complete();

        var read = SoloAcceptanceFailureReceiptReader.Read(fixture.Goal, fixture.Context);

        Assert.Null(read.Failure);
        Assert.Equal("solo-evidence-invalid: InvalidDataException: solo-repeated-execution-flake: " +
            $"identity={SoloAttributedFailureRouteFixture.First}; outcome=flake; outcomes=Failed,Passed", read.Reason);
        Assert.Empty(read.DroppedFlakeIdentities);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task DuplicateArtifactRetainsDiagnostic()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce();
        var artifact = fixture.UseCollisionEvidence();
        fixture.ReplaceProof(proof => proof with { Checks = proof.Checks.Select(item => item with
            { Artifacts = [artifact, artifact] }).ToArray() });
        fixture.Complete();

        var read = SoloAcceptanceFailureReceiptReader.Read(fixture.Goal, fixture.Context);

        Assert.Null(read.Failure);
        Assert.Equal("solo-evidence-invalid: InvalidDataException: solo-duplicate-test-row: " +
            $"identity={SoloAttributedFailureRouteFixture.First}; outcomes=Failed,Failed", read.Reason);
        Assert.Empty(read.DroppedFlakeIdentities);
    }
}
