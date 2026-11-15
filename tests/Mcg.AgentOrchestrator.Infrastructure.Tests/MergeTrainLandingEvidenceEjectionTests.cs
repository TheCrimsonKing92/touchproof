using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each case owns its repositories, stores and cleanup hooks.
public sealed class MergeTrainLandingEvidenceEjectionTests(
    CohortCandidateTemplate template, WorktreeSetupTemplate setupTemplate)
    : AcceptanceCohortWorkflowTests, IClassFixture<CohortCandidateTemplate>, IClassFixture<WorktreeSetupTemplate>
{
    [Fact]
    public void DenylistHold_WithThreeMembers_HoldsWholeTrainBeforeMutation()
    {
        // Shared infrastructure supplies the resource key required for train admission.
        string[] paths = [
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalWorktrees.GitOps.cs",
            "tests/Mcg.AgentOrchestrator.Core.Tests/TrainSecond.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/TrainThird.cs"];
        var copy = template.Copy("train-denylist-hold", 3, (repo, ids) =>
        {
            AddAcceptanceManifest(repo, setupTemplate);
            for (var index = 0; index < paths.Length; index++)
                CreateWorktreeCandidate(repo, ids[index], paths[index], $"member {index}");
        });
        var repo = copy.Repo;
        var cleanup = CreateIsolatedCleanupContext(repo);
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goals = Enumerable.Range(0, 3)
                .Select(index => copy.CreateCompletedGoal(kernel, index, $"Held train member {index}")).ToArray();
            for (var index = 0; index < goals.Length; index++)
            {
                kernel.RecordGoalRefinement(goals[index].Id, new RefinedSpec(goals[index].Objective,
                    ["The full train gate passes"], VerificationClass.TestVerifiable, [], []));
                kernel.MapCriterionEvidenceOwner(goals[index].Id, 0, 1, CriterionEvidenceOwner.Acceptance,
                    "test", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: copy.Revisions[index]);
            }
            var verifier = new SequenceAcceptanceVerifier([new AcceptanceVerificationResult(true, false, 0, null,
                Checks: [new AcceptanceCheckResult("merge train", true, 0, null)],
                TestResultPaths: [WritePassingTrx(repo, "denylist-hold.trx")])]);
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(kernel, workspace, verifier, AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(), cleanupHooks: cleanup.Hooks);
            var selection = ProjectTrainSelection(driver, goals);
            Assert.Equal(3, selection.Members.Count);
            // A simultaneous member gap must not turn a whole-train denylist hold into an ejection.
            kernel.RequestHumanInputDeduplicated(goals[1].Id, null,
                "Missing landing evidence", kind: HumanWaitKind.ProspectiveAcceptanceEvidence,
                evidenceOwner: "Acceptance");
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            RunGit(repo, "update-ref", $"refs/heads/{LandingExecutor.IntegrationBranchName}", main);
            var stateRepository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            stateRepository.SaveAsync(kernel).GetAwaiter().GetResult();
            var landings = new List<ConductorLandingReceipt>();
            driver.SuccessfulLandingSink = landings.Add;

            var result = driver.RunMergeTrain(selection, goals, ConductorAutonomyPolicy.Permissive);

            Assert.Equal(1, verifier.RunCount);
            Assert.Equal(MergeTrainGateOutcome.Passed, result.Receipt?.Outcome);
            Assert.True(result.Receipt?.HasAuthoritativeLandingEvidence);
            Assert.Equal(3, result.Receipt!.Identity.Members.Count);
            Assert.Equal(AcceptanceCohortLandingOutcome.RetryableHold, result.LandingOutcome);
            Assert.Contains(LandingDenylistOwnerGate.HoldReasonPrefix, result.Detail, StringComparison.Ordinal);
            Assert.Equal(goals.Select(goal => goal.Id.Value).Order(), result.MemberResults.Keys.Order());
            Assert.All(result.MemberResults.Values, member => Assert.IsType<ConductorAdvanceOutcome.Held>(member.Outcome));
            Assert.Empty(result.Ejections);
            Assert.Empty(landings);
            Assert.Equal(main, RunGitOutput(repo, "rev-parse", "main").Trim());
            Assert.Equal(main, RunGitOutput(repo, "rev-parse", $"refs/heads/{LandingExecutor.IntegrationBranchName}").Trim());
            Assert.All(goals, goal => Assert.Null(
                GoalOperationJournal.TryGetLatestLandingIntent(GoalOperationJournal.Read(repo, goal.Id))));
            var metadata = stateRepository.ListGoalMetadataAsync().GetAwaiter().GetResult();
            Assert.Equal(3, metadata.Count);
            Assert.All(metadata, row => Assert.Null(row.PromotionFact));
            using var connection = new SqliteConnection($"Data Source={Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db")}");
            connection.Open();
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM merge_train_ejections;";
            Assert.Equal(0L, (long)query.ExecuteScalar()!);
            query.CommandText = "SELECT COUNT(*) FROM merge_train_landings;";
            Assert.Equal(0L, (long)query.ExecuteScalar()!);
            AssertNoMergeTrainWorkspaces(repo);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Theory]
    [InlineData(3, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    public void MemberGap_AfterPassingGate_RegatesSurvivorsOrFallsBack(int memberCount, int gapCount)
    {
        string[] paths = [
            "tests/Mcg.AgentOrchestrator.Core.Tests/TrainFirst.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/TrainSecond.cs",
            "tests/Mcg.AgentOrchestrator.Dashboard.Tests/TrainThird.cs"];
        var copy = template.Copy($"train-evidence-gap-{memberCount}", memberCount, (repo, ids) =>
        {
            AddAcceptanceManifest(repo, setupTemplate);
            for (var index = 0; index < memberCount; index++)
                CreateWorktreeCandidate(repo, ids[index], paths[index], $"member {index}");
        });
        var repo = copy.Repo;
        var cleanup = CreateIsolatedCleanupContext(repo);
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goals = Enumerable.Range(0, memberCount)
                .Select(index => copy.CreateCompletedGoal(kernel, index, $"Train member {index}")).ToArray();
            foreach (var (goal, index) in goals.Select((goal, index) => (goal, index)))
            {
                kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                    ["The full train gate passes"], VerificationClass.TestVerifiable, [], []));
                kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
                    "test", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: copy.Revisions[index]);
            }
            AcceptanceVerificationResult Pass(string name) => new(true, false, 0, null,
                Checks: [new AcceptanceCheckResult("merge train", true, 0, null)],
                TestResultPaths: [WritePassingTrx(repo, name)]);
            var verifier = new SequenceAcceptanceVerifier([
                Pass("original-train.trx"), Pass("survivor-train.trx")]);
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(kernel, workspace, verifier, AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(), cleanupHooks: cleanup.Hooks);
            var selection = ProjectTrainSelection(driver, goals);
            var landings = new List<ConductorLandingReceipt>();
            driver.SuccessfulLandingSink = landings.Add;
            var wait = kernel.RequestHumanInputDeduplicated(goals[1].Id, null,
                "Missing landing evidence", kind: HumanWaitKind.ProspectiveAcceptanceEvidence,
                evidenceOwner: "Acceptance").Request;
            if (gapCount == 2)
                kernel.RequestHumanInputDeduplicated(goals[2].Id, null,
                    "Another missing landing receipt", kind: HumanWaitKind.ProspectiveAcceptanceEvidence,
                    evidenceOwner: "Acceptance");
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var result = driver.RunMergeTrain(selection, goals, ConductorAutonomyPolicy.Permissive);

            var storePath = Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db");
            var store = MergeTrainAcceptanceStore.OpenReadOnly(storePath);
            using var connection = new SqliteConnection($"Data Source={storePath}");
            connection.Open();
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT train_id FROM merge_train_receipts;";
            var receipts = new List<MergeTrainReceipt>();
            using (var reader = query.ExecuteReader())
                while (reader.Read())
                    receipts.Add(Assert.IsType<MergeTrainReceipt>(store.TryReadReceipt(reader.GetString(0))));
            Assert.Equal(memberCount - gapCount >= 2 ? 2 : 1, receipts.Count);
            Assert.All(receipts, receipt => Assert.True(receipt.HasAuthoritativeLandingEvidence));
            Assert.Equal(paths.Take(memberCount).Order(), verifier.ChangedFiles[0].Order());
            Assert.False(wait.IsCompleted);
            Assert.Equal(GoalStatus.Verified, goals[1].Status);
            Assert.All(goals, goal => Assert.Null(goal.LatestAcceptanceFailure));
            if (memberCount - gapCount >= 2)
            {
                Assert.Equal(2, verifier.RunCount);
                var survivors = new[] { goals[0], goals[2] };
                Assert.Equal(new[] { paths[0], paths[2] }.Order(), verifier.ChangedFiles[1].Order());
                var ejection = Assert.Single(result.Ejections);
                Assert.Equal(goals[1].Id, ejection.GoalId);
                Assert.Equal(MergeTrainEjectionReason.LandingEvidenceGap, ejection.Reason);
                Assert.Contains(wait.Id.Value, ejection.Detail, StringComparison.Ordinal);
                query.CommandText = "SELECT train_attempt_id FROM merge_train_ejections;";
                var recorded = Assert.Single(store.ReadAttemptEjections((string)query.ExecuteScalar()!));
                Assert.Equal(ejection.GoalId, recorded.GoalId);
                Assert.Equal(ejection.Reason, recorded.Reason);
                Assert.Equal(ejection.Detail, recorded.Detail);
                Assert.Empty(recorded.ConflictPaths);
                Assert.Equal(survivors.Select(goal => goal.Id), result.Receipt!.Identity.Members.Select(member => member.GoalId));
                Assert.Equal(survivors.Select(goal => goal.Id.Value).Order(), result.MemberResults.Keys.Order());
                Assert.All(result.MemberResults.Values, member => Assert.IsType<ConductorAdvanceOutcome.Executed>(member.Outcome));
                Assert.Equal(2, landings.Count);
                Assert.Equal(survivors.Select(goal => goal.Id.Value).Order(), landings.Select(landing => landing.GoalId).Order());
                var landed = RunGitOutput(repo, "rev-parse", "main").Trim();
                Assert.NotEqual(main, landed);
                Assert.All(landings, landing => Assert.Equal(landed, landing.LandingSha));
                Assert.Equal(result.Receipt.Identity.TrainTreeRevision,
                    RunGitOutput(repo, "rev-parse", "main^{tree}").Trim());
                Assert.DoesNotContain(paths[1], RunGitOutput(repo, "ls-tree", "-r", "--name-only", "main"));
                Assert.All(survivors, goal => Assert.Equal(GoalStatus.Completed, goal.Status));
            }
            else
            {
                Assert.Equal(1, verifier.RunCount);
                Assert.Null(result.Receipt);
                Assert.Null(result.LandingOutcome);
                Assert.Empty(result.MemberResults);
                Assert.Empty(result.Ejections);
                Assert.Contains("fallback=ordinary", result.Detail, StringComparison.Ordinal);
                Assert.Contains(wait.Id.Value, result.Detail, StringComparison.Ordinal);
                Assert.Empty(landings);
                Assert.Equal(main, RunGitOutput(repo, "rev-parse", "main").Trim());
                Assert.All(goals, goal => Assert.Equal(GoalStatus.Verified, goal.Status));
                query.CommandText = "SELECT COUNT(*) FROM merge_train_landings;";
                Assert.Equal(0L, (long)query.ExecuteScalar()!);
                query.CommandText = "SELECT COUNT(*) FROM merge_train_ejections;";
                Assert.Equal(0L, (long)query.ExecuteScalar()!);
            }
            AssertNoMergeTrainWorkspaces(repo);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }
}
