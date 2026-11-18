using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;
using GitFixture = GoalRepositoryWorktreeProvisionerFixture;

// Parallel-safe: repositories, registry, stores and attempt artifacts belong to each fixture.
// Git is real; the acceptance verifier is a recording seam and launches no test processes.
public sealed class KeyedRepositoryEvidenceExecutionTests
{
    private const int CrossTickHangFailsafeMilliseconds = 30_000;

    [Fact]
    public void Resolve_DeclaredRepository_BindsWorktreeHomeBranchAndHead()
    {
        using var fixture = new Fixture();
        var (target, failure) = KeyedRepositoryEvidenceTarget.TryResolve(fixture.Goal, "Crosswalk", fixture.Registry);
        Assert.NotNull(target);
        Assert.Equal(string.Empty, failure);
        Assert.Equal("crosswalk", target.Key);
        Assert.Equal(fixture.SecondaryWorktree, target.WorktreePath);
        Assert.Equal(fixture.ProjectHome, target.ProjectHomeDirectory);
        Assert.Equal("trunk", target.IntegrationBranch);
        Assert.Equal(fixture.SecondaryHead, target.CandidateHead);
        Assert.NotEqual(fixture.HomeHead, target.CandidateHead);
        Assert.NotNull(target.EngineSettings.DeclaredGoTestCheck);
    }

