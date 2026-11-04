using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static AttributedFailureDeveloperRouteTests;

public sealed class SoloAttributedFailureRouteControlTests
{
    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task BuildFailure_WithoutTestIdentities_RetriesCriterion()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce(buildFails: true);
        var summary = fixture.Summary;
        Assert.False(summary.RequiresSoloFailureReceipt);
        Assert.Null(summary.SoloFailureReceipt);
        Assert.NotEmpty(summary.RequiredUnmetCriteria);
        Assert.All(summary.RequiredUnmetCriteria, check =>
            Assert.Empty(check.FailingTestIdentities ?? []));
        Assert.Contains(summary.RequiredUnmetCriteria, check =>
            check.OutputTail?.Contains("error CS1002", StringComparison.Ordinal) == true);

        var driver = fixture.Driver();
        driver.BeginTick(fixture.Kernel, 1);
        var candidate = ConductorParallelAcceptanceCandidate.Create(fixture.Goal, 0, [],
            fixture.Context.CandidateSha, fixture.Context.MainSha);
        var result = driver.CompleteParallelLandingAcceptance(candidate,
            ConductorAutonomyPolicy.Permissive, summary, out var leaseHeld);

        Assert.False(leaseHeld);
        var executed = Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.NotEqual("solo-acceptance-correction", executed.Decision?.Stage);
        var task = fixture.Goal.Tasks.Single();
        Assert.Equal(AgentRole.Developer, task.RequiredRole);
        Assert.Equal(RetryCause.CriterionEvidenceOwnerMismatch, task.PendingRetryCause);
        Assert.Single(fixture.Goal.Timeline.Where(evt => evt.Kind == ProgressKind.TaskRetried));
        Assert.Equal(1, fixture.Goal.AutomaticAcceptanceRetryCount);
        Assert.Empty(MarkerRetries(fixture.Goal));
        Assert.Empty(fixture.Starts);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public async Task ThrowingBaseline_ReloadAndRestart_HoldsWithoutRetry()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce(baselineThrows: true);
        Assert.True(fixture.Summary.RequiresSoloFailureReceipt);
        Assert.Null(fixture.Summary.SoloFailureReceipt);
        var attributions = fixture.Summary.UnmetCriteria.SelectMany(check => check.FailingTestAttributions ?? []).ToArray();
        Assert.NotEmpty(attributions);
        Assert.All(attributions, attribution =>
            Assert.Contains("focused baseline execution failed: AcceptanceGateEngineException", attribution.Evidence));

