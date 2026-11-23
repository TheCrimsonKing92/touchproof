using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using static AttributedFailureDeveloperRouteTests;
using static SoloAttributedFailureRouteFixture;

// Parallel-safe: Git, SQLite, artifact paths and the controlled verifier belong to this test.
public sealed class SoloRepairWorkflowTests
{
    private static readonly ConductorAutonomyPolicy Policy = ConductorAutonomyPolicy.Permissive with { MaxCriterionRetries = 0 };
    private const string LandingBlocker = "fixture stops at landing mutation";

    [Fact]
    public void PullRequestRepair_UsesTheReachableSquashPromotion()
    {
        using var fixture = new PullRequestTrackingTestFixture();
        var squash = fixture.Git("commit-tree", fixture.Delivery.Candidate + "^{tree}", "-p", fixture.Delivery.Main,
            "-m", "Squashed baseline repair");
        fixture.Git("update-ref", "refs/remotes/origin/main", squash);
        fixture.Forge.Json = JsonSerializer.Serialize(new
        {
            state = "MERGED", mergedAt = "2026-10-31T12:00:00Z", mergeCommit = new { oid = squash },
            closedAt = "2026-10-31T12:00:00Z", url = PullRequestLandingTestFixture.Url,
            reviewDecision = (string?)null, latestReviews = Array.Empty<object>()
        });
        Assert.Contains(fixture.Goal.Id, fixture.Tick());
        Assert.Equal(squash, fixture.Fact()?.PromotedMainRevision);
        Assert.Equal(fixture.Delivery.Candidate, fixture.Fact()?.CandidateRevision);
        Assert.Equal(squash, SoloBaselineRepairCoordinator.ResolveSoloRepairLanding(fixture.Workspace, fixture.Goal,
            fixture.Git("rev-parse", "main")));
        Assert.Null(SoloBaselineRepairCoordinator.ResolveSoloRepairLanding(fixture.Workspace, fixture.Goal, fixture.Delivery.Main));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PullRequestPublication_RejectsARefChangeAfterRepairAdmission(bool moveMain)
    {
        using var fixture = new PullRequestLandingTestFixture();
        var store = new SoloBaselineRepairStore(Path.Combine(fixture.Workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName));
        var finding = SoloRepairReplayTests.Finding() with
            { Candidate = fixture.Candidate, Main = fixture.Main, Baseline = fixture.Main };
        store.Retain(fixture.Goal.Id.Value, [finding]);
        store.ReserveGroup(finding.GroupKey, "{}");
        store.AcknowledgeRepair(finding.GroupKey, Guid.NewGuid().ToString("N"));
        RecordPassedValidation(store, finding, fixture.Main);
        store.AcknowledgeEffect(SoloBaselineRepairStore.NormalGateKey(fixture.Goal.Id.Value, fixture.Candidate, fixture.Main),
            PullRequestLandingTestFixture.Attempt);
        var changed = false;

        var result = LandingExecutor.Execute(fixture.Kernel, fixture.Goal, fixture.Workspace,
            pullRequestPublisher: new GitHubPullRequestPublisher(fixture.Forge), mutationBlocker: () =>
            {
                var branch = moveMain ? "main" : GoalWorktrees.BranchName(fixture.Goal.Id);
                RunGit(fixture.Repository, "update-ref", "refs/heads/" + branch, moveMain ? fixture.Candidate : fixture.Main);
                changed = true;
                return null;
            });

        Assert.True(changed);
        Assert.Contains("revision pair changed after baseline repair validation or normal acceptance", result.Message);
        Assert.Empty(fixture.Forge.Calls);
        Assert.Null(fixture.Goal.DeliveredForReview);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Escalation_ReplaysOnceAfterDeliveryAndPartialSinkFailure(bool failCollaborationSink)
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        fixture.Git("checkout", "main");
        var workspace = Workspace(fixture);
        var goal = NewCandidate(fixture, fixture.Kernel);
        var failure = await ProduceFailure(fixture, goal);
        var verifier = new ControlledVerifier(fixture.Root);
        var driver = Driver(fixture.Kernel, workspace, verifier);
        var storePath = Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName);
        var collabPath = Path.Combine(workspace.OrchestratorDirectory, "collaboration-items.db");
        var collaboration = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        Assert.Empty(await collaboration.ListAsync(goal.Id.Value));
        if (failCollaborationSink)
            SoloRepairReplayTests.ExecuteSql(collabPath, """
                CREATE TRIGGER stop_collab BEFORE INSERT ON collaboration_items
                BEGIN SELECT RAISE(ABORT, 'controlled collaboration failure'); END
                """);
        SoloRepairReplayTests.ExecuteSql(storePath, """
            CREATE TRIGGER stop_escalation_ack BEFORE INSERT ON solo_repair_check
            BEGIN SELECT RAISE(ABORT, 'lost escalation acknowledgement'); END
            """);

        Assert.Contains("lost escalation acknowledgement", Assert.Throws<SqliteException>(() => Complete(driver, goal, failure)).Message);
        Assert.Equal(failCollaborationSink ? 0 : 1, (await collaboration.ListAsync(goal.Id.Value)).Count);
        var events = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory).EventFilePath(goal.Id);
        var delivered = Assert.Single(File.ReadLines(events).Where(line => line.Contains("\"eventType\":\"GoalEscalated\"")));
        using var initial = JsonDocument.Parse(delivered);
        var deliveryId = initial.RootElement.GetProperty("deliveryId").GetString()!;
        Assert.Null(new SoloBaselineRepairStore(storePath).ReadCheck(deliveryId));