    [Fact]
    public void Execute_ProductionRunner_PassesTargetAndControlInputsToVerifier()
    {
        using var fixture = new Fixture();
        var verifier = new RecordingVerifier();
        var driver = new ConductorDriver(fixture.Kernel, fixture.Repositories.Workspace, verifier,
            fixture.Repositories.Agents, fixture.Repositories.Profiles,
            cleanupHooks: new GoalWorktreeCleanupHooks { BuildStorageRoot = new(Path.Combine(fixture.Repositories.Home.Root, "builds")) });
        var target = fixture.Resolve();
        var paths = new[] { "src/record.go" };
        var result = driver.RunKeyedFocusedEvidence(fixture.Goal, target, "record:TestReadInvalidFixtures", null,
            false, CancellationToken.None, FindingEvidenceNegativeControl.RevertSrc, paths, null, null);

        Assert.True(result.Accepted);
        var call = Assert.Single(verifier.Calls);
        Assert.Equal(target.WorktreePath, call.Worktree);
        Assert.Equal(target.ProjectHomeDirectory, call.Owner.ProjectHomeDirectory);
        Assert.Equal("trunk", call.Owner.IntegrationBranch);
        Assert.Equal(target.CandidateHead, call.Owner.Identity.CandidateSha);
        Assert.NotNull(call.Owner.Settings.DeclaredGoTestCheck);
        Assert.Equal(FindingEvidenceNegativeControl.RevertSrc, call.Control);
        Assert.Equal(paths, call.RevertPaths);
        Assert.Null(call.DeclaredPaths);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("default")]
    public void Request_MixedRepositories_BindsEachReceiptAndDoesNotReplay(string? homeRepository)
    {
        using var fixture = new Fixture();
        var homeFinding = Finding("home", new([new("Sample.Tests", "HomeTests")], Repository: homeRepository));
        var keyedFinding = Finding("keyed", new([new("./record", "TestReadInvalidFixtures")], Repository: "crosswalk"));
        FailReviewerNeedsWork(fixture.Kernel, fixture.Goal, fixture.Reviewer, "Need repository evidence", findings: [homeFinding, keyedFinding]);
        var homeCalls = new List<string>();
        var keyedCalls = new List<KeyedRepositoryEvidenceTarget>();
        var receipts = new List<FindingEvidenceReceipt>();
        var driver = fixture.Driver(receipts, homeCalls);
        driver.RunKeyedFocusedEvidence = (_, target, request, _, baseline, _, control, paths, mutation, _) =>
        {
            Assert.Equal(fixture.SecondaryWorktree, target.WorktreePath);
            Assert.Equal(fixture.ProjectHome, target.ProjectHomeDirectory);
            Assert.Equal("trunk", target.IntegrationBranch);
            Assert.True(baseline);
            Assert.Null(control);
            Assert.Null(paths);
            Assert.Null(mutation);
            keyedCalls.Add(target);
            return Green(request, target.CandidateHead);
        };

        Assert.True(driver.TryBuildFindingEvidenceRequest(fixture.Goal, fixture.Reviewer, ConductorAutonomyPolicy.Permissive, out _));
        Assert.Equal("tests/Sample.Tests/Sample.Tests.csproj:HomeTests", Assert.Single(homeCalls));
        Assert.Empty(keyedCalls);
        Assert.Equal(fixture.HomeHead, Assert.Single(receipts).CandidateSha);
        Assert.Equal(homeRepository, receipts[0].Request.Repository);

        Assert.True(driver.TryBuildFindingEvidenceRequest(fixture.Goal, fixture.Reviewer, ConductorAutonomyPolicy.Permissive, out _));
        Assert.Single(homeCalls);
        Assert.Equal(fixture.SecondaryHead, Assert.Single(keyedCalls).CandidateHead);
        Assert.Equal(2, receipts.Count);
        var keyed = receipts.Single(receipt => receipt.Request.Repository == "crosswalk");
        Assert.Equal(fixture.SecondaryHead, keyed.CandidateSha);
        Assert.NotEqual(fixture.HomeHead, keyed.CandidateSha);
        Assert.All(keyed.RequestDispositions!, disposition => Assert.Equal(fixture.SecondaryHead, disposition.CandidateSha));
        Assert.All(keyed.Arms!, arm => Assert.Equal(fixture.SecondaryHead, arm.Sha));

        Assert.False(driver.TryBuildFindingEvidenceRequest(fixture.Goal, fixture.Reviewer, ConductorAutonomyPolicy.Permissive, out _));
        Assert.Single(homeCalls);
        Assert.Single(keyedCalls);
        var attempt = driver._focusedEvidenceAttemptCoordinator.TryReadLatest(fixture.Goal.Id.Value)!;
        Assert.Equal("crosswalk", attempt.FocusedEvidenceRepository);
        Assert.Equal(fixture.SecondaryHead, attempt.BranchHeadSha);
        var replay = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(JsonSerializer.Serialize(attempt))!;
        var restoredTarget = driver.ResolveAttemptEvidenceTarget(fixture.Goal, replay)!;
        Assert.Equal(fixture.SecondaryWorktree, restoredTarget.WorktreePath);
        Assert.Equal(fixture.ProjectHome, restoredTarget.ProjectHomeDirectory);
        Assert.Equal("trunk", restoredTarget.IntegrationBranch);
        Assert.Equal(fixture.SecondaryHead, restoredTarget.CandidateHead);
        driver.ResolveKeyedEvidenceTarget = (_, _) => (fixture.Resolve() with { CandidateHead = fixture.HomeHead }, string.Empty);
        Assert.Throws<InvalidOperationException>(() => driver.ResolveAttemptEvidenceTarget(fixture.Goal, replay));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Request_MixedRepositories_ClosesHomeBatchWithRepositoryExecutionProvenance(bool cappedSibling)
    {
        using var fixture = new Fixture();
        FailReviewerNeedsWork(fixture.Kernel, fixture.Goal, fixture.Reviewer, "Need repository evidence", findings:
            [Finding("home", new([new("Sample.Tests", "HomeTests")])),
             Finding("keyed", new([new("./record", "TestReadInvalidFixtures")], Repository: "crosswalk"))]);
        if (cappedSibling)
        {
            for (var retry = 0; retry < 2; retry++)
                fixture.Goal.Append(new(fixture.Goal.Id, fixture.Reviewer.Id, ProgressKind.TaskRetried,
                    $"finding evidence-on-demand: candidate_sha={fixture.HomeHead}; finding_ids=keyed;", DateTimeOffset.UtcNow));
        }
        var homeCalls = new List<string>();
        var receipts = new List<FindingEvidenceReceipt>();
        var driver = fixture.Driver(receipts, homeCalls, recordBatchAccounting: true);
        var keyedCalls = 0;
        driver.RunKeyedFocusedEvidence = (_, target, request, _, _, _, _, _, _, _) =>
        {
            keyedCalls++;
            return Green(request, target.CandidateHead);
        };
        var identity = FindingRoundBatchCoordinator.Identity(driver, fixture.Goal, fixture.Reviewer)!;

        Assert.True(driver.TryBuildFindingEvidenceRequest(fixture.Goal, fixture.Reviewer, ConductorAutonomyPolicy.Permissive, out var intermediate));
        Assert.Equal(FailedGoalFindingObservationKind.FindingEvidencePending, intermediate.Kind);
        Assert.Null(intermediate.TargetTaskId);
        Assert.Null(intermediate.BatchClosure);
        Assert.Equal(0, keyedCalls);

        Assert.True(driver.TryBuildFindingEvidenceRequest(fixture.Goal, fixture.Reviewer, ConductorAutonomyPolicy.Permissive, out var drained));
        Assert.NotEqual(FailedGoalFindingObservationKind.FindingEvidencePending, drained.Kind);
        if (cappedSibling)
            Assert.Equal(FailedGoalFindingObservationKind.FindingRetryCapReached, drained.Kind);
        else
            Assert.NotNull(drained.TargetTaskId);
        var closure = Assert.IsType<FindingEvidenceBatchClosure>(drained.BatchClosure);
        Assert.Equal(identity, closure.Identity);
        Assert.Equal(fixture.HomeHead, closure.Identity.CandidateSha);
        var summary = FindingRoundBatchAccounting.Summarize(fixture.Goal, fixture.Reviewer, identity, "test-drain");
        Assert.Equal(2, summary.BatchSize);
        Assert.Equal(2, summary.NewlyExecuted);
        Assert.True(summary.IsSettled);
        var witness = Assert.Single(summary.EligibilityWitnesses);
        Assert.Equal(AgentRole.Reviewer, witness.Role);
        Assert.Equal(1, summary.ReviewerCallsAvoided);
        Assert.Equal(0, summary.TesterCallsAvoided);
        Assert.Equal(1, summary.PaidVerificationCallsAvoided);
        Assert.Single(homeCalls);
        Assert.Equal(1, keyedCalls);
        var keyed = Assert.Single(receipts.Where(receipt => receipt.Request.Repository == "crosswalk"));
        Assert.Equal(fixture.SecondaryHead, keyed.CandidateSha);
        Assert.All(keyed.RequestDispositions!, disposition => Assert.Equal(fixture.SecondaryHead, disposition.CandidateSha));
        Assert.All(keyed.Arms!, arm => Assert.Equal(fixture.SecondaryHead, arm.Sha));
        Assert.All(FindingRoundBatchAccounting.Read(fixture.Goal, identity), item => Assert.Equal(identity.Key, item.BatchKey));
        Assert.Empty(FindingRoundBatchAccounting.Read(fixture.Goal, identity with { CandidateSha = fixture.SecondaryHead }));

        if (cappedSibling) return;

        var retries = 0;
        string? Retry()
        {
            retries++;
            fixture.Kernel.RetryTask(fixture.Goal.Id, drained.TargetTaskId!, drained.Evidence, RetryCause.NewTestFinding);
            return null;
        }
        Assert.True(FindingRoundBatchCoordinator.ApplyRetry(driver, fixture.Goal, closure, () => true, Retry).Applied);
        Assert.False(FindingRoundBatchCoordinator.ApplyRetry(driver, fixture.Goal, closure, () => true, Retry).Applied);
        Assert.Equal(1, retries);
        var restored = AgentOrchestratorKernel.FromSnapshot(fixture.Kernel.ExportSnapshot()).GetGoal(fixture.Goal.Id);
        Assert.Single(restored.Timeline.Where(item => item.Message.StartsWith("finding-evidence batch-closed;", StringComparison.Ordinal)));
        Assert.True(FindingRoundBatchAccounting.IsClosed(restored, identity));
    }

    [Theory(Timeout = CrossTickHangFailsafeMilliseconds)]
    [Trait("Category", "CrossTick")]
    [InlineData(false)]
    [InlineData(true)]
    public void KeyedBasisChange_InvalidatesClosureAndRetainsHomeEvidence(bool changeSettings)
    {
        using var fixture = new Fixture();
        FailReviewerNeedsWork(fixture.Kernel, fixture.Goal, fixture.Reviewer, "Need repository evidence", findings:
            [Finding("home", new([new("Sample.Tests", "HomeTests")])),
             Finding("keyed", new([new("./record", "TestReadInvalidFixtures")], Repository: "crosswalk"))]);
        var receipts = new List<FindingEvidenceReceipt>();
        var homeCalls = new List<string>();
        var driver = fixture.Driver(receipts, homeCalls, recordBatchAccounting: true);
        driver.RunKeyedFocusedEvidence = (_, target, request, _, _, _, _, _, _, _) => Green(request, target.CandidateHead);

        Assert.True(driver.TryBuildFindingEvidenceRequest(fixture.Goal, fixture.Reviewer, ConductorAutonomyPolicy.Permissive, out _));
        Assert.True(driver.TryBuildFindingEvidenceRequest(fixture.Goal, fixture.Reviewer, ConductorAutonomyPolicy.Permissive, out var original));
        var originalClosure = Assert.IsType<FindingEvidenceBatchClosure>(original.BatchClosure);
        Assert.True(FindingRoundBatchCoordinator.TryApply(driver, fixture.Goal, originalClosure, () => true, () => true));

        if (changeSettings)
        {
            var manifest = System.Text.Json.Nodes.JsonNode.Parse(GoPackageFocusedEvidenceTests.Manifest)!;
            manifest["engine"] = new System.Text.Json.Nodes.JsonObject { ["enforceStructuralCoverage"] = true };
            File.WriteAllText(Path.Combine(fixture.ProjectHome, "acceptance-manifest.json"), manifest.ToJsonString());
        }
        else
            GitFixture.CommitChange(fixture.SecondaryWorktree, "new secondary candidate\n");

        var current = FindingRoundBatchCoordinator.Identity(driver, fixture.Goal, fixture.Reviewer)!;
        Assert.Equal(originalClosure.Identity.CandidateSha, current.CandidateSha);
        Assert.NotEqual(originalClosure.Identity.ExecutionBasisIdentity, current.ExecutionBasisIdentity);
        Assert.False(FindingRoundBatchAccounting.IsClosed(fixture.Goal, current));
        Assert.True(driver.TryBuildFindingEvidenceRequest(fixture.Goal, fixture.Reviewer, ConductorAutonomyPolicy.Permissive, out var changed));
        Assert.Equal(current, Assert.IsType<FindingEvidenceBatchClosure>(changed.BatchClosure).Identity);
        Assert.Single(homeCalls);
        var keyed = receipts.Where(receipt => receipt.Request.Repository == "crosswalk").ToArray();
        Assert.Equal(2, keyed.Length);
        Assert.NotEqual(keyed[0].ReceiptId, keyed[1].ReceiptId);
        Assert.NotEqual(keyed[0].ExecutionBasisIdentity, keyed[1].ExecutionBasisIdentity);
        Assert.Equal(changeSettings, keyed[0].CandidateSha == keyed[1].CandidateSha);
    }

    [Fact]
    public void MissingHomeExecutor_SettlesHomeAndRunsKeyedBeforeOneRecovery()
    {
        using var fixture = new Fixture();
        FailReviewerNeedsWork(fixture.Kernel, fixture.Goal, fixture.Reviewer, "Need repository evidence", findings:
            [Finding("home", new([new("Sample.Tests", "HomeTests")])),
             Finding("keyed", new([new("./record", "TestReadInvalidFixtures")], Repository: "crosswalk"))]);
        var receipts = new List<FindingEvidenceReceipt>();
        var driver = fixture.Driver(receipts, [], recordBatchAccounting: true, configureHomeRunner: false);
        driver.RunKeyedFocusedEvidence = (_, target, request, _, _, _, _, _, _, _) => Green(request, target.CandidateHead);

        Assert.True(driver.TryBuildFindingEvidenceRequest(fixture.Goal, fixture.Reviewer, ConductorAutonomyPolicy.Permissive, out var result));
        Assert.Equal(FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired, result.Kind);
        Assert.Null(result.TargetTaskId);
        Assert.Equal("crosswalk", Assert.Single(receipts).Request.Repository);
        var closure = Assert.IsType<FindingEvidenceBatchClosure>(result.BatchClosure);
        var summary = JsonSerializer.Deserialize<FindingRoundBatchAccounting.Summary>(closure.Accounting)!;
        Assert.Equal(1, summary.Unavailable);
        Assert.Equal(1, summary.NewlyExecuted);
        Assert.Contains("No focused evidence executor was configured", summary.RoutingReason, StringComparison.Ordinal);
        Assert.True(FindingRoundBatchCoordinator.TryApply(driver, fixture.Goal, closure, () => true, () => true));
        Assert.False(FindingRoundBatchCoordinator.TryApply(driver, fixture.Goal, closure, () => true,
            () => throw new InvalidOperationException("Duplicate unavailable recovery")));
    }

    [Fact(Timeout = CrossTickHangFailsafeMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void KeyedRed_PreservesExhaustedHomeRefusal()
    {
        using var fixture = new Fixture();
        FailReviewerNeedsWork(fixture.Kernel, fixture.Goal, fixture.Reviewer, "Need repository evidence", findings:
            [Finding("home", new([new("Sample.Tests", "HomeTests")])) with { Severity = FindingSeverity.Advisory },
             Finding("keyed", new([new("./record", "TestReadInvalidFixtures")], Repository: "crosswalk"))]);
        var homeCalls = new List<string>();
        var driver = fixture.Driver([], homeCalls, recordBatchAccounting: true, runHome: request =>
            new(request, false, false, "Source discovery unavailable", [],
                Rejection: new(FocusedEvidenceRejectionCode.SourceDiscoveryFailure, request, "Source discovery unavailable")));
        driver.RunKeyedFocusedEvidence = (_, target, request, _, _, _, _, _, _, _) =>
            CandidateRedFindingEvidence(request, target.CandidateHead, "TestReadInvalidFixtures");

        for (var tick = 0; tick < FindingRoundBatchSchedule.MaximumCompletedRuns; tick++)
        {
            Assert.True(driver.TryBuildFindingEvidenceRequest(fixture.Goal, fixture.Reviewer, ConductorAutonomyPolicy.Permissive, out var interim));
            Assert.Equal(FailedGoalFindingObservationKind.FindingEvidencePending, interim.Kind);
            Assert.Null(interim.TargetTaskId);
        }
        Assert.True(driver.TryBuildFindingEvidenceRequest(fixture.Goal, fixture.Reviewer, ConductorAutonomyPolicy.Permissive, out var closed));
        Assert.Equal(FailedGoalFindingObservationKind.FindingActionableRed, closed.Kind);
        var closure = Assert.IsType<FindingEvidenceBatchClosure>(closed.BatchClosure);
        var refused = Assert.Single(fixture.Reviewer.LastVerification!.MergedReviewFindings!, finding => finding.StableId == "home");
        Assert.Equal(FindingEvidenceNotHonouredReason.SelectionApparatusFailure, refused.EvidenceOutcome?.Reason);
        Assert.DoesNotContain(FindingRoundBatchAccounting.Read(fixture.Goal, closure.Identity), observation =>
            observation.Kind == "superseded" && observation.FindingIds.Contains("home"));
        Assert.Equal(FindingRoundBatchSchedule.MaximumCompletedRuns, homeCalls.Count);
        var summary = JsonSerializer.Deserialize<FindingRoundBatchAccounting.Summary>(closure.Accounting)!;
        Assert.Equal(1, summary.Unavailable);
        Assert.Equal(0, summary.Superseded);
    }

    [Theory]
    [InlineData("Tester must provide a test receipt", false)]
    [InlineData("Await operator-owned evidence", true)]
    public void LegacyReviewerOwnership_SettlesAsDeferredAndPreservesRecovery(string blocker, bool operatorOwned)
    {
        using var fixture = new Fixture();
        FailReviewerNeedsWork(fixture.Kernel, fixture.Goal, fixture.Reviewer, blocker, findings:
            [Finding("legacy", new([new("Sample.Tests", "HomeTests")])) with
                { Category = FindingCategory.Unspecified, Description = blocker }]);
        var homeCalls = new List<string>();
        var driver = fixture.Driver([], homeCalls, recordBatchAccounting: true);

        Assert.True(driver.TryObserveVerifyingFindingRecovery(fixture.Goal, ConductorAutonomyPolicy.Permissive, [], out var result));
        if (operatorOwned)
            Assert.Equal(FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired, result.Kind);
        else
            Assert.Equal(fixture.Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester).Id, result.TargetTaskId);
        Assert.Empty(homeCalls);
        var summary = JsonSerializer.Deserialize<FindingRoundBatchAccounting.Summary>(
            Assert.IsType<FindingEvidenceBatchClosure>(result.BatchClosure).Accounting)!;
        Assert.Equal(1, summary.Deferred);
        Assert.Equal(0, summary.Unavailable);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void ActionableRed_MixedRepositories_SettlesOneHomeBatch(bool homeRed, bool replay)
    {
        using var fixture = new Fixture();
        FailReviewerNeedsWork(fixture.Kernel, fixture.Goal, fixture.Reviewer, "Need repository evidence", findings:
            [Finding("home", new([new("Sample.Tests", "HomeTests")])),
             Finding("keyed", new([new("./record", "TestReadInvalidFixtures")], Repository: "crosswalk")),
             Finding("control", new([new("./record", "TestReadInvalidFixtures")],
                 NegativeControl: FindingEvidenceNegativeControl.RevertSrc, RevertPaths: ["src/record.go"], Repository: "crosswalk"))]);
        var receipts = new List<FindingEvidenceReceipt>();
        var homeCalls = new List<string>();
        var keyedCalls = 0;
        ConductorDriver Driver(bool accounting)
        {
            var driver = fixture.Driver(receipts, homeCalls, accounting,
                request => homeRed ? CandidateRedFindingEvidence(request, fixture.HomeHead, "HomeTests.Fails")
                    : Green(request, fixture.HomeHead));
            driver.RunKeyedFocusedEvidence = (_, target, request, _, _, _, _, _, _, _) =>
            {
                keyedCalls++;
                return CandidateRedFindingEvidence(request, target.CandidateHead, "TestReadInvalidFixtures");
            };
            return driver;
        }
        var driver = Driver(accounting: true);
        var identity = FindingRoundBatchCoordinator.Identity(driver, fixture.Goal, fixture.Reviewer)!;
        FindingEvidenceOutcome HomeOutcome() => Assert.IsType<FindingEvidenceOutcome>(
            fixture.Reviewer.LastVerification!.MergedReviewFindings!.Single(finding => finding.StableId == "home").EvidenceOutcome);
        string? homeReceiptId = null;
        if (!homeRed)
        {
            Assert.True(driver.TryBuildFindingEvidenceRequest(fixture.Goal, fixture.Reviewer, ConductorAutonomyPolicy.Permissive, out var intermediate));
            Assert.Null(intermediate.TargetTaskId);
            Assert.Null(intermediate.BatchClosure);
            Assert.Equal(FailedGoalFindingObservationKind.FindingEvidencePending, intermediate.Kind);
            homeReceiptId = Assert.Single(receipts.Where(receipt => receipt.Passed)).ReceiptId;
            Assert.True(HomeOutcome().Honoured);
            Assert.Equal(homeReceiptId, HomeOutcome().ReceiptId);
        }
        if (replay)
        {
            // Retain the RED receipt but lose coordinator observations, as on receipt reconciliation.
            driver = Driver(accounting: false);
            Assert.True(driver.TryBuildFindingEvidenceRequest(fixture.Goal, fixture.Reviewer, ConductorAutonomyPolicy.Permissive, out _));
            driver = Driver(accounting: true);
        }
        Assert.True(driver.TryBuildFindingEvidenceRequest(fixture.Goal, fixture.Reviewer, ConductorAutonomyPolicy.Permissive, out var drained));
        Assert.Equal(FailedGoalFindingObservationKind.FindingActionableRed, drained.Kind);
        Assert.Equal(fixture.Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer).Id, drained.TargetTaskId);
        var closure = Assert.IsType<FindingEvidenceBatchClosure>(drained.BatchClosure);
        Assert.Equal(identity, closure.Identity);
        var summary = JsonSerializer.Deserialize<FindingRoundBatchAccounting.Summary>(closure.Accounting)!;
        Assert.Equal(3, summary.BatchSize);
        Assert.Equal(homeRed ? 1 : 2, summary.NewlyExecuted);
        Assert.Equal(homeRed ? 2 : 1, summary.Superseded);
        Assert.Equal(0, summary.Unavailable);
        Assert.True(summary.IsSettled);
        Assert.Single(homeCalls);
        Assert.Equal(homeRed ? 0 : 1, keyedCalls);
        var red = Assert.Single(receipts.Where(receipt => !receipt.Passed).DistinctBy(receipt => receipt.ReceiptId));
        Assert.Equal(homeRed ? fixture.HomeHead : fixture.SecondaryHead, red.CandidateSha);
        if (!homeRed)
        {
            Assert.True(HomeOutcome().Honoured);
            Assert.Equal(homeReceiptId, HomeOutcome().ReceiptId);
            Assert.DoesNotContain(FindingRoundBatchAccounting.Read(fixture.Goal, identity),
                observation => observation.Kind == "superseded" && observation.FindingIds.Contains("home"));
        }
        var control = fixture.Reviewer.LastVerification!.MergedReviewFindings!.Single(finding => finding.StableId == "control");
        Assert.Equal(FindingEvidenceNotHonouredReason.SupersededByActionableRed, control.EvidenceOutcome!.Reason);
        Assert.False(control.EvidenceOutcome.Honoured);
        Assert.Empty(FindingRoundBatchAccounting.Read(fixture.Goal, identity with { CandidateSha = fixture.SecondaryHead }));
        var retries = 0;
        string? Retry()
        {
            retries++;
            fixture.Kernel.RetryTask(fixture.Goal.Id, drained.TargetTaskId!, drained.Evidence, RetryCause.NewSourceFinding);
            return null;
        }
        Assert.True(FindingRoundBatchCoordinator.ApplyRetry(driver, fixture.Goal, closure, () => true, Retry).Applied);
        Assert.False(FindingRoundBatchCoordinator.ApplyRetry(driver, fixture.Goal, closure, () => true, Retry).Applied);
        Assert.Equal(1, retries);
        var restored = AgentOrchestratorKernel.FromSnapshot(fixture.Kernel.ExportSnapshot()).GetGoal(fixture.Goal.Id);
        Assert.True(FindingRoundBatchAccounting.IsClosed(restored, identity));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reviewer_DeferredMembers_PreserveDeveloperRecovery(bool selfExempt)
    {
        using var fixture = new Fixture();
        var blocker = new ReviewFinding("source", ReviewFindingState.Open, new("src/Record.cs", "Read"),
            "Repair source before requesting more evidence", Category: FindingCategory.Correctness,
            EvidenceRequest: selfExempt ? new([new("Sample.Tests", "HomeTests")]) : null);
        FailReviewerNeedsWork(fixture.Kernel, fixture.Goal, fixture.Reviewer, "Repair source", findings:
            [blocker, Finding("deferred", new([new("./record", "TestReadInvalidFixtures")], Repository: "crosswalk"))],
            reviewedCommit: fixture.HomeHead);
        var receipts = new List<FindingEvidenceReceipt>();
        var homeCalls = new List<string>();
        var driver = fixture.Driver(receipts, homeCalls, recordBatchAccounting: true);
        driver.RunKeyedFocusedEvidence = (_, _, _, _, _, _, _, _, _, _) =>
            throw new InvalidOperationException("Deferred evidence executed before source repair");
        var identity = FindingRoundBatchCoordinator.Identity(driver, fixture.Goal, fixture.Reviewer)!;

        Assert.True(driver.TryObserveVerifyingFindingRecovery(fixture.Goal, ConductorAutonomyPolicy.Permissive, [], out var routed));
        var developer = fixture.Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        Assert.Equal(developer.Id, routed.TargetTaskId);
        Assert.Equal(FailedGoalFindingObservationKind.FindingRouteObserved, routed.Kind);
        var closure = Assert.IsType<FindingEvidenceBatchClosure>(routed.BatchClosure);
        var summary = FindingRoundBatchAccounting.Summarize(fixture.Goal, fixture.Reviewer, identity, "deferred-recovery");
        Assert.Equal(selfExempt ? 2 : 1, summary.BatchSize);
        Assert.Equal(selfExempt ? 1 : 0, summary.NewlyExecuted);
        Assert.Equal(1, summary.Deferred);
        Assert.Equal(0, summary.Unavailable);
        Assert.Equal(0, summary.Covered);
        Assert.True(summary.IsSettled);
        Assert.Equal(selfExempt ? 1 : 0, homeCalls.Count);
        var disposition = Assert.Single(fixture.Reviewer.VerificationHistory.SelectMany(record => record.FindingEvidenceRequestDispositions ?? [])
            .Where(item => item.FindingStableId == "deferred"));
        Assert.Equal(FindingEvidenceRequestDecision.DeferredToNextCandidate, disposition.Decision);
        Assert.Null(disposition.SourceReceiptIds);
        var retries = 0;
        string? Retry()
        {
            retries++;
            fixture.Kernel.RetryTask(fixture.Goal.Id, developer.Id, routed.Evidence, RetryCause.NewSourceFinding);
            return null;
        }
        Assert.True(FindingRoundBatchCoordinator.ApplyRetry(driver, fixture.Goal, closure, () => true, Retry).Applied);
        Assert.False(FindingRoundBatchCoordinator.ApplyRetry(driver, fixture.Goal, closure, () => true, Retry).Applied);
        Assert.Equal(1, retries);
        var restored = AgentOrchestratorKernel.FromSnapshot(fixture.Kernel.ExportSnapshot()).GetGoal(fixture.Goal.Id);
        Assert.True(FindingRoundBatchAccounting.IsClosed(restored, identity));
        Assert.Equal(1, FindingRoundBatchAccounting.Summarize(restored, restored.Tasks.Single(task => task.Id == fixture.Reviewer.Id),
            identity, "restored").Deferred);
    }

    [Fact]
    public void Reviewer_DeferredMembersAtRetryCap_AdmitOneClosureAcrossReplay()
    {
        using var fixture = new Fixture();
        var source = new ReviewFinding("source", ReviewFindingState.Open, new("src/Record.cs", "Read"),
            "Repair source before running repository evidence", Category: FindingCategory.Correctness);
        FailReviewerNeedsWork(fixture.Kernel, fixture.Goal, fixture.Reviewer, "Repair source", findings:
            [source, Finding("deferred", new([new("./record", "TestReadInvalidFixtures")], Repository: "crosswalk"))],
            reviewedCommit: fixture.HomeHead);
        var driver = fixture.Driver([], [], recordBatchAccounting: true);
        var policy = ConductorAutonomyPolicy.Permissive with { ReviewAutoRetryStopRound = 1 };
        Assert.True(ReviewRetryCapReceipt.Create(fixture.Goal, policy.ReviewAutoRetryStopRound).IsAtConsecutiveCap);
        Assert.True(driver.TryObserveVerifyingFindingRecovery(fixture.Goal, policy, [], out var capped));
        Assert.Equal(FailedGoalFindingObservationKind.FindingRetryCapReached, capped.Kind);
        Assert.Null(capped.TargetTaskId);
        var closure = Assert.IsType<FindingEvidenceBatchClosure>(capped.BatchClosure);
        var summary = JsonSerializer.Deserialize<FindingRoundBatchAccounting.Summary>(closure.Accounting)!;
        Assert.Equal(1, summary.BatchSize);
        Assert.Equal(1, summary.Deferred);
        Assert.Contains("FindingRetryCapReached", summary.RoutingReason);
        Assert.True(FindingRoundBatchCoordinator.TryApply(driver, fixture.Goal, closure, () => true, () => true));

        var restored = AgentOrchestratorKernel.FromSnapshot(fixture.Kernel.ExportSnapshot()).GetGoal(fixture.Goal.Id);
        Assert.False(FindingEvidenceBatchClosureAdmission.TryApply(restored, closure, () => true,
            () => throw new InvalidOperationException("The capped batch was admitted twice.")));
    }

    [Fact]
    public void Reviewer_DeferredMembersWithRepeatedFailures_EscalateOnceAcrossReplay()
    {
        using var fixture = new Fixture();
        var source = new ReviewFinding("source", ReviewFindingState.Open, new("src/Record.cs", "Read"),
            "Repair source before running repository evidence", Category: FindingCategory.Correctness);
        FailReviewerNeedsWork(fixture.Kernel, fixture.Goal, fixture.Reviewer, "Repair source", findings:
            [source, Finding("deferred", new([new("./record", "TestReadInvalidFixtures")], Repository: "crosswalk"))],
            reviewedCommit: fixture.HomeHead);
        var recordedAt = DateTimeOffset.UtcNow;
        foreach (var candidate in new[] { "abc1234", "def5678", fixture.HomeHead })
            fixture.Kernel.RecordPreReviewEvidence(fixture.Goal.Id, fixture.Reviewer.Id,
                new(fixture.Goal.Id.Value, 1, candidate, [], PreReviewEvidenceDisposition.Red, 0, 1,
                    [new("focused tests", "test", false, 1)], ["RecordTests.Read"], "mapped", null, recordedAt));
        Assert.True(PreReviewRepeatedFailureSet.Evaluate(fixture.Reviewer.PreReviewEvidenceHistory).HoldRequired);
        var escalations = new List<string>();
        var calls = new List<string>();
        var driver = fixture.Driver([], calls, recordBatchAccounting: true, recordEscalation: escalations.Add);
        driver.RunKeyedFocusedEvidence = (_, _, _, _, _, _, _, _, _, _) =>
            throw new InvalidOperationException("Deferred request ran before source repair");
        var policy = ConductorAutonomyPolicy.Permissive;
        var identity = FindingRoundBatchCoordinator.Identity(driver, fixture.Goal, fixture.Reviewer)!;

        Assert.IsType<ConductorAdvanceOutcome.Escalated>(driver.AdvanceOnce(fixture.Goal, policy).Outcome);
        Assert.Contains("PRE_REVIEW_REPEATED_FAILING_SET", Assert.Single(escalations), StringComparison.Ordinal);
        Assert.True(FindingRoundBatchAccounting.IsClosed(fixture.Goal, identity));
        var closed = Assert.Single(fixture.Goal.Timeline.Where(item => item.Message.StartsWith("finding-evidence batch-closed;")));
        var summary = JsonSerializer.Deserialize<FindingRoundBatchAccounting.Summary>(closed.Message[closed.Message.IndexOf('{')..])!;
        Assert.Equal(1, summary.BatchSize);
        Assert.Equal(1, summary.Deferred);
        Assert.StartsWith("FindingRepeatedFailingTestSet:", summary.RoutingReason);

        fixture.Restart();
        driver = fixture.Driver([], calls, recordBatchAccounting: true, recordEscalation: escalations.Add);
        Assert.IsType<ConductorAdvanceOutcome.Held>(driver.AdvanceOnce(fixture.Goal, policy).Outcome);
        Assert.True(FindingRoundBatchAccounting.IsClosed(fixture.Goal, identity));
        Assert.Single(escalations);
        Assert.Empty(calls);
    }

    [Fact]
    public void Request_MissingKeyedTarget_NeverInvokesHomeRunner()
    {
        using var fixture = new Fixture();
        FailReviewerNeedsWork(fixture.Kernel, fixture.Goal, fixture.Reviewer, "Need keyed evidence", findings:
            [Finding("keyed", new([new("./record", "TestReadInvalidFixtures")], Repository: "crosswalk"))]);
        var homeCalls = new List<string>();
        var receipts = new List<FindingEvidenceReceipt>();
        var driver = fixture.Driver(receipts, homeCalls);
        driver.ResolveKeyedEvidenceTarget = (_, _) => (null, "crosswalk:missing-worktree");
        driver.RunKeyedFocusedEvidence = (_, _, _, _, _, _, _, _, _, _) => throw new InvalidOperationException("Unresolved target executed");

        Assert.True(driver.TryBuildFindingEvidenceRequest(fixture.Goal, fixture.Reviewer, ConductorAutonomyPolicy.Permissive, out _));
        Assert.Empty(homeCalls);
        Assert.Empty(receipts);
        var outcome = Assert.Single(fixture.Reviewer.LastVerification!.MergedReviewFindings!).EvidenceOutcome!;
        Assert.False(outcome.Honoured);
        Assert.Equal(FindingEvidenceNotHonouredReason.ExecutorUnavailable, outcome.Reason);
    }

    [Fact]
    public void Resolve_MissingManifest_DoesNotBorrowHomeManifest()
    {
        using var fixture = new Fixture();
        File.Delete(Path.Combine(fixture.ProjectHome, "acceptance-manifest.json"));
        Assert.True(File.Exists(Path.Combine(fixture.HomeWorktree, "config", "acceptance-manifest.json")));
        var (target, failure) = KeyedRepositoryEvidenceTarget.TryResolve(fixture.Goal, "crosswalk", fixture.Registry);
        Assert.Null(target);
        Assert.NotEmpty(failure);
    }

    [Fact]
    public void Batch_IdenticalSelectionsInDifferentRepositories_RemainSeparate()
    {
        var request = new FindingEvidenceRequest([new("Sample.Tests", "ProbeTests")]);
        var groups = new string?[] { null, "default", "crosswalk" }.Select(key =>
        {
            var keyed = request with { Repository = key };
            return new FindingEvidenceRequestGroup(FindingEvidenceExecutionClassifier.BuildRequestIdentity(keyed),
                "Sample.Tests:ProbeTests", keyed, [Finding(key ?? "home", keyed)]);
        }).ToArray();
        var batches = FindingEvidenceRoundPlanner.BuildFindingEvidenceBatches(groups);
        Assert.Equal(3, batches.Count);
        Assert.Equal(new string?[] { null, "default", "crosswalk" }, batches.Select(batch => batch.TypedRequest.Repository));
    }

    [Fact]
    public void Attribute_KeyedRedWithoutBaseline_DoesNotReadHomeChanges()
    {
        var request = new FindingEvidenceRequest([new("Sample.Tests", "ProbeTests")], Repository: "crosswalk");
        var finding = Finding("keyed", request);
        var group = new FindingEvidenceRequestGroup("keyed", "Sample.Tests:ProbeTests", request, [finding]);
        var batch = Assert.Single(FindingEvidenceRoundPlanner.BuildFindingEvidenceBatches([group]));
        var driver = MakeDriver(getLandingFileScopes: _ => throw new InvalidOperationException("Keyed attribution read home changes"));
        FindingEvidenceArmReceipt[] arms = [new(FindingEvidenceArm.Candidate, "bbbbbbb", FindingEvidenceArmDisposition.Red,
            true, false, "red", FailingTestIdentities: ["ProbeTests.Fails"])];
        Assert.Null(driver.TryAttributeActionableCandidateRed(KeyedRepositoryEvidenceNormalizationTests.CreateGoal(), "bbbbbbb", arms, batch));
    }

    private static ReviewFinding Finding(string id, FindingEvidenceRequest request) =>
        new(id, ReviewFindingState.Open, new("src/Record.cs", "Record.Read"), "Prove repository evidence",
            EvidenceRequest: request, Category: FindingCategory.TestEvidence);

    private static FocusedEvidenceRunResult Green(string request, string head)
    {
        AcceptanceCheckResult[] checks = [new("focused tests", true, 0, "Executed: 1", ExecutedTestCount: 1)];
        return new(request, true, true, "focused check passed", checks,
            Arms: [new(FindingEvidenceArm.Candidate, head, FindingEvidenceArmDisposition.Green, true, true, "green", checks)],
            OutcomeReason: FindingEvidenceOutcomeReason.ValidEvidence);
    }

    private sealed class RecordingVerifier : IGoalAcceptanceVerifier
    {
        internal List<(string Worktree, AcceptanceFocusedVerificationOwner Owner, FindingEvidenceNegativeControl? Control,
            IReadOnlyList<string>? RevertPaths, IReadOnlyList<string>? DeclaredPaths)> Calls { get; } = [];
        public Task<AcceptanceVerificationResult> RunOwnedAsync(string worktreePath, GoalId? goalId, IReadOnlyList<string>? changedFiles,
            int? stableSlotIndex, DotnetBuildEnvironmentLease? stableSlotLease, IAcceptanceAttemptExecutionOwner executionOwner) =>
            throw new InvalidOperationException("Unexpected full acceptance run");
        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceOwnedAsync(string worktreePath, GoalId? goalId, string request,
            IAcceptanceFocusedVerificationOwner executionOwner, int? stableSlotIndex = null, DotnetBuildEnvironmentLease? stableSlotLease = null,
            bool runBaselineArm = false) => Record(worktreePath, request, executionOwner, null, null, null);
        public Task<FocusedEvidenceRunResult> RunNegativeControlFocusedEvidenceOwnedAsync(string worktreePath, GoalId? goalId, string request,
            IAcceptanceFocusedVerificationOwner executionOwner, FindingEvidenceNegativeControl negativeControl, int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null, bool runBaselineArm = false, IReadOnlyList<string>? revertPaths = null,
            FindingEvidenceMutation? mutation = null, IReadOnlyList<string>? declaredPaths = null) =>
            Record(worktreePath, request, executionOwner, negativeControl, revertPaths, declaredPaths);
        private Task<FocusedEvidenceRunResult> Record(string worktree, string request, IAcceptanceFocusedVerificationOwner owner,
            FindingEvidenceNegativeControl? control, IReadOnlyList<string>? paths, IReadOnlyList<string>? declared)
        {
            var typedOwner = Assert.IsType<AcceptanceFocusedVerificationOwner>(owner);
            Calls.Add((worktree, typedOwner, control, paths, declared));
            return Task.FromResult(Green(request, typedOwner.Identity.CandidateSha));
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal GoalRepositoriesTestFixture Repositories { get; } = new();
        internal AgentOrchestratorKernel Kernel { get; private set; }
        internal Goal Goal { get; private set; }
        internal TaskSpec Reviewer => Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        internal OrchestratorProjectRegistry Registry { get; }
        internal string HomeWorktree { get; }
        internal string SecondaryWorktree { get; }
        internal string ProjectHome { get; }
        internal string HomeHead { get; }
        internal string SecondaryHead { get; }

        internal Fixture()
        {
            Registry = new(Path.Combine(Repositories.Home.Root, "evidence-registry"), Path.Combine(Repositories.Home.Root, "evidence-data"));
            Repositories.Crosswalk.Git("checkout", "-b", "trunk");
            Registry.CreateProject("crosswalk", Repositories.Crosswalk.CheckoutPath, "trunk");
            ProjectHome = MultiRepositoryProjectVerifier.ProjectHome(Registry, "crosswalk");
            Directory.CreateDirectory(ProjectHome);
            File.WriteAllText(Path.Combine(ProjectHome, "acceptance-manifest.json"), GoPackageFocusedEvidenceTests.Manifest);
            var homeConfig = Path.Combine(Repositories.Home.CheckoutPath, "config");
            Directory.CreateDirectory(homeConfig);
            File.WriteAllText(Path.Combine(homeConfig, "acceptance-manifest.json"), """
                {"checks":[{"type":"dotnet-test","project":"tests/Sample.Tests/Sample.Tests.csproj"}]}
                """);
            Repositories.Home.Git("add", "config/acceptance-manifest.json");
            Repositories.Home.Git("commit", "-m", "Seed home settings");
            (Kernel, Goal) = SoftwareGoal();
            Kernel.DeclareGoalRepositories(Goal.Id, Repositories.Declaration);
            HomeWorktree = Repositories.CreateHomeWorktree(Goal);
            Assert.True(GoalRepositoryWorktreeProvisioner.TryProvision(new GoalRepositorySet(["crosswalk"]), Goal.Id,
                new Dictionary<string, string> { ["crosswalk"] = Repositories.Crosswalk.CheckoutPath }, out var bindings, out var failure), failure);
            SecondaryWorktree = bindings!["crosswalk"].WorktreePath;
            Assert.False(File.Exists(Path.Combine(SecondaryWorktree, "config", "acceptance-manifest.json")));
            HomeHead = GitFixture.CommitChange(HomeWorktree, "home candidate\n");
            SecondaryHead = GitFixture.CommitChange(SecondaryWorktree, "secondary candidate\n");
            Assert.NotEqual(HomeHead, SecondaryHead);
            foreach (var task in Goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer)) PassVerification(Kernel, Goal, task);
        }

        internal KeyedRepositoryEvidenceTarget Resolve()
        {
            var (target, failure) = KeyedRepositoryEvidenceTarget.TryResolve(Goal, "crosswalk", Registry);
            Assert.NotNull(target);
            Assert.Empty(failure);
            return target;
        }

        internal void Restart()
        {
            Kernel = AgentOrchestratorKernel.FromSnapshot(Kernel.ExportSnapshot());
            Goal = Kernel.GetGoal(Goal.Id);
        }

        internal ConductorDriver Driver(List<FindingEvidenceReceipt> receipts, List<string> homeCalls, bool recordBatchAccounting = false,
            Func<string, FocusedEvidenceRunResult>? runHome = null, bool configureHomeRunner = true, Action<string>? recordEscalation = null)
        {
            var driver = MakeDriver(getPreReviewEvidenceContext: _ => NoPreReviewContext(HomeHead),
                writeEscalationWithResult: recordEscalation is null ? null : (_, _, message) =>
                {
                    recordEscalation(message);
                    return LandingEscalationWriteResult.CompletedWithoutExternalSinks;
                },
                getFindingEvidenceEngineSettings: _ => KeyedRepositoryEvidenceNormalizationTests.HomeSettings,
                focusedEvidenceAttemptCoordinator: new(Path.Combine(Repositories.Home.Root, "evidence-attempts"), "main", runInline: true),
                runFocusedEvidence: configureHomeRunner
                    ? (_, request) => { homeCalls.Add(request); return runHome?.Invoke(request) ?? Green(request, HomeHead); }
                    : null,
                recordFindingEvidenceRequestDispositions: (goalId, taskId, dispositions) =>
                {
                    if (recordBatchAccounting) Kernel.RecordFindingEvidenceRequestDispositions(goalId, taskId, dispositions);
                },
                recordFindingEvidenceRequest: (goalId, taskId, message) =>
                {
                    if (recordBatchAccounting) Kernel.RecordFindingEvidenceRequest(goalId, taskId, message);
                },
                recordFindingEvidenceRun: (goalId, taskId, message) =>
                {
                    if (recordBatchAccounting) Kernel.RecordFindingEvidenceRun(goalId, taskId, message);
                },
                recordFindingEvidenceOutcome: (goalId, taskId, id, outcome, receipt) =>
                {
                    if (receipt is not null) receipts.Add(receipt);
                    Kernel.RecordFindingEvidenceOutcome(goalId, taskId, id, outcome, receipt);
                });
            driver.ResolveKeyedEvidenceTarget = (goal, key) => KeyedRepositoryEvidenceTarget.TryResolve(goal, key, Registry);
            return driver;
        }

        public void Dispose() => Repositories.Dispose();
    }
}
