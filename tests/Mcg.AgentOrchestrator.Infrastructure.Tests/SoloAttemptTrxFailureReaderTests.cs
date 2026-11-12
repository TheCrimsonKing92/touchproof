using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: in-memory kernel and exclusively owned, hand-written result artifacts.
public sealed class SoloAttemptTrxFailureReaderTests
{
    [Fact]
    public void EffectiveFailedCheck_ExcludesPassedLaneAndFlakedRows()
    {
        using var fixture = new SoloCorrectionTestFixture();
        fixture.Record();
        fixture.Trx("Tests.FlakedInFailedLane", "Tests.Fails");
        File.Copy(Path.Combine(fixture.Root, "failed.trx"), Path.Combine(fixture.Root, "first.trx"));
        fixture.Trx("Tests.FlakedInPassedLane");
        File.Copy(Path.Combine(fixture.Root, "failed.trx"), Path.Combine(fixture.Root, "passed.trx"));
        fixture.Trx("Tests.Fails");
        fixture.Attempt = fixture.Attempt! with { TestResultPaths = ["first.trx", "passed.trx", "failed.trx"] };
        fixture.SaveResult(
            new("solo tests", false, 1, "failed", TestResultPaths: ["first.trx", "failed.trx"],
                FailingTestIdentities: ["Tests.Fails"]),
            new("passed lane", true, 0, "rerun passed", TestResultPaths: ["passed.trx"]));

        Assert.IsType<ConductorAdvanceOutcome.Executed>(fixture.Route().Outcome);
        var feedback = Assert.Single(fixture.Routes).Feedback!;
        Assert.Contains("failing_tests=Tests.Fails" + Environment.NewLine, feedback);
        Assert.Contains("failed Tests.Fails: message", feedback);
        Assert.DoesNotContain("Flaked", feedback);
        Assert.Equal(1, fixture.RetryCalls);
    }

    [Theory]
    [InlineData("absent-result")]
    [InlineData("malformed-result")]
    [InlineData("missing-check")]
    [InlineData("missing-identities")]
    [InlineData("uncovered-identity")]
    [InlineData("inherited-test")]
    public void UnusableFailedCheckEvidence_DoesNotAuthorizeRoute(string fault)
    {
        using var fixture = new SoloCorrectionTestFixture();
        fixture.Record();
        fixture.Trx("Tests.Fails");
        switch (fault)
        {
            case "absent-result": File.Delete(fixture.Attempt!.ResultPath); break;
            case "malformed-result": File.WriteAllText(fixture.Attempt!.ResultPath, "not json"); break;
            case "missing-check": fixture.SaveResult(new AcceptanceCheckResult("other", false, 1, "failed")); break;
            case "missing-identities":
                fixture.SaveResult(new AcceptanceCheckResult("solo tests", false, 1, "failed", TestResultPaths: ["failed.trx"])); break;
            case "uncovered-identity":
                fixture.SaveResult(new AcceptanceCheckResult("solo tests", false, 1, "failed", TestResultPaths: ["failed.trx"],
                    FailingTestIdentities: ["Tests.Fails", "Tests.NotInTrx"])); break;
            case "inherited-test":
                fixture.SaveResult(new AcceptanceCheckResult("solo tests", false, 1, "failed", TestResultPaths: ["failed.trx"],
                    FailingTestIdentities: ["Tests.Fails"],
                    FailingTestAttributions: [new("Tests.Fails", AcceptanceTestFailureOrigin.Inherited, "baseline red")])); break;
        }
        Assert.Null(SoloAttemptTrxFailureReader.Read(fixture.Goal, fixture.Attempt));
    }

    [Fact]
    public void Driver_RegisteredAttemptTrx_RoutesOneDeveloperCorrection()
    {
        using var fixture = new SoloCorrectionTestFixture();
        fixture.Record();
        fixture.Trx("Tests.Fails");
        var attemptsRoot = Path.Combine(fixture.Root, "attempts");
        var directory = Path.Combine(attemptsRoot, fixture.Goal.Id.Value);
        Directory.CreateDirectory(directory);
        // Canonical metadata moves under the coordinator; TRX paths remain explicit.
        fixture.SaveResult(new AcceptanceCheckResult("solo tests", false, 1, "failed",
            TestResultPaths: [Path.Combine(fixture.Root, "failed.trx")], FailingTestIdentities: ["Tests.Fails"]));
        var attempt = fixture.Attempt! with { MetadataPath = Path.Combine(directory, "attempt.attempt.json") };
        File.WriteAllBytes(attempt.MetadataPath, JsonSerializer.SerializeToUtf8Bytes(attempt,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var starts = 0;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(attemptsRoot, TrunkBranchName.Default);
        var driver = new ConductorDriver(
            getFacts: _ => new(WorkspaceExists: true), getRunningPaidWorkerCount: () => 0,
            createWorkspace: _ => throw new InvalidOperationException("Unexpected workspace creation"),
            dispatchAndStart: _ => { starts++; return DispatchStartOutcome.Started(); },
            startRecordedDispatches: null, buildServerShutdown: null,
            runAcceptanceVerification: _ => throw new InvalidOperationException("Unexpected real gate"),
            runAdvisorySemanticAcceptance: null, retryTask: null, recordTaskNote: null,
            recordCriterionRetryFeedback: fixture.Kernel.RecordCriterionRetryFeedback,
            clearCriterionRetryFeedback: null,
            rebaseOntoMain: _ => throw new InvalidOperationException("Unexpected git"),
            land: (_, _) => throw new InvalidOperationException("Unexpected landing"),
            afterSuccessfulLanding: null, record: _ => { },
            cleanup: _ => throw new InvalidOperationException("Unexpected cleanup"),
            writeEscalation: (_, _, reason) => throw new InvalidOperationException(reason),
            classifyChangeRisk: _ => null,
            parallelAcceptanceAttemptCoordinator: coordinator,
            getPreReviewEvidenceContext: _ => ConductorDriverTests.NoPreReviewContext(fixture.Context.CandidateSha),
            retryTaskWithCause: (goal, task, feedback, round, cause) =>
                fixture.Kernel.RetryTaskAutomatically(goal, task, feedback, cause, retryRoundKind: round));
        driver.SoloAcceptanceContextReader = _ => fixture.Context;
        Assert.Equal(attempt.AttemptId, coordinator.TryReadLatest(fixture.Goal.Id.Value)?.AttemptId);
        driver.BeginTick(fixture.Kernel, 1);
        Assert.IsType<ConductorAdvanceOutcome.Executed>(driver.AdvanceOnce(fixture.Goal,
            fixture.Policy with { AutomaticRetryOperatorRoutingHoldSeconds = 0 }).Outcome);
        var retry = Assert.Single(AttributedFailureDeveloperRouteTests.MarkerRetries(fixture.Goal));
        Assert.Contains("failing_tests=Tests.Fails", retry.Message);
        Assert.Contains("failed Tests.Fails: message 0", retry.Message);
        Assert.Equal(1, starts);
        Assert.Equal(1, fixture.Goal.AutomaticAcceptanceRetryCount);
    }
}
