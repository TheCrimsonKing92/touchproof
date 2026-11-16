using System.Xml.Linq;

public sealed class ProspectiveEvidenceGateResolverRerunSupersessionTests
{
    [Theory]
    [InlineData("RerunTests")]
    [InlineData("RerunTests.Check")]
    public void PassingRerunInSecondTrxResolvesWaitWithSupersededCount(string selector)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var wait = fixture.CreateWait([selector]);
        var rerunPath = WriteRerun(fixture.Root, "Passed");
        var (attemptId, trxPath, _) = fixture.WriteAttempt(
            [("RerunTests.Check", "Failed")], additionalPaths: [rerunPath]);

        var result = fixture.Resolve();

        Assert.Equal([wait.Id], result.ResolvedRequestIds);
        Assert.Empty(result.UnresolvedWaits);
        Assert.True(wait.IsCompleted);
        Assert.Equal($"Satisfied by gate attempt {attemptId}; TRX {trxPath}, {rerunPath}; " +
            "1 named tests passed; 1 superseded by rerun.", wait.Answer);
    }

    [Fact]
    public void FailedRerunStaysPendingWithTestNamed()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var wait = fixture.CreateWait(["RerunTests.Check"]);
        var rerunPath = WriteRerun(fixture.Root, "Failed");
        fixture.WriteAttempt([("RerunTests.Check", "Failed")], additionalPaths: [rerunPath]);

        var result = fixture.Resolve();

        Assert.Empty(result.ResolvedRequestIds);
        var unresolved = Assert.Single(result.UnresolvedWaits);
        Assert.Equal(wait.Id, unresolved.RequestId);
        Assert.Equal(["RerunTests.Check"], unresolved.MissingOrFailedTestNames);
        Assert.Null(unresolved.SubmissionFailure);
        Assert.False(wait.IsCompleted);
        Assert.Null(wait.Answer);
    }

    [Fact]
    public void PassingAttemptWithoutSupersessionKeepsExactAnswer()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var wait = fixture.CreateWait(["RerunTests.Check"]);
        var (attemptId, trxPath, _) = fixture.WriteAttempt([("RerunTests.Check", "Passed")]);

        var result = fixture.Resolve();

        Assert.Equal([wait.Id], result.ResolvedRequestIds);
        Assert.Empty(result.UnresolvedWaits);
        Assert.True(wait.IsCompleted);
        Assert.Equal($"Satisfied by gate attempt {attemptId}; TRX {trxPath}; 1 named tests passed.", wait.Answer);
        Assert.DoesNotContain("superseded by rerun", wait.Answer, StringComparison.Ordinal);
    }

    private static string WriteRerun(string root, string outcome)
    {
        var path = Path.Combine(root, "rerun.trx");
        new XDocument(new XElement("TestRun", new XElement("Results",
            new XElement("UnitTestResult", new XAttribute("testName", "RerunTests.Check"),
                new XAttribute("outcome", outcome))))).Save(path);
        return path;
    }
}