        var artifact = new ConductorParallelAcceptanceRunArtifact("accepted", null, null, null, null,
            fixture.Summary, fixture.Context.CandidateSha, fixture.Context.MainSha, null, null);
        var resultPath = Path.Combine(fixture.Root, "acceptance-result.json");
        File.WriteAllBytes(resultPath, JsonSerializer.SerializeToUtf8Bytes(artifact));
        var saved = JsonSerializer.Deserialize<ConductorParallelAcceptanceRunArtifact>(File.ReadAllBytes(resultPath))!;
        Assert.True(saved.Acceptance!.RequiresSoloFailureReceipt);
        Assert.Null(saved.Acceptance.SoloFailureReceipt);
        fixture.Complete(saved.Acceptance);
        await fixture.Restart();
        var driver = fixture.Driver();
        driver.BeginTick(fixture.Kernel, 2);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(
            driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive).Outcome);
        Assert.Contains("solo-receipt-absent", held.Reason);
        Assert.Equal("solo-acceptance-correction", Assert.IsType<PolicyDecisionRecord>(held.Decision).Stage);
        Assert.Empty(fixture.Starts);
        Assert.Empty(MarkerRetries(fixture.Goal));
        Assert.Equal(0, fixture.Goal.AutomaticAcceptanceRetryCount);
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData("absent", "solo-receipt-absent")]
    [InlineData("missing", "FileNotFoundException")]
    [InlineData("unreadable", "solo-evidence-invalid")]
    [InlineData("malformed", "JsonException")]
    [InlineData("malformed-trx", "XmlException")]
    [InlineData("foreign", "solo-foreign-artifact")]
    [InlineData("digest", "solo-artifact-digest-mismatch")]
    [InlineData("goal", "solo-goal-or-attempt-mismatch")]
    [InlineData("attempt", "solo-goal-or-attempt-mismatch")]
    [InlineData("candidate", "solo-revision-mismatch")]
    [InlineData("main", "solo-revision-mismatch")]
    [InlineData("baseline", "solo-revision-mismatch")]
    [InlineData("tree", "solo-revision-mismatch")]
    [InlineData("current-candidate", "solo-revision-mismatch")]
    [InlineData("current-main", "solo-revision-mismatch")]
    [InlineData("checks", "solo-check-mismatch")]
    [InlineData("tests", "solo-failing-tests-mismatch")]
    [InlineData("unknown", "solo-candidate-not-introduced")]
    [InlineData("inherited", "solo-candidate-not-introduced")]
    [InlineData("unconfirmed", "solo-candidate-not-introduced")]
    [InlineData("classified", "solo-authoritative-attribution-mismatch")]
    [InlineData("apparatus", "solo-candidate-not-introduced")]
    [InlineData("rerun-unavailable", "solo-candidate-rerun-unconfirmed")]
    [InlineData("baseline-uncovered", "solo-incomplete-check-rows")]
    public async Task InvalidEvidenceHoldsWithoutRetryOrDispatch(string fault, string reason)
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce();
        var summary = fixture.Summary;
        var receipt = summary.SoloFailureReceipt!;
        switch (fault)
        {
            case "absent": summary = summary with { SoloFailureReceipt = null }; break;
            case "missing": File.Delete(receipt.Evidence.Path); break;
            case "unreadable":
                Directory.CreateDirectory(Path.Combine(receipt.ResultsRoot, "unreadable.json"));
                summary = summary with { SoloFailureReceipt = receipt with
                    { Evidence = receipt.Evidence with { Path = Path.Combine(receipt.ResultsRoot, "unreadable.json") } } };
                break;
            case "malformed":
                File.WriteAllText(receipt.Evidence.Path, "{");
                summary = summary with { SoloFailureReceipt = receipt with { Evidence = receipt.Evidence with
                    { Sha256 = SoloAcceptanceFailureEvidenceWriter.Digest(File.ReadAllBytes(receipt.Evidence.Path)) } } };
                break;
            case "foreign": summary = summary with { SoloFailureReceipt = receipt with
                { Evidence = receipt.Evidence with { Path = Path.Combine(fixture.Root, "foreign.json") } } }; break;
            case "digest": File.AppendAllText(receipt.Evidence.Path, " "); break;
            case "goal": summary = summary with { SoloFailureReceipt = receipt with { GoalId = GoalId.New().Value } }; break;
            case "attempt": summary = summary with { AttemptId = "foreign-attempt" }; break;
            case "candidate": summary = summary with { BranchHeadSha = "another-candidate" }; break;
            case "main": summary = summary with { MainHeadSha = "another-main" }; break;
            case "baseline": summary = summary with { SoloFailureReceipt = receipt with { BaselineSha = "another-baseline" } }; break;
            case "tree": summary = summary with { SoloFailureReceipt = receipt with { CandidateTreeSha = "another-tree" } }; break;
            case "checks": summary = summary with { FailedChecks = ["foreign check"] }; break;
            case "classified": summary = summary with { CheckAttributions = summary.CheckAttributions!.Select(attr =>
                attr with { Cause = AcceptanceFailureCause.FixturePublication }).ToArray() }; break;
            case "current-candidate": case "current-main": break;
            case "malformed-trx":
                fixture.ReplaceProof(proof => proof with { Checks = proof.Checks.Select(item =>
                {
                    var artifact = item.Artifacts[0];
                    File.WriteAllText(artifact.Path, "not XML");
                    return item with { Artifacts = [artifact with
                        { Sha256 = SoloAcceptanceFailureEvidenceWriter.Digest(File.ReadAllBytes(artifact.Path)) }] };
                }).ToArray() });
                summary = fixture.Summary;
                break;
            default:
                fixture.ReplaceProof(proof => proof with
                {
                    Checks = proof.Checks.Select(item => item with { Check = fault switch
                    {
                        "tests" => item.Check with { FailingTestIdentities = ["Foreign.Tests.Fails"],
                            FailingTestAttributions = [new("Foreign.Tests.Fails", AcceptanceTestFailureOrigin.Introduced, "untrusted")] },
                        "unknown" => item.Check with { FailingTestAttributions = item.Check.FailingTestAttributions!
                            .Select(attr => attr with { Origin = AcceptanceTestFailureOrigin.Unattributed }).ToArray() },
                        "inherited" => item.Check with { FailingTestAttributions = item.Check.FailingTestAttributions!
                            .Select(attr => attr with { Origin = AcceptanceTestFailureOrigin.Inherited }).ToArray() },
                        "unconfirmed" => item.Check with { FailingTestAttributions = item.Check.FailingTestAttributions!
                            .Select(attr => attr with { Origin = AcceptanceTestFailureOrigin.UnconfirmedIntroduced }).ToArray() },
                        "apparatus" => item.Check with { FailureClassification = AcceptanceFailureClassifications.FocusedSelectionApparatusFailure },
                        "rerun-unavailable" => item.Check with { FailingTestAttributions = item.Check.FailingTestAttributions!
                            .Select(attr => attr with { CandidateRerun = new("Unavailable", null, "executor unavailable") }).ToArray() },
                        _ => item.Check
                    } }).ToArray(),
                    BaselineChecks = fault == "baseline-uncovered" ? proof.BaselineChecks.Select(item => item with
                        { Check = item.Check with { ExecutedTestCount = 99 } }).ToArray() : proof.BaselineChecks,
                    Baseline = fault == "baseline-uncovered" ? proof.Baseline with { Checks = proof.Baseline.Checks.Select(check =>
                        check with { ExecutedTestCount = 99 }).ToArray() } : proof.Baseline
                });
                summary = fixture.Summary;
                break;
        }
        fixture.Complete(summary);
        await fixture.Restart();
        var driver = fixture.Driver();
        if (fault == "current-candidate") driver.SoloAcceptanceContextReader = _ => fixture.Context with { CandidateSha = "changed" };
        if (fault == "current-main") driver.SoloAcceptanceContextReader = _ => fixture.Context with { MainSha = "changed" };
        driver.BeginTick(fixture.Kernel, 2);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive).Outcome);
        Assert.Contains(reason, held.Reason);
        Assert.Contains("evidence=", held.Reason);
        var decision = Assert.IsType<PolicyDecisionRecord>(held.Decision);
        Assert.Equal("solo-acceptance-correction", decision.Stage);
        Assert.Equal("Hold", decision.Action);
        Assert.Contains(reason, decision.DiscriminatingEvidence);
        Assert.Equal(held.Reason, decision.Reason);
        Assert.Contains(new PolicyDecisionFact("goalId", fixture.Goal.Id.Value), decision.Facts);
        Assert.Contains(new PolicyDecisionFact("attemptId", summary.AttemptId!), decision.Facts);
        Assert.Contains(new PolicyDecisionFact("evidencePath", summary.SoloFailureReceipt?.Evidence.Path ?? "absent"), decision.Facts);
        Assert.Empty(fixture.Starts);
        Assert.Empty(MarkerRetries(fixture.Goal));
        Assert.Equal(0, fixture.Goal.AutomaticAcceptanceRetryCount);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task NoCommittedDeveloperHoldsValidatedEvidence()
    {
        using var fixture = new SoloAttributedFailureRouteFixture(committed: false);
        await fixture.Produce();
        fixture.Complete();
        var driver = fixture.Driver();
        driver.BeginTick(fixture.Kernel, 2);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive).Outcome);
        Assert.Contains("no-committed-developer", held.Reason);
        Assert.Empty(fixture.Starts);
        Assert.Equal(0, fixture.Goal.AutomaticAcceptanceRetryCount);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public async Task ActiveWorkerDoesNotConsumeReceiptAndLaterTickRevalidatesIt()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce();
        fixture.Complete();
        var task = fixture.Goal.Tasks.Single();
        var dispatch = task.LastDispatch!;
        fixture.Kernel.RecordTaskProcessRefreshed(fixture.Goal.Id, task.Id,
            new(12345, dispatch.Command, dispatch.WorkingDirectory, "stdout", "stderr", "exit", DateTimeOffset.UtcNow, null, null), null);
        var driver = fixture.Driver();
        driver.BeginTick(fixture.Kernel, 2);
        driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive);
        Assert.Empty(fixture.Starts);
        Assert.Empty(MarkerRetries(fixture.Goal));
        Assert.Equal(0, fixture.Goal.AutomaticAcceptanceRetryCount);
        Assert.Equal(GoalStatus.AcceptanceFailed, fixture.Goal.Status);
        var process = task.LastProcess!;
        fixture.Kernel.RecordTaskProcessRefreshed(fixture.Goal.Id, task.Id, process with
            { CompletedAt = DateTimeOffset.UtcNow, ExitCode = 0 }, null);
        driver.BeginTick(fixture.Kernel, 3);
        driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive);
        Assert.Single(fixture.Starts);
        Assert.Single(MarkerRetries(fixture.Goal));
    }
}
