using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each test owns its temp root, repository, data root and faked evidence runner.
public sealed class DeferredNoChangeContextDeclarationPrecedenceTests
{
    [Fact]
    public void ClassOrderEquivalentDeclarationsYieldOneReceiptBodyPerCanonicalAttempt()
    {
        using var fixture = new DeferredNoChangeContextFixture();
        fixture.Complete();
        var firstId = fixture.Attempt.AttemptId;
        File.WriteAllText(Path.Combine(fixture.Worktree, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests",
            "DeferredBetaTests.cs"), "public class DeferredBetaTests {}\n");
        Declare(fixture, "DeferredBetaTests", "DeferredAlphaTests");
        Declare(fixture, "DeferredAlphaTests", "DeferredBetaTests");
        fixture.Complete();
        var secondId = fixture.Attempt.AttemptId;
        Assert.NotEqual(firstId, secondId);
        Declare(fixture, "DeferredAlphaTests", "DeferredAlphaTests", "DeferredBetaTests");

        var context = fixture.Capture();

        Assert.Equal(2, context.Receipts.Count);
        var first = Assert.Single(context.Receipts, receipt => receipt.AttemptId == firstId);
        var second = Assert.Single(context.Receipts, receipt => receipt.AttemptId == secondId);
        Assert.False(first.IsCurrent);
        Assert.True(second.IsCurrent);
        Assert.Equal(DeferredNoChangeEvidenceAvailability.Green, first.Availability);
        Assert.Equal(DeferredNoChangeEvidenceAvailability.Green, second.Availability);
        Assert.Equal(["DeferredAlphaTests"], first.Declared);
        Assert.Equal(["DeferredBetaTests", "DeferredAlphaTests"], second.Declared);
        var package = fixture.Package(context: context);
        var bodies = package.Artifacts.Where(artifact => artifact.Identity.Value.StartsWith(
            "goal/deferred-no-change-receipts/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, bodies.Length);
        foreach (var receipt in context.Receipts)
        {
            var body = Assert.Single(bodies, artifact => JsonSerializer.Deserialize<JsonElement>(
                artifact.AuthoritativeBytes!).GetProperty("AttemptId").GetString() == receipt.AttemptId);
            Assert.Equal(Encoding.UTF8.GetBytes(receipt.BodyJson!), body.AuthoritativeBytes);
            Assert.Equal(WorkerContextArtifact.Hash(body.AuthoritativeBytes!), body.ContentHash);
            Assert.Equal(receipt.IsCurrent ? ContextDeliveryMode.MandatoryFile : ContextDeliveryMode.HistoricalFile,
                body.DeliveryMode);
        }
    }

    [Theory]
    [InlineData(AgentRole.Tester)]
    [InlineData(AgentRole.Reviewer)]
    public void SupersededUnmarkedDeclarationDoesNotHideLaterGreenReceipt(AgentRole role)
    {
        using var fixture = new DeferredNoChangeContextFixture();
        SupersedeUnmarkedDeclaration(fixture);
        fixture.Complete();
        var attemptId = fixture.Attempt.AttemptId;
        fixture.Kernel.RecordTaskVerification(fixture.Goal.Id, fixture.TesterA.Id,
            new("review", fixture.Worktree, 1, "open evidence requests", "", DateTimeOffset.UtcNow,
                ReviewedCommit: fixture.Candidate, MergedReviewFindings:
                [Finding("covered", "DeferredAlphaTests"), Finding("outstanding", "AdditionalTests")]));
        var task = fixture.Goal.Tasks.First(task => task.RequiredRole == role);
        var history = JsonSerializer.SerializeToUtf8Bytes(task.VerificationHistory);
        var context = fixture.Capture();

        var current = Assert.Single(context.Receipts, receipt => receipt.IsCurrent);
        Assert.Equal(attemptId, current.AttemptId);
        Assert.Equal(DeferredNoChangeEvidenceAvailability.Green, current.Availability);
        var superseded = Assert.Single(context.Receipts, receipt => receipt.AttemptId is null);
        Assert.False(superseded.IsCurrent);
        Assert.Equal(["MissingDeclarationTests"], superseded.Declared);
        Assert.Equal(DeferredNoChangeEvidenceAvailability.Unavailable, superseded.Availability);
        Assert.Equal("deferred-evidence-locator-unavailable", superseded.Diagnostic);
        var package = fixture.Package(task, context);
        var summary = DeferredNoChangeContextFixture.Summary(package);
        Assert.True(summary.GetProperty("Receipts")[0].GetProperty("CurrentGreenProof").GetBoolean());
        Assert.Equal("Infrastructure.Tests:DeferredAlphaTests",
            Assert.Single(summary.GetProperty("CoveredSelections").EnumerateArray()).GetString());
        var requirements = summary.GetProperty("OutstandingRequirements").EnumerateArray()
            .ToDictionary(row => row.GetProperty("StableId").GetString()!);
        Assert.Equal("Infrastructure.Tests:DeferredAlphaTests",
            Assert.Single(requirements["covered"].GetProperty("CoveredSelections").EnumerateArray()).GetString());
        Assert.Empty(requirements["covered"].GetProperty("OutstandingSelections").EnumerateArray());
        Assert.Equal("Infrastructure.Tests:AdditionalTests",
            Assert.Single(requirements["outstanding"].GetProperty("OutstandingSelections").EnumerateArray()).GetString());
        Assert.Empty(requirements["outstanding"].GetProperty("CoveredSelections").EnumerateArray());
        Assert.Equal(history, JsonSerializer.SerializeToUtf8Bytes(task.VerificationHistory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentUnmatchedDeclarationStaysCurrentAndFailsClosed(bool olderGreen)
    {
        using var fixture = new DeferredNoChangeContextFixture();
        string? greenId = null;
        if (olderGreen)
        {
            fixture.Complete();
            greenId = fixture.Attempt.AttemptId;
            Declare(fixture, "MissingDeclarationTests");
        }

        var context = fixture.Capture();

        var current = Assert.Single(context.Receipts, receipt => receipt.IsCurrent);
        Assert.Null(current.AttemptId);
        Assert.Equal(DeferredNoChangeEvidenceAvailability.Unavailable, current.Availability);
        Assert.Equal("deferred-evidence-locator-unavailable", current.Diagnostic);
        Assert.Equal([olderGreen ? "MissingDeclarationTests" : "DeferredAlphaTests"], current.Declared);
        if (olderGreen)
        {
            var green = Assert.Single(context.Receipts, receipt => receipt.AttemptId == greenId);
            Assert.False(green.IsCurrent);
            Assert.Equal(DeferredNoChangeEvidenceAvailability.Green, green.Availability);
        }
        else Assert.Single(context.Receipts);
        AssertNoGreenCoverage(fixture, context);
    }

    [Fact]
    public void LaterMalformedLocatorMarkerStaysCurrentUnavailable()
    {
        using var fixture = new DeferredNoChangeContextFixture();
        SupersedeUnmarkedDeclaration(fixture);
        fixture.Complete();
        var greenId = fixture.Attempt.AttemptId;
        fixture.Kernel.RecordFindingEvidenceRun(fixture.Goal.Id, fixture.TesterA.Id,
            "finding-evidence deferred-no-change outcome=green; task_id=duplicate; task_id=duplicate");

        var context = fixture.Capture();

        var current = Assert.Single(context.Receipts, receipt => receipt.IsCurrent);
        Assert.Equal(DeferredNoChangeEvidenceAvailability.Unavailable, current.Availability);
        Assert.Equal("deferred-evidence-locator-malformed", current.Diagnostic);
        Assert.Contains(context.Receipts, receipt => receipt.AttemptId == greenId && !receipt.IsCurrent &&
            receipt.Availability == DeferredNoChangeEvidenceAvailability.Green);
        AssertNoGreenCoverage(fixture, context);
    }

    [Fact]
    public void LaterRedAttemptStaysCurrentAndOlderGreenStaysHistorical()
    {
        using var fixture = new DeferredNoChangeContextFixture();
        SupersedeUnmarkedDeclaration(fixture);
        fixture.Complete();
        var old = fixture.Attempt;
        var oldMarker = fixture.Locator;
        var newerId = old.AttemptId + "-red";
        var newer = old with { AttemptId = newerId, Ordinal = old.Ordinal + 1,
            MetadataPath = Path.Combine(fixture.GoalStore, newerId + ".attempt.json"),
            ResultPath = Path.Combine(fixture.GoalStore, newerId + ".result.json"),
            Outcome = ConductorParallelAcceptanceAttemptOutcome.Failed };
        fixture.WriteAttempt(newer);
        var source = fixture.Result;
        var evidence = source.FocusedEvidence!;
        var candidate = evidence.Arms![0];
        var trxPath = candidate.Checks[0].TestResultPaths![0];
        var redTrx = Path.Combine(fixture.GoalStore, "new-red.trx");
        File.WriteAllText(redTrx, File.ReadAllText(trxPath).Replace("outcome=\"Passed\"", "outcome=\"Failed\"")
            .Replace("passed=\"1\"", "passed=\"0\"").Replace("failed=\"0\"", "failed=\"1\""));
        var check = candidate.Checks[0] with { Passed = false, TestResultPaths = [redTrx],
            FailingTestIdentities = ["DeferredAlphaTests.Runs"] };
        var red = source with { FocusedEvidence = evidence with { Passed = false, Checks = [check], Arms =
            [candidate with { Passed = false, Disposition = FindingEvidenceArmDisposition.Red, Checks = [check] }] } };
        File.WriteAllText(newer.ResultPath, JsonSerializer.Serialize(red, DeferredNoChangeContextFixture.JsonOptions));
        fixture.Mark(oldMarker with { Outcome = "red", ResultPath = newer.ResultPath });
        fixture.Mark(oldMarker);

        var context = fixture.Capture();

        var current = Assert.Single(context.Receipts, receipt => receipt.IsCurrent);
        Assert.Equal(newerId, current.AttemptId);
        Assert.Equal(DeferredNoChangeEvidenceAvailability.Red, current.Availability);
        Assert.NotNull(current.BodyJson);
        Assert.Contains(context.Receipts, receipt => receipt.AttemptId == old.AttemptId && !receipt.IsCurrent &&
            receipt.Availability == DeferredNoChangeEvidenceAvailability.Green);
        AssertNoGreenCoverage(fixture, context);
    }

    private static void Declare(DeferredNoChangeContextFixture fixture, params string[] classes)
    {
        var original = fixture.Developer.LastVerification!;
        Assert.True(DeferredNoChangeOutcome.TryParse(original.StandardError, out var previous));
        fixture.Kernel.RecordTaskVerification(fixture.Goal.Id, fixture.Developer.Id, original with
        {
            StandardOutput = original.StandardOutput.Replace("tests: deferred - " + string.Join(", ", previous.TestClasses),
                "tests: deferred - " + string.Join(", ", classes)),
            StandardError = new DeferredNoChangeOutcome(fixture.Candidate, classes, "NO_CHANGE: fixture declaration").FormatMarker(),
            CompletedAt = original.CompletedAt.AddSeconds(1)
        });
        Assert.Equal("deferred-no-change-round", fixture.Developer.LastVerification!.CompletionVerdictRule);
    }

    private static void SupersedeUnmarkedDeclaration(DeferredNoChangeContextFixture fixture)
    {
        Declare(fixture, "MissingDeclarationTests");
        Declare(fixture, "DeferredAlphaTests");
    }

    private static ReviewFinding Finding(string id, string testClass) => new(id, ReviewFindingState.Open,
        new("tests/DeferredAlphaTests.cs", "DeferredAlphaTests"), "Evidence remains open",
        Category: FindingCategory.TestCoverage, EvidenceRequest: new([new("Infrastructure.Tests", testClass)]));

    private static void AssertNoGreenCoverage(DeferredNoChangeContextFixture fixture, DeferredNoChangeEvidenceContext context)
    {
        var summary = DeferredNoChangeContextFixture.Summary(fixture.Package(context: context));
        Assert.Empty(summary.GetProperty("CoveredSelections").EnumerateArray());
        Assert.DoesNotContain(summary.GetProperty("Receipts").EnumerateArray(),
            receipt => receipt.GetProperty("CurrentGreenProof").GetBoolean());
    }
}