        SoloRepairReplayTests.ExecuteSql(storePath, "DROP TRIGGER stop_escalation_ack");
        if (failCollaborationSink) SoloRepairReplayTests.ExecuteSql(collabPath, "DROP TRIGGER stop_collab");
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(fixture.Kernel);
        var restarted = await repository.LoadAsync();
        driver = Driver(restarted, workspace, verifier);
        AssertHold(driver.AdvanceOnce(restarted.GetGoal(goal.Id), Policy), SoloHoldReasons.RepairEvidenceRequired, owner: "operator");

        Assert.Equal(delivered, Assert.Single(File.ReadLines(events).Where(line => line.Contains("\"eventType\":\"GoalEscalated\""))));
        Assert.Single(await collaboration.ListAsync(goal.Id.Value));
        Assert.Equal("passed", new SoloBaselineRepairStore(storePath).ReadCheck(deliveryId)?.Outcome);
    }

    [Fact]
    public async Task RefreshedBaselineStillRed_EscalatesTheRepairDecisionOnce()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        fixture.Git("checkout", "main");
        var workspace = Workspace(fixture);
        await new PortfolioStore(workspace.PortfolioStorePath).AddEpicAsync("baseline-maintenance");
        var goal = NewCandidate(fixture, fixture.Kernel);
        var verifier = new ControlledVerifier(fixture.Root) { RefreshedBaselinePass = false };
        var driver = Driver(fixture.Kernel, workspace, verifier);
        AssertHold(Complete(driver, goal, await ProduceFailure(fixture, goal)), SoloHoldReasons.RepairPending);
        var store = new SoloBaselineRepairStore(Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName));
        var finding = Assert.Single(store.ReadFindings(goal.Id.Value));
        var repairId = Assert.IsType<string>(store.ReadGroup(finding.GroupKey)?.RepairGoal);
        verifier.Finding = finding;
        fixture.Git("commit", "--allow-empty", "-m", $"Integrate goal/{repairId}: incomplete repair");
        var refreshedMain = fixture.Git("rev-parse", "HEAD");
        RunGit(Worktree(fixture, goal), "rebase", "main");

        for (var tick = 0; tick < 2; tick++)
        {
            var result = driver.AdvanceOnce(goal, Policy);
            AssertHold(result, SoloHoldReasons.RepairEvidenceRequired, "operator");
            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
            Assert.Contains($"The original test {finding.Identity} executed and still fails on refreshed baseline {refreshedMain}",
                held.Reason, StringComparison.Ordinal);
            Assert.Contains($"reopen repair {repairId} or designate a follow-up repair", held.Reason, StringComparison.Ordinal);
        }

        Assert.Equal((finding.Request!, refreshedMain), Assert.Single(verifier.FocusedRuns));
        Assert.Equal("failed", store.ReadCheck(SoloBaselineRepairStore.ValidationKey(finding, refreshedMain))?.Outcome);
        var events = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory).EventFilePath(goal.Id);
        Assert.Single(File.ReadLines(events).Where(line => line.Contains("\"eventType\":\"GoalEscalated\"")));
        Assert.Empty(verifier.NormalRuns);
    }

    [Fact]
    public async Task RepairFlow_AfterLostAcknowledgement_ReusesRepairAndRegatesEachCandidate()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        fixture.Git("checkout", "main");
        var oldMain = fixture.Git("rev-parse", "HEAD");
        var workspace = Workspace(fixture);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var maintenance = await portfolio.AddEpicAsync("baseline-maintenance");
        var firstEpic = await portfolio.AddEpicAsync("first project");
        var secondEpic = await portfolio.AddEpicAsync("second project");
        var first = NewCandidate(fixture, fixture.Kernel);
        var second = NewCandidate(fixture, fixture.Kernel);
        await portfolio.AssignGoalToEpicAsync(first.Id.Value, firstEpic.Id);
        await portfolio.AssignGoalToEpicAsync(second.Id.Value, secondEpic.Id);
        var third = NewCandidate(fixture, fixture.Kernel);
        var fourth = NewCandidate(fixture, fixture.Kernel);
        var goalIds = new[] { first.Id, second.Id, third.Id, fourth.Id };
        await repository.SaveAsync(fixture.Kernel);
        var verifier = new ControlledVerifier(fixture.Root);
        var driver = Driver(fixture.Kernel, workspace, verifier);
        var storePath = Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName);
        var store = new SoloBaselineRepairStore(storePath);

        var firstFailure = await ProduceFailure(fixture, first);
        var interrupted = false;
        using (GoalCreationSideEffectDelivery.UseBeforeEffectDelivery(_ =>
        {
            interrupted = true;
            throw new IOException("lost repair creation acknowledgement");
        }))
        {
            AssertHold(Complete(driver, first, firstFailure), SoloHoldReasons.RepairPending);
        }
        Assert.True(interrupted);
        Assert.Equal(GoalStatus.AcceptanceFailed, first.Status);
        var finding = Assert.Single(store.ReadFindings(first.Id.Value));
        Assert.True(finding.Inherited, finding.Reason);
        Assert.Equal(oldMain, finding.Baseline);
        var intake = new GoalIntakeRequestStore(workspace.SqliteStatePath);
        var requestKey = "solo-baseline-repair-" + finding.GroupKey;
        var receipt = Assert.IsType<GoalIntakeRequestRecord>(intake.Get(requestKey));
        Assert.Equal(GoalIntakeRequestStates.Created, receipt.State);
        Assert.Null(store.ReadGroup(finding.GroupKey)?.RepairGoal);
        Assert.Empty(verifier.FocusedRuns);

        await repository.SaveAsync(fixture.Kernel);
        var kernel = await repository.LoadAsync();
        first = kernel.GetGoal(first.Id);
        second = kernel.GetGoal(second.Id);
        var secondDispatch = Assert.Single(second.Tasks.Single().DispatchHistory);
        Assert.Equal(oldMain, secondDispatch.BaseCommit);
        Assert.Equal(RunGit(Worktree(fixture, second), "rev-parse", "HEAD"), secondDispatch.ResultCommit);
        driver = Driver(kernel, workspace, verifier);
        AssertHold(driver.AdvanceOnce(first, Policy), SoloHoldReasons.RepairPending);
        Assert.Equal(receipt.GoalId, store.ReadGroup(finding.GroupKey)?.RepairGoal);
        var repair = kernel.GetGoal(new GoalId(receipt.GoalId!));
        Assert.Equal(maintenance.Id, (await portfolio.GetGoalMembershipAsync(repair.Id.Value))?.EpicId);

        foreach (var id in goalIds.Skip(1))
        {
            var goal = kernel.GetGoal(id);
            AssertHold(Complete(driver, goal, await ProduceFailure(fixture, goal)), SoloHoldReasons.RepairPending);
        }
        Assert.Equal(1, SoloRepairReplayTests.Count(storePath, "solo_repair_group"));
        Assert.Equal(4, SoloRepairReplayTests.Count(storePath, "solo_repair_dependency"));
        Assert.Equal(0, SoloRepairReplayTests.Count(storePath, "solo_repair_check"));
        var corroboration = new CohortAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
        Assert.Equal(4, corroboration.ReadSoloInheritedReceipts(oldMain, repair.Id, oldMain).Count);
        Assert.Equal(AcceptanceEngineHealth.Healthy, PostLandingCanaryFactory.CreateCircuit(workspace).Read().Health);
        Assert.Equal(firstEpic.Id, (await portfolio.GetGoalMembershipAsync(first.Id.Value))?.EpicId);
        Assert.Equal(secondEpic.Id, (await portfolio.GetGoalMembershipAsync(second.Id.Value))?.EpicId);

        GoalOperationJournal.RecordLandingIntent(fixture.Repo, repair, GoalWorktrees.BranchName(repair.Id),
            "main", oldMain, "fixture intent");
        AssertHold(driver.AdvanceOnce(first, Policy), SoloHoldReasons.RepairPending);
        fixture.Git("checkout", "-b", "unmerged-repair");
        fixture.Git("commit", "--allow-empty", "-m", $"Integrate goal/{repair.Id.Value}: repair baseline");
        var unmerged = fixture.Git("rev-parse", "HEAD");
        fixture.Git("checkout", "main");
        var branch = GoalWorktrees.BranchName(repair.Id);
        var integrationBranch = LandingExecutor.IntegrationBranchName;
        GoalOperationJournal.RecordLandingIntent(fixture.Repo, repair, branch, integrationBranch, unmerged, "earlier landing intent");
        GoalOperationJournal.Completed(fixture.Repo, repair, "conductor:land", "earlier landing completed", mainHeadSha: unmerged);
        new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory).AppendGoalLanded(repair.Id, integrationBranch,
            branch, LandingAdmissionReceipt.Recovered(unmerged));
        GoalOperationJournal.RecordLandingIntent(fixture.Repo, repair, branch, integrationBranch, oldMain, "unrelated reachable intent");
        AssertHold(driver.AdvanceOnce(first, Policy), SoloHoldReasons.RepairPending);
        Assert.Empty(verifier.FocusedRuns);

        fixture.Git("merge", "--ff-only", "unmerged-repair");
        fixture.Git("commit", "--allow-empty", "-m", "Advance integration after repair");
        var refreshedMain = fixture.Git("rev-parse", "HEAD");
        verifier.Finding = finding;
        foreach (var id in goalIds.Take(2))
        {
            var goal = kernel.GetGoal(id);
            RunGit(Worktree(fixture, goal), "rebase", "main");
            var resumed = Assert.IsType<ConductorAdvanceOutcome.Executed>(driver.AdvanceOnce(goal, Policy).Outcome);
            Assert.Equal("FreshGate", resumed.Decision?.Action);
            Assert.Equal(SoloHoldReasons.RepairValidated, resumed.Decision?.DiscriminatingEvidence);
            Assert.Contains(new PolicyDecisionFact("mainSha", refreshedMain), resumed.Decision!.Facts);
            Assert.Equal(GoalStatus.Verified, goal.Status);
        }
        Assert.Equal((finding.Request!, refreshedMain), Assert.Single(verifier.FocusedRuns));
        Assert.Empty(verifier.NormalRuns);
        store = new SoloBaselineRepairStore(storePath);
        var validation = Assert.IsType<SoloRepairCheckReceipt>(store.ReadCheck(
            SoloBaselineRepairStore.ValidationKey(finding, refreshedMain)));
        Assert.Equal("passed", validation.Outcome);
        Assert.NotEmpty(validation.Artifacts);

        var firstAdmission = Admit(driver, first);
        var secondAdmission = Admit(driver, second);
        var firstPass = RunNormal(fixture, verifier, firstAdmission, passed: true);
        var secondFailure = RunNormal(fixture, verifier, secondAdmission, passed: false);
        Assert.Equal(2, verifier.NormalRuns.Count);
        Assert.Equal(refreshedMain, firstPass.MainHeadSha);
        Assert.Equal(refreshedMain, secondFailure.MainHeadSha);
        Assert.Equal((first.Id, firstAdmission.BranchHeadSha!), verifier.NormalRuns[0]);
        Assert.Equal((second.Id, secondAdmission.BranchHeadSha!), verifier.NormalRuns[1]);
        Assert.NotEqual(firstPass.AttemptId, secondFailure.AttemptId);
        Assert.Null(store.ReadCheck(GateKey(firstAdmission)));
        Assert.Null(store.ReadCheck(GateKey(secondAdmission)));

        var atLanding = Assert.IsType<ConductorAdvanceOutcome.Held>(Complete(driver, first, firstPass).Outcome);
        Assert.Contains(LandingBlocker, atLanding.Reason);
        Assert.Equal(firstPass.AttemptId, store.ReadCheck(GateKey(firstAdmission))?.Detail);
        var rejected = Assert.IsType<ConductorAdvanceOutcome.Escalated>(Complete(driver, second, secondFailure).Outcome);
        Assert.Contains("normal candidate acceptance", rejected.Reason);
        Assert.Null(store.ReadCheck(GateKey(secondAdmission)));

        Assert.Contains(LandingBlocker, Assert.IsType<ConductorAdvanceOutcome.Held>(
            Complete(driver, first, firstPass).Outcome).Reason);
        Assert.Equal(2, SoloRepairReplayTests.Count(storePath, "solo_repair_check"));
        Assert.Single(verifier.FocusedRuns);

        await fixture.Produce(attemptId: "normal-introduced-" + second.Id.Value, singleRow: true,
            candidateWorktree: Worktree(fixture, second), goal: second);
        Assert.Equal(secondAdmission.BranchHeadSha, fixture.Summary.BranchHeadSha);
        Assert.Equal(refreshedMain, fixture.Summary.MainHeadSha);
        driver.FailureDiagnosis = null;
        var completion = Complete(driver, second, fixture.Summary, Policy with { MaxCriterionRetries = 1 });
        var retries = MarkerRetries(second);
        Assert.True(retries.Length > 0, completion.Outcome.ToString());
        var retry = Assert.Single(retries);
        Assert.Contains($"failing_tests={First}{Environment.NewLine}", retry.Message);
        Assert.Contains($"reproduced {First}:", retry.Message);
        Assert.Equal(RetryCause.NewTestFinding, second.Tasks.Single().PendingRetryCause);
        Assert.Null(store.ReadCheck(GateKey(secondAdmission)));
        Assert.Equal(4, SoloRepairReplayTests.Count(storePath, "solo_repair_dependency"));

        fixture.Git("commit", "--allow-empty", "-m", "Move integration before another candidate gate");
        Assert.Null(driver.TryBuildParallelAcceptanceCandidate(first, Policy, 0, out var decline));
        Assert.Equal(SoloHoldReasons.RepairPending, decline);
        Assert.Null(driver.TryBuildParallelAcceptanceCandidate(second, Policy, 1, out _));
        Assert.Single(verifier.FocusedRuns);

        var otherBase = NewCandidate(fixture, kernel);
        AssertHold(Complete(driver, otherBase, await ProduceFailure(fixture, otherBase)), SoloHoldReasons.RepairPending);
        var otherFinding = Assert.Single(store.ReadFindings(otherBase.Id.Value));
        Assert.NotEqual(finding.GroupKey, otherFinding.GroupKey);
        Assert.Equal(fixture.Git("rev-parse", "HEAD"), otherFinding.Baseline);
        Assert.NotEqual(repair.Id.Value, store.ReadGroup(otherFinding.GroupKey)?.RepairGoal);
        Assert.Equal(2, SoloRepairReplayTests.Count(storePath, "solo_repair_group"));
        Assert.Equal(5, SoloRepairReplayTests.Count(storePath, "solo_repair_dependency"));
    }

    [Fact]
    public async Task MixedCompletion_LinksRepairRoutesOnlyIntroducedFeedbackAndRetainsUnresolvedRow()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        fixture.Git("checkout", "main");
        var main = fixture.Git("rev-parse", "HEAD");
        var workspace = Workspace(fixture);
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var maintenance = await portfolio.AddEpicAsync("baseline-maintenance");
        var goal = NewCandidate(fixture, fixture.Kernel);
        await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(fixture.Kernel);
        await fixture.Produce(mixed: true, attemptId: goal.Id.Value, candidateWorktree: Worktree(fixture, goal), goal: goal);
        fixture.ReplaceProof(proof => proof with
        {
            Bindings = proof.Bindings.Where(binding => binding.TestIdentity != Unknown).ToArray()
        });
        var request = $"{Project}: FullyQualifiedName~{Unknown}";
        var verifier = new ControlledVerifier(fixture.Root) { UnresolvedAttributionRequest = request };
        var driver = Driver(fixture.Kernel, workspace, verifier);
        driver.FailureDiagnosis = null;

        var outcome = Complete(driver, goal, fixture.Summary, Policy with { MaxCriterionRetries = 1 }).Outcome;

        Assert.IsType<ConductorAdvanceOutcome.Held>(outcome);
        var retry = Assert.Single(MarkerRetries(goal));
        Assert.Equal(RetryCause.NewTestFinding, goal.Tasks.Single().PendingRetryCause);
        Assert.Contains($"failing_tests={Long}{Environment.NewLine}", retry.Message);
        Assert.Contains($"reproduced {Long}:", retry.Message);
        Assert.DoesNotContain(First, retry.Message);
        Assert.DoesNotContain(Unknown, retry.Message);

        var storePath = Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName);
        var store = new SoloBaselineRepairStore(storePath);
        var findings = store.ReadFindings(goal.Id.Value);
        Assert.Equal(2, findings.Count);
        var inherited = Assert.Single(findings.Where(row => row.Inherited));
        Assert.Equal(First, inherited.Identity);
        var group = Assert.IsType<SoloRepairGroup>(store.ReadGroup(inherited.GroupKey));
        Assert.Equal(maintenance.Id, (await portfolio.GetGoalMembershipAsync(Assert.IsType<string>(group.RepairGoal)))?.EpicId);
        Assert.Equal(1, SoloRepairReplayTests.Count(storePath, "solo_repair_dependency"));

        var unresolved = Assert.Single(findings.Where(row => !row.Inherited));
        Assert.Equal(Unknown, unresolved.Identity);
        Assert.Equal(request, unresolved.Request);
        Assert.Equal(fixture.Summary.SoloFailureReceipt!.Evidence, unresolved.Proof);
        Assert.NotEmpty(unresolved.CandidateArtifacts);
        var candidate = Assert.IsType<string>(fixture.Summary.BranchHeadSha);
        Assert.Equal((request, candidate), Assert.Single(verifier.FocusedRuns));
        var attribution = store.ReadCheck(SoloBaselineRepairStore.AttributionKey(goal.Id.Value, unresolved, candidate, main));
        Assert.Equal("unresolved", attribution?.Outcome);
    }

    [Fact]
    public async Task InvalidEnvelope_KeepsLiveExactSelectionsAcrossRestart()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        fixture.Git("checkout", "main");
        var workspace = Workspace(fixture);
        var kernel = fixture.Kernel;
        var goal = NewCandidate(fixture, kernel);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(kernel);
        var completion = await ProduceFailure(fixture, goal);
        var receipt = completion.SoloFailureReceipt!;
        var invalid = completion with { SoloFailureReceipt = receipt with { ContractVersion = 0 } };
        var request = $"{Project}: FullyQualifiedName~{First}";
        var verifier = new ControlledVerifier(fixture.Root) { UnresolvedAttributionRequest = request };

        AssertHold(Complete(Driver(kernel, workspace, verifier), goal, invalid), SoloHoldReasons.RepairEvidenceRequired, "operator");
        await repository.SaveAsync(kernel);
        var restarted = await repository.LoadAsync();
        var dependent = restarted.GetGoal(goal.Id);
        AssertHold(Driver(restarted, workspace, verifier).AdvanceOnce(dependent, Policy),
            SoloHoldReasons.RepairEvidenceRequired, "operator");

        var path = Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName);
        var store = new SoloBaselineRepairStore(path);
        var unresolved = Assert.Single(store.ReadFindings(goal.Id.Value).Where(row => row.Identity.Length != 0));
        Assert.Equal(First, unresolved.Identity);
        Assert.Equal(request, unresolved.Request);
        Assert.Equal(Project, unresolved.Project);
        Assert.Equal(receipt.Evidence, unresolved.Proof);
        Assert.False(unresolved.Inherited);
        Assert.Empty(unresolved.CandidateArtifacts);
        Assert.Equal((request, completion.BranchHeadSha!), Assert.Single(verifier.FocusedRuns));
        Assert.Equal(0, SoloRepairReplayTests.Count(path, "solo_repair_dependency"));
    }

    [Fact]
    public async Task MainMovement_PreparesCandidateBeforeChargingBothArms()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        fixture.Git("checkout", "main");
        var workspace = Workspace(fixture);
        var kernel = fixture.Kernel;
        var goal = NewCandidate(fixture, kernel);
        await ProduceFailure(fixture, goal);
        fixture.ReplaceProof(proof => proof with { Checks = proof.Checks.Select(item => item with { Artifacts = [] }).ToArray() });
        var failure = fixture.Summary;
        kernel.RecordAcceptanceFailure(goal.Id, failure.FailedChecks!, failure.BranchHeadSha, failure.MainHeadSha,
            failure.CheckAttributions, failure.BaselineAttestation, failure.AttemptId, failure.SoloFailureReceipt);
        kernel.RouteRecordedAcceptanceFailure(goal.Id, "Hold the unresolved acceptance failure.");
        var original = Assert.Single(Assert.IsType<SoloRepairPartition>(
            SoloAcceptanceFailureReceiptReader.ReadRepairPartition(goal, null, failure)).Findings);
        Assert.Equal(First, original.Identity);
        await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
        await new PortfolioStore(workspace.PortfolioStorePath).AddEpicAsync("baseline-maintenance");

        fixture.Git("commit", "--allow-empty", "-m", "Move only main");
        var main = fixture.Git("rev-parse", "main");
        var path = Worktree(fixture, goal);
        Assert.Equal(failure.MainHeadSha, RunGit(path, "merge-base", "HEAD", main));
        var verifier = new ControlledVerifier(fixture.Root) { Finding = original, AttributionBaselinePass = false };
        var driver = Driver(kernel, workspace, verifier);

        AssertHold(driver.AdvanceOnce(goal, Policy), SoloHoldReasons.RepairPending);
        var candidate = RunGit(path, "rev-parse", "HEAD");
        Assert.NotEqual(failure.BranchHeadSha, candidate);
        Assert.Equal(main, RunGit(path, "merge-base", "HEAD", main));
        Assert.Empty(verifier.FocusedRuns);

        AssertHold(driver.AdvanceOnce(goal, Policy), SoloHoldReasons.RepairPending);
        Assert.Equal((original.Request!, candidate), Assert.Single(verifier.FocusedRuns));
        var storePath = Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName);
        var store = new SoloBaselineRepairStore(storePath);
        var inherited = Assert.Single(store.ReadFindings(goal.Id.Value).Where(row => row.Inherited));
        Assert.Equal((candidate, main, main), (inherited.Candidate, inherited.Main, inherited.Baseline));
        Assert.Equal(1, SoloRepairReplayTests.Count(storePath, "solo_repair_check"));
    }

    [Fact]
    public async Task FocusedIntroduced_RoutesFromThePersistedProofAfterRestart()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        fixture.Git("checkout", "main");
        var workspace = Workspace(fixture);
        var kernel = fixture.Kernel;
        var goal = NewCandidate(fixture, kernel);
        await ProduceFailure(fixture, goal);
        fixture.ReplaceProof(proof => proof with { Checks = proof.Checks.Select(item => item with { Artifacts = [] }).ToArray() });
        var failure = fixture.Summary;
        var receipt = failure.SoloFailureReceipt!;
        var context = new SoloAcceptanceConsumptionContext(receipt.CandidateSha, receipt.MainSha,
            receipt.CandidateTreeSha, receipt.BaselineSha);
        kernel.RecordAcceptanceFailure(goal.Id, failure.FailedChecks!, failure.BranchHeadSha, failure.MainHeadSha,
            failure.CheckAttributions, failure.BaselineAttestation, failure.AttemptId, receipt);
        kernel.RouteRecordedAcceptanceFailure(goal.Id, "Hold the unresolved acceptance failure.");
        var original = Assert.Single(Assert.IsType<SoloRepairPartition>(
            SoloAcceptanceFailureReceiptReader.ReadRepairPartition(goal, context, failure)).Findings);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(kernel);
        var verifier = new ControlledVerifier(fixture.Root) { Finding = original, AttributionBaselinePass = true };
        var interrupted = new SoloBaselineRepairCoordinator(workspace, kernel, verifier,
            _ => new(receipt.CandidateSha, receipt.MainSha), _ => context,
            () => throw new InvalidOperationException("Lost introduced route before dispatch."),
            _ => new(true, false, false, false, false, false),
            (_, _, _, _) => LandingEscalationWriteResult.CompletedWithoutExternalSinks,
            (_, _) => AcceptanceStableSlotTestSupport.CreateFakeStableSlotLease(workspace.OrchestratorDirectory));
        Assert.Equal("Lost introduced route before dispatch.", Assert.Throws<InvalidOperationException>(
            () => interrupted.TryAdvance(goal, goal.Id.Value[..8], Policy)).Message);
        var store = new SoloBaselineRepairStore(Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName));
        Assert.Empty(store.ReadFindings(goal.Id.Value));
        Assert.Equal(First, Assert.Single(store.ReadFindings(goal.Id.Value, introduced: true)).Identity);

        var restarted = await repository.LoadAsync();
        var dependent = restarted.GetGoal(goal.Id);
        var driver = Driver(restarted, workspace, verifier);
        driver.FailureDiagnosis = null;
        _ = driver.AdvanceOnce(dependent, Policy with { MaxCriterionRetries = 1 });
        Assert.Contains($"failing_tests={First}{Environment.NewLine}", Assert.Single(MarkerRetries(dependent)).Message);
        Assert.Single(verifier.FocusedRuns);
    }

    [Fact]
    public async Task AcknowledgedRepair_WithItsOwnFailureRequiresRepairOwnerAction()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        fixture.Git("checkout", "main");
        var workspace = Workspace(fixture);
        var kernel = fixture.Kernel;
        var repair = NewCandidate(fixture, kernel);
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var maintenance = await portfolio.AddEpicAsync("baseline-maintenance");
        await portfolio.AssignGoalToEpicAsync(repair.Id.Value, maintenance.Id);
        await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
        var failure = await ProduceFailure(fixture, repair);
        var proof = fixture.Proof();
        var check = Assert.Single(proof.Checks).Check;
        var binding = Assert.Single(proof.Bindings);
        var finding = new SoloRepairFinding(failure.BranchHeadSha!, failure.MainHeadSha!, proof.Identity.BaselineSha,
            check.Name, binding.TestIdentity, check.TestProjectPath, $"{Project}: FullyQualifiedName~{First}",
            binding.BaselineCheckName, true, failure.AttemptId!, failure.SoloFailureReceipt!.Evidence, [], [], null);
        var path = Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName);
        var store = new SoloBaselineRepairStore(path);
        store.ReserveGroup(finding.GroupKey, "already provisioned");
        store.AcknowledgeRepair(finding.GroupKey, repair.Id.Value);
        var verifier = new ControlledVerifier(fixture.Root);
        var driver = Driver(kernel, workspace, verifier);

        AssertHold(Complete(driver, repair, failure), SoloHoldReasons.RepairOwnerActionRequired, "repair owner");
        AssertHold(driver.AdvanceOnce(repair, Policy), SoloHoldReasons.RepairOwnerActionRequired, "repair owner");
        Assert.Equal(0, SoloRepairReplayTests.Count(path, "solo_repair_dependency"));
        Assert.Equal(finding.GroupKey, Assert.Single(store.ReadFindings(repair.Id.Value)).GroupKey);
        Assert.Empty(verifier.FocusedRuns);
    }

    internal static OrchestratorWorkspace Workspace(SoloAttributedFailureRouteFixture fixture)
    {
        var workspace = OrchestratorWorkspace.ForProject("solo-repair-test", fixture.Repo, integrationBranch: "main",
            dataRootDirectory: Path.Combine(fixture.Root, "data"));
        Directory.CreateDirectory(workspace.OrchestratorDirectory);
        _ = StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        AgentCatalogStore.Save(workspace.AgentCatalogPath, AgentCatalog.Default());
        return workspace;
    }

    internal static SoloRepairCheckReceipt RecordPassedValidation(SoloBaselineRepairStore store, SoloRepairFinding finding, string main)
    {
        var key = SoloBaselineRepairStore.ValidationKey(finding, main);
        var root = store.ResultDirectory(key);
        Directory.CreateDirectory(root);
        var trx = Path.Combine(root, "original-selection.trx");
        WriteTrx(trx, true, finding.Identity);
        var evidence = SoloRepairEvidenceTests.FocusedResult(finding, main, trx, true, true, finding.Identity);
        var validation = SoloAcceptanceFailureReceiptReader.ReadRepairCheck(evidence, FindingEvidenceArm.Candidate, main, finding, root);
        Assert.Equal("passed", validation.Outcome);
        var proof = Path.Combine(root, "focused-result.json");
        var identity = SoloBaselineRepairStore.FocusedProofIdentity(key, finding, main, main, false, false);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new SoloRepairVerifierResult(identity, validation));
        File.WriteAllBytes(proof, bytes);
        var receipt = validation with { Proof = new(proof, SoloBaselineRepairStore.Digest(bytes)) };
        Assert.True(store.TryClaimCheck(key));
        store.CompleteCheck(key, receipt);
        return receipt;
    }

    private static ConductorDriver Driver(AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace,
        ControlledVerifier verifier)
    {
        var now = DateTimeOffset.UtcNow;
        var driver = new ConductorDriver(kernel, workspace, verifier, AgentCatalog.Default().Agents,
            WorkerProfileCatalog.Default(), utcNow: () => now, runAcceptanceAttemptsInCurrentProcess: true,
            cleanupHooks: new GoalWorktreeCleanupHooks
            {
                BuildStorageRoot = new DotnetBuildStorageRoot(Path.Combine(workspace.OrchestratorDirectory, "build"))
            })
        {
            CohortPartitionStableSlotLeaseSource = (_, _) =>
                AcceptanceStableSlotTestSupport.CreateFakeStableSlotLease(workspace.OrchestratorDirectory),
            LandingMutationBlocker = () => LandingBlocker
        };
        driver.BeginTick(kernel, 1);
        return driver;
    }

    private static Goal NewCandidate(SoloAttributedFailureRouteFixture fixture, AgentOrchestratorKernel kernel)
    {
        var goal = CompletedGoal(kernel, fixture.Repo);
        var worktree = GoalWorktrees.Ensure(fixture.Repo, goal.Id);
        var taskId = goal.Tasks.Single().Id;
        kernel.RecordDispatchBaseCommit(goal.Id, taskId, RunGit(worktree, "rev-parse", "HEAD"));
        File.WriteAllText(Path.Combine(worktree, "README.md"), goal.Id.Value);
        RunGit(worktree, "add", "README.md");
        RunGit(worktree, "commit", "-m", "Candidate changes");
        kernel.RecordDispatchResultCommit(goal.Id, taskId, RunGit(worktree, "rev-parse", "HEAD"));
        return goal;
    }

    private static string Worktree(SoloAttributedFailureRouteFixture fixture, Goal goal) =>
        Assert.IsType<string>(GoalWorktrees.TryResolve(fixture.Repo, goal.Id));

    private static string RunGit(string directory, params string[] arguments)
    {
        var result = GitCli.Run(directory, arguments);
        Assert.True(result.Succeeded, result.Error);
        return result.Output.Trim();
    }

    private static async Task<AcceptanceVerificationSummary> ProduceFailure(SoloAttributedFailureRouteFixture fixture, Goal goal)
    {
        await fixture.Produce(baselineFails: true, attemptId: goal.Id.Value, candidateWorktree: Worktree(fixture, goal),
            goal: goal, singleRow: true);
        return fixture.Summary;
    }

    private static ConductorAdvanceResult Complete(ConductorDriver driver, Goal goal, AcceptanceVerificationSummary summary,
        ConductorAutonomyPolicy? policy = null) =>
        driver.CompleteParallelLandingAcceptance(
            ConductorParallelAcceptanceCandidate.Create(goal, 0, ["README.md"], summary.BranchHeadSha, summary.MainHeadSha),
            policy ?? Policy, summary, out _);

    private static void AssertHold(ConductorAdvanceResult result, string reason, string owner = "conductor")
    {
        var hold = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(reason, hold.Decision?.DiscriminatingEvidence);
        Assert.Contains(new PolicyDecisionFact("owner", owner), hold.Decision!.Facts);
    }

    private static ConductorParallelAcceptanceCandidate Admit(ConductorDriver driver, Goal goal)
    {
        var candidate = driver.TryBuildParallelAcceptanceCandidate(goal, Policy, 0, out var reason);
        Assert.Null(reason);
        return Assert.IsType<ConductorParallelAcceptanceCandidate>(candidate);
    }

    private static string GateKey(ConductorParallelAcceptanceCandidate admitted) =>
        SoloBaselineRepairStore.NormalGateKey(admitted.Goal.Id.Value, admitted.BranchHeadSha!, admitted.MainHeadSha!);

    private static AcceptanceVerificationSummary RunNormal(SoloAttributedFailureRouteFixture fixture,
        ControlledVerifier verifier, ConductorParallelAcceptanceCandidate admitted, bool passed)
    {
        verifier.NormalPass = passed;
        var verification = AcceptanceExecutionRunner.RunAttempt(verifier, Worktree(fixture, admitted.Goal), admitted.Goal.Id,
            ["README.md"], null, null, CancellationToken.None,
            new(RunId: "normal-" + admitted.Goal.Id.Value, ResultsPrefix: Path.Combine(fixture.Root, "normal", admitted.Goal.Id.Value),
                IntegrationBranch: "main"));
        Assert.Equal(passed, verification.Passed);
        var failed = verification.Checks!.Where(check => !check.Passed).ToArray();
        return AcceptanceVerificationSummary.FromVerification(verification, failed, failed.Select(check => check.Name).ToArray(),
            admitted.BranchHeadSha, admitted.MainHeadSha, verification.TestResultPaths, null, null);
    }

    internal sealed class ControlledVerifier(string root) : IGoalAcceptanceVerifier
    {
        internal SoloRepairFinding? Finding { get; set; }
        internal string? UnresolvedAttributionRequest { get; init; }
        internal bool? AttributionBaselinePass { get; init; }
        internal bool AttributionCandidatePass { get; init; }
        internal bool RefreshedBaselinePass { get; init; } = true;
        internal List<(string Request, string Sha)> FocusedRuns { get; } = [];
        internal List<(GoalId Goal, string Sha)> NormalRuns { get; } = [];
        internal bool NormalPass { get; set; }

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceOwnedAsync(string worktreePath, GoalId? goalId,
            string request, IAcceptanceFocusedVerificationOwner executionOwner, int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null, bool runBaselineArm = false)
        {
            Assert.NotNull(stableSlotLease);
            var sha = RunGit(worktreePath, "rev-parse", "HEAD");
            FocusedRuns.Add((request, sha));
            if (runBaselineArm && AttributionBaselinePass is null)
            {
                Assert.Equal(UnresolvedAttributionRequest, request);
                return Task.FromResult(new FocusedEvidenceRunResult(request, false, false, "The focused test did not execute.", []));
            }
            var finding = Assert.IsType<SoloRepairFinding>(Finding);
            Assert.Equal(finding.Request, request);
            var trx = Path.Combine(root, sha + "-candidate.trx");
            var candidatePass = runBaselineArm ? AttributionCandidatePass : RefreshedBaselinePass;
            WriteTrx(trx, candidatePass, finding.Identity);
            var candidate = SoloRepairEvidenceTests.FocusedResult(finding, sha, trx, candidatePass, true, finding.Identity);
            if (!runBaselineArm) return Task.FromResult(candidate);
            var baselineSha = RunGit(worktreePath, "merge-base", "HEAD", "main");
            var baselineTrx = Path.Combine(root, baselineSha + "-baseline.trx");
            var baselinePass = AttributionBaselinePass!.Value;
            WriteTrx(baselineTrx, baselinePass, finding.Identity);
            var baseline = SoloRepairEvidenceTests.FocusedResult(finding, baselineSha, baselineTrx, baselinePass, true,
                finding.Identity, FindingEvidenceArm.Baseline);
            return Task.FromResult(candidate with { Arms = [Assert.Single(candidate.Arms!), Assert.Single(baseline.Arms!)] });
        }

        public Task<AcceptanceVerificationResult> RunOwnedAsync(string worktreePath, GoalId? goalId,
            IReadOnlyList<string>? changedFiles, int? stableSlotIndex, DotnetBuildEnvironmentLease? stableSlotLease,
            IAcceptanceAttemptExecutionOwner executionOwner)
        {
            var id = Assert.IsType<GoalId>(goalId);
            NormalRuns.Add((id, RunGit(worktreePath, "rev-parse", "HEAD")));
            var check = new AcceptanceCheckResult("normal candidate acceptance", NormalPass, NormalPass ? 0 : 1,
                NormalPass ? "pass" : "branch-local failure");
            return Task.FromResult(new AcceptanceVerificationResult(NormalPass, false, check.ExitCode, check.OutputTail,
                Checks: [check]));
        }
    }
}
