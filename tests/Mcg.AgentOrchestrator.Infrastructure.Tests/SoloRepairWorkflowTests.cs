using System.Text.Json;
using System.Text.Json.Serialization;
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
    public async Task CliAdmission_BlocksLegacyRepairObligationAfterReload()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        fixture.Git("checkout", "main");
        var workspace = Workspace(fixture);
        var goal = NewCandidate(fixture, fixture.Kernel);
        var checkout = Worktree(fixture, goal);
        var main = fixture.Git("rev-parse", "main");
        var candidate = RunGit(checkout, "rev-parse", "HEAD");
        var original = SoloRepairReplayTests.Finding() with { Candidate = candidate, Main = main, Baseline = main };
        var path = Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName);
        new SoloBaselineRepairStore(path).Retain(goal.Id.Value, [original]);
        SoloRepairReplayTests.ExecuteSql(path,
            "UPDATE solo_repair_finding SET finding = json_remove(finding, '$.LaneEnvironmentVerified')");
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(fixture.Kernel);
        var reloaded = await repository.LoadAsync();
        goal = reloaded.GetGoal(goal.Id);
        var verifier = new ControlledVerifier(fixture.Root) { NormalPass = true };
        var context = CreateCliContext(reloaded, workspace, goal, verifier);
        var store = new SoloBaselineRepairStore(path);
        Assert.False(Assert.Single(store.ReadFindings(goal.Id.Value)).LaneEnvironmentVerified);

        var pair = new GateReadyCandidateRevisionPair(candidate, main);
        var obligation = Assert.Single(SoloBaselineRepairCoordinator.SoloRepairFindings(store, goal, candidate, main));
        Assert.Equal(original.Request, obligation.Request);
        Assert.Equal(SoloAcceptanceFailureReceiptReader.LaneEvidenceUnavailable, obligation.Reason);
        Assert.True(CliWorkspaceMerge.ReportRepairHoldBeforeVerification(context, goal, pair, checkout, false, out _, out _));
        Assert.False(CliCommandHandlers.RunAcceptanceWorkspaceMergeCore(context));
        Assert.Empty(verifier.NormalRuns);
        Assert.Equal(1, SoloRepairReplayTests.Count(path, "solo_repair_dependency"));
        Assert.Equal(main, fixture.Git("rev-parse", "main"));
    }

    [Fact]
    public void CliFastForwardSynchronization_PreservesUntrackedRootFile()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        fixture.Git("checkout", "main");
        var goal = NewCandidate(fixture, fixture.Kernel);
        var main = fixture.Git("rev-parse", "main");
        var candidate = RunGit(Worktree(fixture, goal), "rev-parse", "HEAD");
        var untracked = Path.Combine(fixture.Repo, "operator-notes.txt");
        File.WriteAllText(untracked, "keep this untracked file");

        var advanced = GoalWorktrees.TryAdvanceFastForwardRefs(fixture.Repo, goal.Id, "refs/heads/main",
            main, candidate, () => null, new SystemClock(), fromAcceptance: true);
        var receipt = Assert.IsType<CliFastForwardReceipt>(advanced.FastForwardReceipt);
        var synced = GoalWorktrees.SynchronizeCliFastForward(fixture.Repo, goal.Id, receipt);

        Assert.True(synced.FastForwarded, synced.Message);
        Assert.Equal(candidate, fixture.Git("rev-parse", "main"));
        Assert.Equal(goal.Id.Value, File.ReadAllText(Path.Combine(fixture.Repo, "README.md")));
        Assert.Equal("keep this untracked file", File.ReadAllText(untracked));
        Assert.Contains("?? operator-notes.txt", fixture.Git("status", "--short"));
    }

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
        Assert.Equal(squash, SoloBaselineRepairCoordinator.ResolveSoloRepairLanding(fixture.Workspace, fixture.Goal.Id,
            fixture.Git("rev-parse", "main")));
        Assert.Null(SoloBaselineRepairCoordinator.ResolveSoloRepairLanding(fixture.Workspace, fixture.Goal.Id, fixture.Delivery.Main));
    }

    [Theory]
    [InlineData(true, "main")]
    [InlineData(true, "candidate")]
    [InlineData(true, "envelope")]
    [InlineData(true, "trx")]
    [InlineData(false, "envelope")]
    [InlineData(false, "trx")]
    [InlineData(true, "abbreviated failure")]
    [InlineData(false, "abbreviated failure")]
    [InlineData(true, "outcomeless failure")]
    [InlineData(false, "outcomeless failure")]
    [InlineData(true, "unreadable journal")]
    [InlineData(false, "unreadable journal")]
    public void Publication_RechecksRepairEvidenceAfterAdmission(bool pullRequest, string invalidation)
    {
        using var fixture = new PullRequestLandingTestFixture();
        var workspace = pullRequest ? fixture.Workspace : fixture.Workspace with { Delivery = ProjectDelivery.Direct };
        var store = new SoloBaselineRepairStore(Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName));
        var finding = CapturePublicationFinding(fixture);
        store.Retain(fixture.Goal.Id.Value, [finding]);
        store.ReserveGroup(finding.GroupKey, "{}");
        store.AcknowledgeRepair(finding.GroupKey, Guid.NewGuid().ToString("N"));
        RecordPassedValidation(store, finding, fixture.Main);
        var coordinator = new SoloBaselineRepairCoordinator(workspace, fixture.Kernel, new ControlledVerifier(fixture.Repository),
            _ => new(fixture.Candidate, fixture.Main), _ => null, () => throw new InvalidOperationException(),
            _ => throw new InvalidOperationException(), (_, _, _, _) => throw new InvalidOperationException(),
            (_, _) => throw new InvalidOperationException());
        var verification = new AcceptanceVerificationResult(true, false, 0, null) { AttemptId = PullRequestLandingTestFixture.Attempt };
        var summary = AcceptanceVerificationSummary.FromVerification(verification, [], [], fixture.Candidate, fixture.Main,
            null, null, null);
        Assert.Null(coordinator.ObserveAcceptedPair(fixture.Goal, fixture.Goal.Id.Value[..8], Policy, summary));
        var gateKey = SoloBaselineRepairStore.NormalGateKey(fixture.Goal.Id.Value, fixture.Candidate, fixture.Main);
        Assert.Equal(PullRequestLandingTestFixture.Attempt, store.ReadCheck(gateKey)?.Detail);
        var journal = GoalOperationJournal.ReadStrict(fixture.Repository, fixture.Goal.Id);
        var pass = Assert.Single(journal.Entries, entry => entry.AcceptanceOutcome == "passed");
        var beforePublication = 0;

        var result = LandingExecutor.Execute(fixture.Kernel, fixture.Goal, workspace,
            pullRequestPublisher: new GitHubPullRequestPublisher(fixture.Forge), mutationBlocker: () =>
            {
                // Direct landing also calls this hook before preparing its candidate.
                if (++beforePublication == (pullRequest ? 1 : 2))
                {
                    if (invalidation == "envelope")
                        File.Delete(Assert.IsType<SoloAcceptanceArtifact>(finding.Proof).Path);
                    else if (invalidation == "trx")
                        File.AppendAllText(Assert.Single(finding.CandidateArtifacts).Path, " ");
                    else if (invalidation == "abbreviated failure")
                        GoalOperationJournal.AcceptanceFailed(fixture.Repository, fixture.Goal, "conductor:acceptance",
                            fixture.Candidate[..12], fixture.Main, attemptStartedAt: pass.At.AddSeconds(1));
                    else if (invalidation == "outcomeless failure")
                        AppendJournalEntry(journal.Path, new("newer-failed-attempt", fixture.Goal.Id, "Acceptance",
                            GoalOperationStatus.Failed, pass.At.AddSeconds(1), null, fixture.Candidate, fixture.Main));
                    else if (invalidation == "unreadable journal")
                        SharedJsonlFile.AppendLine(journal.Path, """{"operation":"acceptance" """);
                    else
                    {
                        var branch = invalidation == "main" ? "main" : GoalWorktrees.BranchName(fixture.Goal.Id);
                        var revision = invalidation == "main" ? fixture.Candidate : fixture.Main;
                        RunGit(fixture.Repository, "update-ref", "refs/heads/" + branch, revision);
                    }
                }
                return null;
            });

        Assert.Equal(pullRequest ? 1 : 2, beforePublication);
        var expectedBlocker = invalidation switch
        {
            "abbreviated failure" or "outcomeless failure" => "normal acceptance is pending for the current revision pair",
            "unreadable journal" => "Restore a readable goal operation journal before retrying acceptance",
            _ => "revision pair changed after baseline repair validation or normal acceptance"
        };
        Assert.Contains(expectedBlocker, result.Message);
        Assert.False(result.MainAdvanced);
        Assert.Empty(fixture.Forge.Calls);
        Assert.Null(fixture.Goal.DeliveredForReview);
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
        Assert.Equal(invalidation == "main" ? fixture.Candidate : fixture.Main,
            RunGit(fixture.Repository, "rev-parse", "main"));
    }

    [Theory]
    [InlineData("repair", "baseline repair and original-selection validation")]
    [InlineData("normal gate", "normal acceptance is pending")]
    [InlineData("moved candidate", "revision pair changed")]
    public void CliFastForward_HoldsMainUntilRepairAndExactGatePass(string pending, string expectedReason)
    {
        using var fixture = new PullRequestLandingTestFixture();
        var (context, store, finding) = PrepareCliRepair(fixture, new ControlledVerifier(fixture.Repository));
        if (pending != "repair") RecordPassedValidation(store, finding, fixture.Main);
        GateReadyCandidateRevisionPair? testedPair = null;
        if (pending == "moved candidate")
        {
            testedPair = new(fixture.Candidate, fixture.Main);
            GoalOperationJournal.AcceptanceGatePassed(fixture.Repository, fixture.Goal, "acceptance", fixture.Candidate,
                fixture.Main, requiredChecksValidated: true);
            store.AcknowledgeEffect(SoloBaselineRepairStore.NormalGateKey(fixture.Goal.Id.Value, fixture.Candidate, fixture.Main), "prior-attempt");
        }

        var merge = CliWorkspaceMerge.MergeWithPublication(context, fixture.Goal, out var publication,
            () =>
            {
                if (pending == "moved candidate")
                {
                    var path = GoalWorktrees.TryResolve(fixture.Repository, fixture.Goal.Id)!;
                    RunGit(path, "commit", "--allow-empty", "-m", "Move candidate before merge");
                }
                return null;
            }, out var repairBlock, testedPair);

        Assert.False(Assert.IsType<GoalWorktreeMergeResult>(merge).FastForwarded);
        Assert.Contains(expectedReason, repairBlock);
        Assert.Null(publication);
        Assert.Equal(fixture.Main, RunGit(fixture.Repository, "rev-parse", "main"));
    }

    [Theory]
    [InlineData(true, true, "normal acceptance is pending")]
    [InlineData(true, false, null)]
    [InlineData(true, null, "incomplete revision pair")]
    [InlineData(null, true, "incomplete revision pair")]
    [InlineData(null, null, "incomplete revision pair")]
    [InlineData(false, null, null)]
    public async Task CliFastForward_UsesLatestNormalOutcomeAfterRestart(
        bool? failureCandidateMatches, bool? failureMainMatches, string? expectedBlocker)
    {
        using var fixture = new PullRequestLandingTestFixture();
        var verifier = new ControlledVerifier(fixture.Repository);
        var (context, store, finding) = PrepareCliRepair(fixture, verifier);
        RecordPassedValidation(store, finding, fixture.Main);
        var pair = new GateReadyCandidateRevisionPair(fixture.Candidate, fixture.Main);
        var key = SoloBaselineRepairStore.NormalGateKey(fixture.Goal.Id.Value, fixture.Candidate, fixture.Main);
        var initialJournal = GoalOperationJournal.ReadStrict(fixture.Repository, fixture.Goal.Id);
        var initialPass = Assert.Single(initialJournal.Entries, entry =>
            entry.AcceptanceOutcome == "passed" && entry.HasCandidate(fixture.Candidate, fixture.Main));
        GoalOperationJournal.AcceptanceFailed(fixture.Repository, fixture.Goal, "conductor:acceptance", fixture.Candidate,
            null, attemptStartedAt: initialPass.At.AddSeconds(-1));
        store.AcknowledgeEffect(key, "prior-attempt");
        var failureCandidate = failureCandidateMatches switch { true => fixture.Candidate, false => fixture.Main, _ => null };
        var failureMain = failureMainMatches switch { true => fixture.Main, false => fixture.Candidate, _ => null };
        GoalOperationJournal.AcceptanceFailed(fixture.Repository, fixture.Goal, "conductor:acceptance", failureCandidate,
            failureMain, attemptStartedAt: initialPass.At.AddSeconds(1));

        var repository = new SqliteOrchestratorStateRepository(context.Workspace.SqliteStatePath);
        await repository.SaveAsync(fixture.Kernel);
        var restarted = await repository.LoadAsync();
        var goal = restarted.GetGoal(fixture.Goal.Id);
        var resumed = CreateCliContext(restarted, context.Workspace, goal, verifier);
        var journal = GoalOperationJournal.ReadActive(fixture.Repository, goal.Id);
        var failure = journal.Entries.Last(entry => entry.AcceptanceOutcome == "failed");
        Assert.Equal(failureCandidate, failure.BranchHeadSha);
        Assert.Equal(failureMain, failure.MainHeadSha);
        var current = GoalOperationJournal.NewestAcceptanceOutcomeForCandidate(journal, fixture.Candidate, fixture.Main);
        Assert.Equal(failureCandidateMatches == true && failureMainMatches == true ? "failed" : "passed", current?.AcceptanceOutcome);
        Assert.Equal("passed", store.ReadCheck(key)?.Outcome);

        var merge = CliWorkspaceMerge.MergeWithPublication(resumed, goal, out var publication,
            () => null, out var repairBlock, pair);

        Assert.Equal(expectedBlocker is null, Assert.IsType<GoalWorktreeMergeResult>(merge).FastForwarded);
        Assert.Equal(expectedBlocker is null ? fixture.Candidate : fixture.Main, RunGit(fixture.Repository, "rev-parse", "main"));
        if (expectedBlocker is not null)
        {
            Assert.Contains(expectedBlocker, repairBlock);
            Assert.Null(publication);
            Assert.False(CliCommandHandlers.RunAcceptanceWorkspaceMergeCore(resumed, skipVerify: true));
            Assert.Empty(verifier.NormalRuns);
            Assert.Equal(fixture.Main, RunGit(fixture.Repository, "rev-parse", "main"));
        }
    }

    [Theory]
    [InlineData("acceptance", GoalOperationStatus.Failed, "failed", true, false)]
    [InlineData("acceptance", GoalOperationStatus.Failed, "failed", false, true)]
    [InlineData("acceptance", GoalOperationStatus.Completed, "gate-passed", false, true)]
    [InlineData("acceptance", GoalOperationStatus.Failed, null, false, false)]
    [InlineData("Acceptance", GoalOperationStatus.Failed, null, false, false)]
    [InlineData("Conductor:Acceptance", GoalOperationStatus.Failed, null, false, false)]
    [InlineData("conductor:acceptance", GoalOperationStatus.Begin, null, false, false)]
    [InlineData("Acceptance", GoalOperationStatus.Begin, null, false, false)]
    public async Task CliFastForward_NewerJournalRowsCanInvalidatePriorPass(
        string operation, GoalOperationStatus status, string? outcome, bool abbreviateCandidate, bool abbreviateMain)
    {
        using var fixture = new PullRequestLandingTestFixture();
        var verifier = new ControlledVerifier(fixture.Repository);
        var (context, store, finding) = PrepareCliRepair(fixture, verifier);
        RecordPassedValidation(store, finding, fixture.Main);
        var pair = new GateReadyCandidateRevisionPair(fixture.Candidate, fixture.Main);
        GoalOperationJournal.AcceptanceGatePassed(fixture.Repository, fixture.Goal, "acceptance", fixture.Candidate,
            fixture.Main, requiredChecksValidated: true);
        var journal = GoalOperationJournal.ReadStrict(fixture.Repository, fixture.Goal.Id);
        var pass = Assert.Single(journal.Entries, entry => entry.RequiredChecksValidated);
        Assert.Equal(pass.IdempotencyKey,
            SoloBaselineRepairCoordinator.ReadNormalGate(fixture.Repository, store, fixture.Goal, pair).RecoverableAttempt);

        var begin = status == GoalOperationStatus.Begin;
        var recordedCandidate = abbreviateCandidate ? fixture.Candidate[..12] : fixture.Candidate;
        var recordedMain = abbreviateMain ? fixture.Main[..12] : fixture.Main;
        var newer = new GoalOperationJournalEntry("newer-attempt", fixture.Goal.Id,
            operation, status, pass.At.AddSeconds(1), null,
            begin ? null : recordedCandidate, begin ? null : recordedMain, outcome)
        {
            RequiredChecksValidated = status == GoalOperationStatus.Completed
        };
        AppendJournalEntry(journal.Path, newer);

        var repository = new SqliteOrchestratorStateRepository(context.Workspace.SqliteStatePath);
        await repository.SaveAsync(fixture.Kernel);
        var restarted = await repository.LoadAsync();
        var goal = restarted.GetGoal(fixture.Goal.Id);
        var resumed = CreateCliContext(restarted, context.Workspace, goal, verifier);
        var reloadedJournal = GoalOperationJournal.ReadStrict(fixture.Repository, goal.Id);
        Assert.Equal(newer, reloadedJournal.Entries.Last());
        Assert.Equal(begin ? pass.IdempotencyKey : null,
            SoloBaselineRepairCoordinator.ReadNormalGate(fixture.Repository, store, goal, pair).RecoverableAttempt);

        var key = SoloBaselineRepairStore.NormalGateKey(goal.Id.Value, fixture.Candidate, fixture.Main);
        store.AcknowledgeEffect(key, pass.IdempotencyKey);
        var merge = CliWorkspaceMerge.MergeWithPublication(resumed, goal, out var publication,
            () => null, out var repairBlock, pair);

        Assert.Equal(begin, Assert.IsType<GoalWorktreeMergeResult>(merge).FastForwarded);
        Assert.Equal(begin ? fixture.Candidate : fixture.Main, RunGit(fixture.Repository, "rev-parse", "main"));
        if (!begin)
        {
            Assert.Contains("normal acceptance is pending", repairBlock);
            Assert.Null(publication);
        }
        Assert.Empty(verifier.NormalRuns);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CliFastForward_UnreadableNormalOutcomeBlocksAfterRestart(bool acknowledged)
    {
        using var fixture = new PullRequestLandingTestFixture();
        var verifier = new ControlledVerifier(fixture.Repository);
        var (context, store, finding) = PrepareCliRepair(fixture, verifier);
        RecordPassedValidation(store, finding, fixture.Main);
        var pair = new GateReadyCandidateRevisionPair(fixture.Candidate, fixture.Main);
        var key = SoloBaselineRepairStore.NormalGateKey(fixture.Goal.Id.Value, fixture.Candidate, fixture.Main);
        GoalOperationJournal.AcceptanceGatePassed(fixture.Repository, fixture.Goal, "acceptance", fixture.Candidate,
            fixture.Main, requiredChecksValidated: true);
        if (acknowledged) store.AcknowledgeEffect(key, "prior-attempt");
        var prior = SoloBaselineRepairCoordinator.ReadNormalGate(fixture.Repository, store, fixture.Goal, pair);
        Assert.Equal(acknowledged, prior.Acknowledged);
        Assert.NotNull(prior.RecoverableAttempt);
        Assert.Null(prior.Blocker);

        GoalOperationJournal.AcceptanceFailed(fixture.Repository, fixture.Goal, "conductor:acceptance", fixture.Candidate,
            fixture.Main);
        var journal = GoalOperationJournal.ReadStrict(fixture.Repository, fixture.Goal.Id);
        Assert.Equal("failed", GoalOperationJournal.NewestAcceptanceOutcomeForCandidate(journal, fixture.Candidate,
            fixture.Main)?.AcceptanceOutcome);
        var lines = File.ReadAllLines(journal.Path);
        lines[^1] = lines[^1][..^1];
        File.WriteAllLines(journal.Path, lines);

        var repository = new SqliteOrchestratorStateRepository(context.Workspace.SqliteStatePath);
        await repository.SaveAsync(fixture.Kernel);
        var restarted = await repository.LoadAsync();
        var goal = restarted.GetGoal(fixture.Goal.Id);
        var resumed = CreateCliContext(restarted, context.Workspace, goal, verifier);
        Assert.Equal(acknowledged ? "passed" : null, store.ReadCheck(key)?.Outcome);
        Assert.Throws<FormatException>(() => GoalOperationJournal.ReadStrict(fixture.Repository, goal.Id));

        var merge = CliWorkspaceMerge.MergeWithPublication(resumed, goal, out var publication,
            () => null, out var repairBlock, pair);

        Assert.False(Assert.IsType<GoalWorktreeMergeResult>(merge).FastForwarded);
        Assert.Contains("Restore a readable goal operation journal", repairBlock);
        Assert.Null(publication);
        Assert.False(CliCommandHandlers.RunAcceptanceWorkspaceMergeCore(resumed, skipVerify: true));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var reloaded = await repository.LoadAsync();
            var retry = CreateCliContext(reloaded, context.Workspace, reloaded.GetGoal(goal.Id), verifier);
            var output = CaptureConsole(() => Assert.False(CliCommandHandlers.RunAcceptanceWorkspaceMergeCore(retry)));
            Assert.Contains("Restore a readable goal operation journal", output);
            Assert.Contains(journal.Path, output);
            Assert.Empty(verifier.NormalRuns);
            Assert.Equal(lines, File.ReadAllLines(journal.Path));
            Assert.Equal(fixture.Main, RunGit(fixture.Repository, "rev-parse", "main"));
        }
        Assert.Equal(acknowledged ? "passed" : null, store.ReadCheck(key)?.Outcome);
    }

    [Fact]
    public void CliAcceptance_WaitsForOriginalSelectionAndRequiresNormalVerification()
    {
        using var fixture = new PullRequestLandingTestFixture();
        var verifier = new ControlledVerifier(fixture.Repository);
        var (context, store, finding) = PrepareCliRepair(fixture, verifier);

        Assert.False(CliCommandHandlers.RunAcceptanceWorkspaceMergeCore(context));
        Assert.Empty(verifier.NormalRuns);
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
        Assert.Equal(fixture.Main, RunGit(fixture.Repository, "rev-parse", "main"));

        RecordPassedValidation(store, finding, fixture.Main);
        Assert.False(CliCommandHandlers.RunAcceptanceWorkspaceMergeCore(context, skipVerify: true));
        Assert.Empty(verifier.NormalRuns);
        Assert.Equal(fixture.Main, RunGit(fixture.Repository, "rev-parse", "main"));

        Assert.False(CliCommandHandlers.RunAcceptanceWorkspaceMergeCore(context));
        Assert.Equal((fixture.Goal.Id, fixture.Candidate), Assert.Single(verifier.NormalRuns));
        Assert.Null(store.ReadCheck(SoloBaselineRepairStore.NormalGateKey(fixture.Goal.Id.Value, fixture.Candidate, fixture.Main)));
        Assert.Equal(fixture.Main, RunGit(fixture.Repository, "rev-parse", "main"));
    }

    [Theory]
    [InlineData("candidate")]
    [InlineData("main")]
    public void CliFastForward_RejectsRefMovementAtTheGitMutation(string moved)
    {
        using var fixture = new PullRequestLandingTestFixture();
        var (context, store, finding) = PrepareCliRepair(fixture, new ControlledVerifier(fixture.Repository));
        RecordPassedValidation(store, finding, fixture.Main);
        GoalOperationJournal.AcceptanceGatePassed(fixture.Repository, fixture.Goal, "acceptance", fixture.Candidate,
            fixture.Main, requiredChecksValidated: true);
        store.AcknowledgeEffect(SoloBaselineRepairStore.NormalGateKey(fixture.Goal.Id.Value, fixture.Candidate, fixture.Main), "prior-attempt");
        var mutationPath = moved == "main" ? fixture.Repository : GoalWorktrees.TryResolve(fixture.Repository, fixture.Goal.Id)!;
        var thread = Environment.CurrentManagedThreadId;
        var prior = Volatile.Read(ref GitCli.LaunchObserver);
        GitCli.GitResult? mutation = null;
        Action<string> observer = command =>
        {
            prior?.Invoke(command);
            if (Environment.CurrentManagedThreadId == thread && command == "update-ref" && mutation is null)
                mutation = GitCli.Run(mutationPath, "commit", "--allow-empty", "-m", $"Advance {moved} after CLI admission");
        };
        Assert.Same(prior, Interlocked.CompareExchange(ref GitCli.LaunchObserver, observer, prior));
        try
        {
            var merge = CliWorkspaceMerge.MergeWithPublication(context, fixture.Goal, out var publication,
                () => null, out var repairBlock, new(fixture.Candidate, fixture.Main));

            Assert.True(mutation?.Succeeded, mutation?.Error);
            Assert.False(Assert.IsType<GoalWorktreeMergeResult>(merge).FastForwarded);
            Assert.Contains("revision pair changed", repairBlock);
            Assert.Null(publication);
            var currentMain = RunGit(fixture.Repository, "rev-parse", "main");
            if (moved == "main")
            {
                Assert.NotEqual(fixture.Main, currentMain);
                Assert.Equal(fixture.Main, RunGit(fixture.Repository, "rev-parse", "main^"));
                Assert.Equal(fixture.Candidate, RunGit(fixture.Repository, "rev-parse", GoalWorktrees.BranchName(fixture.Goal.Id)));
            }
            else
            {
                Assert.Equal(fixture.Main, currentMain);
                Assert.Equal(fixture.Candidate, RunGit(mutationPath, "rev-parse", "HEAD^"));
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref GitCli.LaunchObserver, prior, observer);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CliAcceptance_RetainsSupersededSoloEnvelopeBeforeAnyNewGate(bool routeToHold)
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        fixture.Git("checkout", "main");
        var workspace = Workspace(fixture);
        var goal = NewCandidate(fixture, fixture.Kernel);
        var failure = await ProduceFailure(fixture, goal);
        var receipt = Assert.IsType<SoloAcceptanceFailureReceipt>(failure.SoloFailureReceipt);
        fixture.Kernel.RecordAcceptanceFailure(goal.Id, failure.FailedChecks!, failure.BranchHeadSha, failure.MainHeadSha,
            failure.CheckAttributions, failure.BaselineAttestation, failure.AttemptId, receipt);
        if (routeToHold) fixture.Kernel.RouteRecordedAcceptanceFailure(goal.Id, "Hold the original solo failure.");

        fixture.Git("commit", "--allow-empty", "-m", "Advance main before CLI acceptance");
        var currentMain = fixture.Git("rev-parse", "main");
        var storePath = Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName);
        Assert.False(File.Exists(storePath));
        var verifier = new ControlledVerifier(fixture.Root);
        var context = CreateCliContext(fixture.Kernel, workspace, goal, verifier);

        Assert.False(CliCommandHandlers.RunAcceptanceWorkspaceMergeCore(context));
        Assert.Empty(verifier.NormalRuns);
        Assert.Equal(currentMain, fixture.Git("rev-parse", "main"));
        var finding = Assert.Single(new SoloBaselineRepairStore(storePath).ReadFindings(goal.Id.Value));
        Assert.False(finding.Inherited);
        Assert.Equal(First, finding.Identity);
        Assert.Equal($"{GoalAcceptanceVerifier.ProjectLabel(Project)}: FullyQualifiedName~{First}", finding.Request);
        Assert.Equal(receipt.Evidence, finding.Proof);
    }

    [Fact]
    public async Task CliAcceptance_RetainsExecutedInheritedFailureAndRoutesItAfterReload()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        fixture.Git("checkout", "main");
        var workspace = Workspace(fixture);
        await new PortfolioStore(workspace.PortfolioStorePath).AddEpicAsync("baseline-maintenance");
        var goal = NewCandidate(fixture, fixture.Kernel);
        var context = CreateCliContext(fixture.Kernel, workspace, goal,
            fixture.CreateVerifier(Worktree(fixture, goal), baselineFails: true, singleRow: true));

        Assert.False(CliCommandHandlers.RunAcceptanceWorkspaceMergeCore(context));
        Assert.True(fixture.CandidateRuns > 0);
        Assert.True(fixture.BaselineRuns > 0);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(fixture.Kernel);
        var restarted = await repository.LoadAsync();
        var reloaded = restarted.GetGoal(goal.Id);
        var failure = Assert.IsType<AcceptanceFailureSummary>(reloaded.LatestAcceptanceFailure);
        var receipt = Assert.IsType<SoloAcceptanceFailureReceipt>(failure.SoloFailureReceipt);
        Assert.Null(receipt.CaptureError);
        Assert.False(string.IsNullOrWhiteSpace(failure.AttemptId));
        Assert.Equal(receipt.AttemptId, failure.AttemptId);
        Assert.Equal(receipt.CandidateSha, failure.BranchHeadSha);
        Assert.Equal(receipt.MainSha, failure.MainHeadSha);
        Assert.Equal(AcceptanceFailureOrigin.Inherited, Assert.Single(failure.CheckAttributions!).Origin);

        Assert.True(restarted.RouteRecordedAcceptanceFailure(goal.Id, "Route the recorded CLI acceptance failure."));
        AssertHold(Driver(restarted, workspace, new ControlledVerifier(fixture.Root)).AdvanceOnce(reloaded, Policy),
            SoloHoldReasons.RepairPending);
        var storePath = Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName);
        var store = new SoloBaselineRepairStore(storePath);
        var finding = Assert.Single(store.ReadFindings(goal.Id.Value));
        Assert.Equal(First, finding.Identity);
        Assert.Equal(receipt.Evidence, finding.Proof);
        Assert.NotNull(store.ReadGroup(finding.GroupKey)?.RepairGoal);
        Assert.Equal(1, SoloRepairReplayTests.Count(storePath, "solo_repair_dependency"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CliAcceptance_ReusesNormalGateAfterRestart(bool beforeAcknowledgement)
    {
        using var fixture = new PullRequestLandingTestFixture();
        var verifier = new ControlledVerifier(fixture.Repository) { NormalPass = true };
        var (context, store, finding) = PrepareCliRepair(fixture, verifier,
            beforeAcknowledgement ? null : _ => throw new IOException("Stopped after normal acceptance acknowledgement."));
        RecordPassedValidation(store, finding, fixture.Main);
        var repository = new SqliteOrchestratorStateRepository(context.Workspace.SqliteStatePath);
        await repository.SaveAsync(fixture.Kernel);

        var key = SoloBaselineRepairStore.NormalGateKey(fixture.Goal.Id.Value, fixture.Candidate, fixture.Main);
        if (beforeAcknowledgement)
        {
            var storePath = Path.Combine(context.Workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName);
            SoloRepairReplayTests.ExecuteSql(storePath, $"""
                CREATE TRIGGER stop_gate_ack BEFORE INSERT ON solo_repair_check
                WHEN NEW.request_key = '{key}'
                BEGIN SELECT RAISE(ABORT, 'lost normal gate acknowledgement'); END
                """);
            Assert.Contains("lost normal gate acknowledgement",
                Assert.Throws<SqliteException>(() => CliCommandHandlers.RunAcceptanceWorkspaceMergeCore(context)).Message);
            Assert.Null(store.ReadCheck(key));
            SoloRepairReplayTests.ExecuteSql(storePath, "DROP TRIGGER stop_gate_ack");
        }
        else
        {
            Assert.Equal("Stopped after normal acceptance acknowledgement.",
                Assert.Throws<IOException>(() => CliCommandHandlers.RunAcceptanceWorkspaceMergeCore(context)).Message);
            Assert.Equal("passed", store.ReadCheck(key)?.Outcome);
        }

        var journal = GoalOperationJournal.ReadActive(fixture.Repository, fixture.Goal.Id);
        var pass = GoalOperationJournal.NewestAcceptanceOutcomeForCandidate(journal, fixture.Candidate, fixture.Main);
        Assert.Equal("gate-passed", pass?.AcceptanceOutcome);
        Assert.True(pass?.RequiredChecksValidated);
        Assert.Null(SoloBaselineRepairCoordinator.ReadNormalGate(fixture.Repository, store, fixture.Goal,
            new(fixture.Candidate, fixture.Candidate)).RecoverableAttempt);
        Assert.Equal(fixture.Main, RunGit(fixture.Repository, "rev-parse", "main"));

        var restarted = await repository.LoadAsync();
        var resumed = CreateCliContext(restarted, context.Workspace, restarted.GetGoal(fixture.Goal.Id), verifier);
        Assert.True(CliCommandHandlers.RunAcceptanceWorkspaceMergeCore(resumed));
        Assert.Equal((fixture.Goal.Id, fixture.Candidate), Assert.Single(verifier.NormalRuns));
        Assert.Equal("passed", store.ReadCheck(key)?.Outcome);
        Assert.Equal(fixture.Candidate, RunGit(fixture.Repository, "rev-parse", "main"));
    }

    [Fact]
    public async Task CliAcceptance_DoesNotRecoverLegacyPassWithMissingRequiredCheck()
    {
        using var fixture = new PullRequestLandingTestFixture();
        var verifier = new ControlledVerifier(fixture.Repository) { NormalPass = true };
        var (context, store, finding) = PrepareCliRepair(fixture, verifier);
        RecordPassedValidation(store, finding, fixture.Main);
        var worktree = GoalWorktrees.TryResolve(fixture.Repository, fixture.Goal.Id)!;
        const string coreSource = "src/Mcg.AgentOrchestrator.Core/GateControl.cs";
        const string coreTestProject = "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj";
        const string acceptanceManifest = "config/acceptance-manifest.json";
        var coreFile = Path.Combine(worktree, coreSource);
        Directory.CreateDirectory(Path.GetDirectoryName(coreFile)!);
        File.WriteAllText(coreFile, "internal sealed class GateControl { }");
        var projectFile = Path.Combine(worktree, coreTestProject);
        Directory.CreateDirectory(Path.GetDirectoryName(projectFile)!);
        File.WriteAllText(projectFile, "<Project />");
        var manifestFile = Path.Combine(worktree, acceptanceManifest);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestFile)!);
        File.WriteAllText(manifestFile, $$"""
            { "checks": [{ "name": "core tests", "type": "dotnet-test", "project": "{{coreTestProject}}" }] }
            """);
        RunGit(worktree, "add", coreSource, coreTestProject, acceptanceManifest);
        RunGit(worktree, "commit", "-m", "Change Core so policy requires its checks");
        var candidate = RunGit(worktree, "rev-parse", "HEAD");
        var result = new AcceptanceVerificationResult(true, false, 0, null,
            Checks: [new(verifier.NormalCheckName, true, 0, null)]);
        var bundle = context.Worktrees.BuildAcceptanceEvidence(context.Kernel, fixture.Goal, worktree, result, false,
            fixture.Repository);
        Assert.Contains(bundle.PolicyChecks, check => check.Required && check.Name == "core tests" && check.State == "missing");

        GoalOperationJournal.AcceptanceGatePassed(fixture.Repository, fixture.Goal, "acceptance", candidate, fixture.Main);
        var repository = new SqliteOrchestratorStateRepository(context.Workspace.SqliteStatePath);
        await repository.SaveAsync(fixture.Kernel);
        var restarted = await repository.LoadAsync();
        var goal = restarted.GetGoal(fixture.Goal.Id);
        var resumed = CreateCliContext(restarted, context.Workspace, goal, verifier);

        Assert.False(CliCommandHandlers.RunAcceptanceWorkspaceMergeCore(resumed));
        Assert.Equal((goal.Id, candidate), Assert.Single(verifier.NormalRuns));
        Assert.Null(store.ReadCheck(SoloBaselineRepairStore.NormalGateKey(goal.Id.Value, candidate, fixture.Main)));
        var outcomes = GoalOperationJournal.AcceptanceOutcomesForCandidate(
            GoalOperationJournal.ReadActive(fixture.Repository, goal.Id), candidate, fixture.Main);
        Assert.Contains(outcomes, entry => entry.AcceptanceOutcome == "failed");
        Assert.False(Assert.Single(outcomes.Where(entry => entry.AcceptanceOutcome == "gate-passed")).RequiredChecksValidated);
        Assert.Equal(fixture.Main, RunGit(fixture.Repository, "rev-parse", "main"));
    }

    [Fact]
    public void DirectPublication_PreparesTheAcceptedCommitWhenTheGoalRefMovesAndReturns()
    {
        using var fixture = new PullRequestLandingTestFixture();
        var (context, store, finding) = PrepareCliRepair(fixture, new ControlledVerifier(fixture.Repository));
        RecordPassedValidation(store, finding, fixture.Main);
        store.AcknowledgeEffect(SoloBaselineRepairStore.NormalGateKey(fixture.Goal.Id.Value, fixture.Candidate, fixture.Main),
            PullRequestLandingTestFixture.Attempt);
        var goalRef = "refs/heads/" + GoalWorktrees.BranchName(fixture.Goal.Id);
        var alternate = RunGit(fixture.Repository, "commit-tree", fixture.Main + "^{tree}", "-p", fixture.Candidate,
            "-m", "Alternate unverified tree");
        var movedDuringPreparation = false;
        using var runner = LandingExecutor.UseGitRunner((directory, args) =>
        {
            if (args is not ["merge", "--no-ff", ..]) return GitCli.Run(directory, args);

            RunGit(fixture.Repository, "update-ref", goalRef, alternate, fixture.Candidate);
            movedDuringPreparation = true;
            try { return GitCli.Run(directory, args); }
            finally { RunGit(fixture.Repository, "update-ref", goalRef, fixture.Candidate, alternate); }
        });

        var landed = LandingExecutor.Execute(fixture.Kernel, fixture.Goal, context.Workspace);

        Assert.True(movedDuringPreparation);
        Assert.True(landed.MainAdvanced, landed.Message);
        Assert.Equal(fixture.Candidate, RunGit(fixture.Repository, "rev-parse", "main^2"));
        Assert.Equal(RunGit(fixture.Repository, "rev-parse", fixture.Candidate + "^{tree}"),
            RunGit(fixture.Repository, "rev-parse", "main^{tree}"));
    }

    [Fact]
    public void DirectPublication_RejectsGoalRefMovementAtMainRefTransaction()
    {
        using var fixture = new PullRequestLandingTestFixture();
        var (context, store, finding) = PrepareCliRepair(fixture, new ControlledVerifier(fixture.Repository));
        RecordPassedValidation(store, finding, fixture.Main);
        store.AcknowledgeEffect(SoloBaselineRepairStore.NormalGateKey(fixture.Goal.Id.Value, fixture.Candidate, fixture.Main),
            PullRequestLandingTestFixture.Attempt);
        var goalRef = "refs/heads/" + GoalWorktrees.BranchName(fixture.Goal.Id);
        var alternate = RunGit(fixture.Repository, "commit-tree", fixture.Main + "^{tree}", "-p", fixture.Candidate,
            "-m", "Unverified revision at publication");
        RunGit(fixture.Repository, "branch", LandingExecutor.IntegrationBranchName, fixture.Main);
        var reachedPublication = false;
        using var runner = LandingExecutor.UseGitRunner(GitCli.Run, (directory, input, args) =>
        {
            Assert.Equal(new[] { "update-ref", "--stdin" }, args);
            Assert.Contains($"verify {goalRef} {fixture.Candidate}", input, StringComparison.Ordinal);
            Assert.NotEqual(fixture.Main, RunGit(directory, "rev-parse", LandingExecutor.IntegrationBranchName));
            RunGit(directory, "update-ref", goalRef, alternate, fixture.Candidate);
            reachedPublication = true;
            return GitCli.RunWithStandardInput(directory, GitCli.DefaultTimeoutMilliseconds, input, args);
        });

        var landed = LandingExecutor.Execute(fixture.Kernel, fixture.Goal, context.Workspace);

        Assert.True(reachedPublication);
        Assert.False(landed.MainAdvanced);
        Assert.True(LandingExecutor.IsMutationHoldEscalation(Assert.IsType<LandingDecision.Escalate>(landed.Decision).Reason));
        Assert.Equal(alternate, RunGit(fixture.Repository, "rev-parse", goalRef));
        Assert.Equal(fixture.Main, RunGit(fixture.Repository, "rev-parse", "main"));
        Assert.Equal(fixture.Main, RunGit(fixture.Repository, "rev-parse", LandingExecutor.IntegrationBranchName));
        Assert.False(GoalOperationJournal.HasDurableLandingIntent(GoalOperationJournal.Read(fixture.Repository, fixture.Goal.Id)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CliAcceptance_KeepsExactSelectionWhenCaptureFails(bool baselineThrows)
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        fixture.Git("checkout", "main");
        var workspace = Workspace(fixture);
        var goal = NewCandidate(fixture, fixture.Kernel);
        var checkout = Worktree(fixture, goal);
        var candidate = RunGit(checkout, "rev-parse", "HEAD");
        var main = fixture.Git("rev-parse", "main");
        const string dataCase = "(value: 1)";
        var verifier = fixture.CreateVerifier(checkout, baselineThrows: baselineThrows, baselineFails: true,
            singleRow: true, firstCase: dataCase, captureFails: !baselineThrows);
        Assert.False(CliCommandHandlers.RunAcceptanceWorkspaceMergeCore(
            CreateCliContext(fixture.Kernel, workspace, goal, verifier)));
        Assert.True(fixture.CandidateRuns > 0);
        Assert.True(fixture.BaselineRuns > 0);

        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(fixture.Kernel);
        var restarted = await repository.LoadAsync();
        var reloaded = restarted.GetGoal(goal.Id);
        var failure = Assert.IsType<AcceptanceFailureSummary>(reloaded.LatestAcceptanceFailure);
        if (baselineThrows)
            Assert.Null(failure.SoloFailureReceipt);
        else
            Assert.NotNull(Assert.IsType<SoloAcceptanceFailureReceipt>(failure.SoloFailureReceipt).CaptureError);

        Assert.True(CliWorkspaceMerge.ReportRepairHold(CreateCliContext(restarted, workspace, reloaded, verifier),
            reloaded, new(candidate, main), checkout, requireNormalAcceptance: false, out _));
        var path = Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName);
        var unresolved = Assert.Single(new SoloBaselineRepairStore(path).ReadFindings(goal.Id.Value)
            .Where(finding => finding.Identity.Length > 0));
        Assert.Equal((candidate, main, failure.AttemptId), (unresolved.Candidate, unresolved.Main, unresolved.Attempt));
        Assert.Equal(First + dataCase, unresolved.Identity);
        Assert.Equal(Project, unresolved.Project);
        Assert.Equal($"{Project}: FullyQualifiedName~{First}", unresolved.Request);
        Assert.Equal(failure.SoloFailureReceipt?.Evidence, unresolved.Proof);
        Assert.NotNull(unresolved.Reason);
        Assert.False(unresolved.Inherited);
        Assert.Null(unresolved.Feedback);
        Assert.Equal(0, SoloRepairReplayTests.Count(path, "solo_repair_dependency"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CliLanding_ReplaysEffectsForOriginalPromotion(bool fromAcceptance)
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        fixture.Git("checkout", "main");
        var workspace = Workspace(fixture);
        var goal = NewCandidate(fixture, fixture.Kernel);
        var checkout = Worktree(fixture, goal);
        const string enginePath = "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs";
        var source = Path.Combine(checkout, enginePath);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "// Acceptance-engine change.");
        RunGit(checkout, "add", enginePath);
        RunGit(checkout, "commit", "-m", "Change the acceptance engine");
        var candidate = RunGit(checkout, "rev-parse", "HEAD");
        fixture.Kernel.RecordDispatchResultCommit(goal.Id, goal.Tasks.Single().Id, candidate);
        var main = fixture.Git("rev-parse", "main");
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        var verifier = new ControlledVerifier(fixture.Root) { NormalPass = true, NormalCheckName = ManifestCheckName };
        if (fromAcceptance)
        {
            var interrupted = CreateCliContext(fixture.Kernel, workspace, goal, verifier, finalize: request =>
            {
                var result = request.Merge();
                Assert.True(result.FastForwarded, result.Message);
                CliWorkspaceMerge.ApplyAcceptanceResult(fixture.Kernel, request, result);
                repository.SaveAsync(fixture.Kernel).GetAwaiter().GetResult();
                throw new IOException("Interrupted after saving goal completion.");
            });
            Assert.Equal("Interrupted after saving goal completion.",
                Assert.Throws<IOException>(() => CliCommandHandlers.RunAcceptanceWorkspaceMergeCore(interrupted)).Message);
        }
        else
        {
            var advanced = GoalWorktrees.TryAdvanceFastForwardRefs(fixture.Repo, goal.Id, "refs/heads/main",
                main, candidate, () => null, new SystemClock(), fromAcceptance: false);
            var receipt = Assert.IsType<CliFastForwardReceipt>(advanced.FastForwardReceipt);
            Assert.True(GoalWorktrees.SynchronizeCliFastForward(fixture.Repo, goal.Id, receipt).FastForwarded);
            await repository.SaveAsync(fixture.Kernel);
        }

        Assert.Empty(CompletionReceipts());
        var reloaded = await repository.LoadAsync();
        Assert.Equal(fromAcceptance ? GoalStatus.Completed : GoalStatus.Verified, reloaded.GetGoal(goal.Id).Status);
        fixture.Git("commit", "--allow-empty", "-m", "Advance main after the promotion");
        var laterMain = fixture.Git("rev-parse", "main");
        Assert.NotEqual(candidate, laterMain);

        var events = new PostLandingCanaryEventStore(new SqliteRunEventStore(workspace.RunEventStorePath), workspace.RunEventStorePath);
        var runner = new LandingCanaryRunner(fromAcceptance
            ? PostLandingCanaryOutcome.Passed(1, "Fixture canary passed.")
            : PostLandingCanaryOutcome.Failed(PostLandingCanaryFailureReason.Reject, "Fixture canary rejected.", 1));
        var writer = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory);
        var refLock = Path.GetFullPath(fixture.Git("rev-parse", "--git-path", $"refs/mcg/cli-fast-forward/{goal.Id.Value}.lock"), fixture.Repo);
        File.WriteAllText(refLock, "fixture blocks acknowledgement after effects are delivered");
        try
        {
            var failure = Assert.Throws<InvalidOperationException>(() => Replay(reloaded));
            Assert.Contains("Cannot acknowledge the CLI fast-forward", failure.Message);
            Assert.NotNull(GoalWorktrees.ReadCliFastForward(fixture.Repo, goal.Id));
        }
        finally
        {
            File.Delete(refLock);
        }

        Replay(await repository.LoadAsync());
        Assert.Null(GoalWorktrees.ReadCliFastForward(fixture.Repo, goal.Id));
        Assert.Equal(laterMain, fixture.Git("rev-parse", "main"));
        var delivery = Assert.Single(runner.Runs);
        Assert.Equal(candidate, delivery.LandingSha);
        Assert.Equal(enginePath, Assert.Single(delivery.TriggeringPaths));
        var retained = await events.ReadForLandingAsync(candidate);
        Assert.Single(retained.Where(record => record.Kind == PostLandingCanaryEventKind.Queued));
        var expectedCanary = fromAcceptance ? PostLandingCanaryEventKind.Passed : PostLandingCanaryEventKind.Failed;
        Assert.Single(retained.Where(record => record.Kind == expectedCanary));
        Assert.Empty(await events.ReadForLandingAsync(laterMain));
        if (fromAcceptance)
        {
            var completion = Assert.Single(CompletionReceipts());
            Assert.True(completion.HasCandidate(candidate, main));
            var lifecycle = File.ReadLines(writer.EventFilePath(goal.Id));
            Assert.Single(lifecycle.Where(line => line.Contains("\"eventType\":\"AcceptanceResult\"", StringComparison.Ordinal)));
            Assert.Single(File.ReadLines(workspace.ConductEventsLogPath)
                .Where(line => line.Contains($"cli-acceptance-completed:{goal.Id.Value}:{main}:{candidate}", StringComparison.Ordinal)));
            Assert.Equal((goal.Id, candidate), Assert.Single(verifier.NormalRuns));
        }
        else
        {
            Assert.Empty(CompletionReceipts());
            Assert.Empty(verifier.NormalRuns);
        }

        GoalOperationJournalEntry[] CompletionReceipts() => GoalOperationJournal.Read(fixture.Repo, goal.Id).Entries
            .Where(entry => entry.Operation == "acceptance" && entry.AcceptanceOutcome == "passed").ToArray();

        void Replay(AgentOrchestratorKernel kernel)
        {
            var canary = new PostLandingCanaryCoordinator(PostLandingCanaryConfiguration.Default, runner, events,
                PostLandingCanaryFactory.CreateCircuit(workspace));
            var context = CreateCliContext(kernel, workspace, kernel.GetGoal(goal.Id), verifier,
                eventWriter: writer, canary: canary);
            if (fromAcceptance)
                CliCommandHandlers.RunAcceptanceWorkspaceMergeCore(context);
            else
                CliCommandHandlers.Execute(["workspace", "merge", goal.Id.Value], context);
        }
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("interrupted")]
    [InlineData("checkout-blocked")]
    public async Task CliAcceptance_LandsRepairWithoutDutiesAndValidatesDependentAfterReload(string boundary)
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        fixture.Git("checkout", "main");
        var workspace = Workspace(fixture);
        var dependent = NewCandidate(fixture, fixture.Kernel);
        var failure = await ProduceFailure(fixture, dependent);
        var receipt = Assert.IsType<SoloAcceptanceFailureReceipt>(failure.SoloFailureReceipt);
        fixture.Kernel.RecordAcceptanceFailure(dependent.Id, failure.FailedChecks!, failure.BranchHeadSha, failure.MainHeadSha,
            failure.CheckAttributions, failure.BaselineAttestation, failure.AttemptId, receipt);
        fixture.Kernel.RouteRecordedAcceptanceFailure(dependent.Id, "Hold the inherited solo failure.");
        CliWorkspaceMerge.RetainRepairEvidence(CreateCliContext(fixture.Kernel, workspace, dependent,
            new ControlledVerifier(fixture.Root)), dependent);
        var storePath = Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName);
        var store = new SoloBaselineRepairStore(storePath);
        var finding = Assert.Single(store.ReadFindings(dependent.Id.Value));
        Assert.True(finding.Inherited, finding.Reason);
        var repair = CompletedGoal(fixture.Kernel, fixture.Repo);
        var repairWorktree = GoalWorktrees.Ensure(fixture.Repo, repair.Id);
        File.WriteAllText(Path.Combine(repairWorktree, "repair.txt"), "baseline repair");
        RunGit(repairWorktree, "add", "repair.txt");
        RunGit(repairWorktree, "commit", "-m", "Repair the baseline");
        var repairHead = RunGit(repairWorktree, "rev-parse", "HEAD");
        store.ReserveGroup(finding.GroupKey, "{}");
        store.AcknowledgeRepair(finding.GroupKey, repair.Id.Value);
        Assert.Empty(repair.OutstandingPostLandingDuties);
        var verifier = new ControlledVerifier(fixture.Root) { NormalPass = true, NormalCheckName = finding.Check, Finding = finding };

        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(fixture.Kernel);
        var context = CreateCliContext(fixture.Kernel, workspace, repair, verifier, finalize: request =>
        {
            if (boundary == "interrupted")
            {
                var advanced = GoalWorktrees.TryAdvanceFastForwardRefs(fixture.Repo, repair.Id, "refs/heads/main",
                    receipt.MainSha, request.TestedWorktreeHead!, () => null, new SystemClock(), fromAcceptance: true);
                Assert.NotNull(advanced.FastForwardReceipt);
                throw new IOException("Interrupted after the Git ref transaction.");
            }
            var result = request.Merge();
            CliWorkspaceMerge.ApplyAcceptanceResult(fixture.Kernel, request, result);
            return result;
        });
        if (boundary == "interrupted")
        {
            Assert.Equal("Interrupted after the Git ref transaction.",
                Assert.Throws<IOException>(() => CliCommandHandlers.RunAcceptanceWorkspaceMergeCore(context)).Message);
            Assert.Null(await repository.ReadPromotionFactAsync(repair.Id));
        }
        else if (boundary == "checkout-blocked")
        {
            var indexLock = Path.GetFullPath(RunGit(fixture.Repo, "rev-parse", "--git-path", "index.lock"), fixture.Repo);
            var thread = Environment.CurrentManagedThreadId;
            var prior = Volatile.Read(ref GitCli.LaunchObserver);
            Action<string> observer = command =>
            {
                prior?.Invoke(command);
                if (Environment.CurrentManagedThreadId == thread && command == "update-ref")
                    File.WriteAllText(indexLock, "fixture interrupted checkout synchronization");
            };
            Assert.Same(prior, Interlocked.CompareExchange(ref GitCli.LaunchObserver, observer, prior));
            try
            {
                Assert.False(CliCommandHandlers.RunAcceptanceWorkspaceMergeCore(context));
                Assert.True(File.Exists(indexLock));
            }
            finally
            {
                Interlocked.CompareExchange(ref GitCli.LaunchObserver, prior, observer);
                File.Delete(indexLock);
            }
            Assert.NotNull(await repository.ReadPromotionFactAsync(repair.Id));
            Assert.Null(repair.LatestAcceptanceFailure);
        }
        else
        {
            Assert.True(CliCommandHandlers.RunAcceptanceWorkspaceMergeCore(context));
        }

        if (boundary != "completed")
        {
            Assert.Equal(GoalStatus.Verified, repair.Status);
            Assert.Equal(repairHead, fixture.Git("rev-parse", "main"));
            Assert.False(File.Exists(Path.Combine(fixture.Repo, "repair.txt")));
            var pending = Assert.IsType<CliFastForwardReceipt>(GoalWorktrees.ReadCliFastForward(fixture.Repo, repair.Id));
            var reloaded = await repository.LoadAsync();
            var resumed = CreateCliContext(reloaded, workspace, reloaded.GetGoal(repair.Id), verifier);
            Assert.True(CliCommandHandlers.RunAcceptanceWorkspaceMergeCore(resumed));
            Assert.Equal(pending.PublishedAt, (await repository.ReadPromotionFactAsync(repair.Id))?.PromotedAt);
            await repository.SaveAsync(reloaded);
        }
        else
        {
            await repository.SaveAsync(fixture.Kernel);
        }
        Assert.Equal((repair.Id, repairHead), Assert.Single(verifier.NormalRuns));
        Assert.Equal(repairHead, fixture.Git("rev-parse", "main"));
        Assert.True(File.Exists(Path.Combine(fixture.Repo, "repair.txt")));
        Assert.Null(GoalWorktrees.ReadCliFastForward(fixture.Repo, repair.Id));
        var restarted = await repository.LoadAsync();
        var promoted = Assert.IsType<GoalPromotionFact>(await repository.ReadPromotionFactAsync(repair.Id));
        Assert.Equal(GoalPromotionFact.CliFastForwardProvenance, promoted.Provenance);
        Assert.Equal(repairHead, promoted.PromotedMainRevision);
        Assert.Equal(receipt.MainSha, promoted.PriorMainRevision);
        RunGit(Worktree(fixture, dependent), "rebase", "main");
        var result = Driver(restarted, workspace, verifier).AdvanceOnce(restarted.GetGoal(dependent.Id), Policy);
        Assert.Equal(SoloHoldReasons.RepairValidated, Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome).Decision?.DiscriminatingEvidence);
        Assert.Equal((finding.Request!, repairHead), Assert.Single(verifier.FocusedRuns));
        Assert.Equal("passed", new SoloBaselineRepairStore(storePath).ReadCheck(SoloBaselineRepairStore.ValidationKey(finding, repairHead))?.Outcome);
    }

    private static (CliExecutionContext Context, SoloBaselineRepairStore Store, SoloRepairFinding Finding)
        PrepareCliRepair(PullRequestLandingTestFixture fixture, ControlledVerifier verifier,
            Func<AcceptanceHostStopRequest, AcceptanceHostStopResult>? stopHosts = null)
    {
        var workspace = fixture.Workspace with { Delivery = ProjectDelivery.Direct };
        var context = CreateCliContext(fixture.Kernel, workspace, fixture.Goal, verifier, stopHosts);
        var store = new SoloBaselineRepairStore(Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName));
        var finding = CapturePublicationFinding(fixture);
        store.Retain(fixture.Goal.Id.Value, [finding]);
        store.ReserveGroup(finding.GroupKey, "{}");
        store.AcknowledgeRepair(finding.GroupKey, Guid.NewGuid().ToString("N"));
        return (context, store, finding);
    }

    private static CliExecutionContext CreateCliContext(AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace,
        Goal goal, IGoalAcceptanceVerifier verifier, Func<AcceptanceHostStopRequest, AcceptanceHostStopResult>? stopHosts = null,
        Func<AcceptanceMergeCommitRequest, AcceptanceMergeCommitResult>? finalize = null,
        IGoalLifecycleEventWriter? eventWriter = null, PostLandingCanaryCoordinator? canary = null) =>
        new(kernel, workspace, new InMemoryModelProviderRegistry([]), AgentCatalog.Default().Agents,
            WorkerProfileCatalog.Default(), goal, finalizeAcceptanceMerge: finalize,
            stopAcceptanceHosts: stopHosts ?? (_ => AcceptanceHostStopResult.Success("Fixture stopped hosts.")))
        {
            AcceptanceVerifier = verifier,
            EventWriter = eventWriter ?? NullGoalLifecycleEventWriter.Instance,
            PostLandingCanary = canary,
            StableSlotSelector = (_, _) => AcceptanceStableSlotTestSupport.CreateFakeStableSlotLease(workspace.OrchestratorDirectory)
        };

    private sealed class LandingCanaryRunner(PostLandingCanaryOutcome outcome) : IPostLandingCanaryRunner
    {
        internal List<PostLandingCanaryRequest> Runs { get; } = [];

        public Task<PostLandingCanaryOutcome> RunAsync(PostLandingCanaryRequest request, CancellationToken cancellationToken)
        {
            Runs.Add(request);
            return Task.FromResult(outcome);
        }
    }

    private static void AppendJournalEntry(string path, GoalOperationJournalEntry entry) =>
        SharedJsonlFile.AppendLine(path, JsonSerializer.Serialize(entry, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        }));

    private static SoloRepairFinding CapturePublicationFinding(PullRequestLandingTestFixture fixture)
    {
        var finding = SoloRepairReplayTests.Finding() with
            { Candidate = fixture.Candidate, Main = fixture.Main, Baseline = fixture.Main };
        var root = Path.Combine(fixture.Workspace.OrchestratorDirectory, "retained-solo-proof");
        Directory.CreateDirectory(root);
        var trx = Path.Combine(root, "failure.trx");
        WriteTrx(trx, false, finding.Identity);
        var candidate = new AcceptanceCheckResult(finding.Check, false, 1, null, TestResultPaths: [trx],
            ExecutedTestCount: 1, FailingTestIdentities: [finding.Identity], TestProjectPath: finding.Project)
            { LaneToolResolution = new() };
        var baseline = SoloRepairEvidenceTests.FocusedResult(finding, fixture.Main, trx, false, true, finding.Identity,
            FindingEvidenceArm.Baseline);
        var tree = RunGit(fixture.Repository, "rev-parse", fixture.Candidate + "^{tree}");
        var receipt = SoloAcceptanceFailureEvidenceWriter.Capture(new(finding.Attempt, fixture.Goal.Id.Value,
            fixture.Repository, tree, fixture.Main, fixture.Candidate, root, null, Environment.ProcessId, null),
            fixture.Main, [candidate], Assert.Single(baseline.Arms!),
            [new(finding.Check, finding.Identity, finding.BaselineCheck!)]);
        Assert.Null(receipt.CaptureError);
        var proof = Assert.IsType<SoloAcceptanceFailureEvidence>(
            JsonSerializer.Deserialize<SoloAcceptanceFailureEvidence>(File.ReadAllBytes(receipt.Evidence.Path)));
        return finding with { Proof = receipt.Evidence, CandidateArtifacts = Assert.Single(proof.Checks).Artifacts,
            BaselineArtifacts = Assert.Single(proof.BaselineChecks).Artifacts };
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
    public async Task SameProjectLabel_UsesSecondDeclaredProjectThroughRepairValidation()
    {
        using var fixture = new SoloAttributedFailureRouteFixture(collidingProjectLabels: true);
        fixture.Git("checkout", "main");
        var workspace = Workspace(fixture);
        await new PortfolioStore(workspace.PortfolioStorePath).AddEpicAsync("baseline-maintenance");
        var goal = NewCandidate(fixture, fixture.Kernel);
        var worktree = Worktree(fixture, goal);
        var label = GoalAcceptanceVerifier.ProjectLabel(SecondCollisionProject);
        Assert.Equal(label, GoalAcceptanceVerifier.ProjectLabel(FirstCollisionProject));
        var settings = AcceptanceGateEngineSettings.Load(worktree);
        Assert.True(GoalAcceptanceVerifier.TryResolveFocusedEvidenceProject(label, settings, out var resolved));
        Assert.Equal(FirstCollisionProject, resolved);

        await ProduceFailure(fixture, goal);
        var baselineCheck = Assert.Single(fixture.Proof().BaselineChecks).Check;
        Assert.Equal(SecondCollisionProject, baselineCheck.TestProjectPath);
        fixture.ReplaceProof(proof => proof with { Bindings = [] });
        var failure = fixture.Summary;
        fixture.Kernel.RecordAcceptanceFailure(goal.Id, failure.FailedChecks!, failure.BranchHeadSha, failure.MainHeadSha,
            failure.CheckAttributions, failure.BaselineAttestation, failure.AttemptId, failure.SoloFailureReceipt);
        fixture.Kernel.RouteRecordedAcceptanceFailure(goal.Id, "Hold the unresolved acceptance failure.");
        var finding = Assert.Single(Assert.IsType<SoloRepairPartition>(
            SoloAcceptanceFailureReceiptReader.ReadRepairPartition(goal, null, failure)).Findings);
        var expectedRequest = $"{SecondCollisionProject}: FullyQualifiedName~{First}";
        Assert.Equal(expectedRequest, finding.Request);
        var selectedProjects = new List<(string Project, bool RunBaseline)>();
        var verifier = new ControlledVerifier(fixture.Root)
        {
            Finding = finding, AttributionBaselinePass = false,
            FocusedSelection = (path, request, runBaseline) =>
            {
                Assert.True(GoalAcceptanceVerifier.TryBuildFocusedEvidenceChecks(request, settings, path,
                    out var checks, out _, out var rejection), rejection?.Detail);
                var selection = Assert.Single(checks);
                selectedProjects.Add((selection.Project!, runBaseline));
                return selection.Name;
            }
        };
        var driver = Driver(fixture.Kernel, workspace, verifier);

        AssertHold(driver.AdvanceOnce(goal, Policy), SoloHoldReasons.RepairPending);
        var store = new SoloBaselineRepairStore(Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName));
        var inherited = Assert.Single(store.ReadFindings(goal.Id.Value));
        Assert.True(inherited.Inherited, inherited.Reason);
        var repairId = Assert.IsType<string>(store.ReadGroup(inherited.GroupKey)?.RepairGoal);
        fixture.Git("commit", "--allow-empty", "-m", $"Integrate goal/{repairId}: repair the second project");
        var refreshedMain = fixture.Git("rev-parse", "HEAD");
        RunGit(worktree, "rebase", "main");

        driver.AdvanceOnce(goal, Policy);

        Assert.Equal(new[] { (SecondCollisionProject, true), (SecondCollisionProject, false) }, selectedProjects);
        Assert.Equal((expectedRequest, refreshedMain), verifier.FocusedRuns.Last());
        Assert.Equal("passed", store.ReadCheck(SoloBaselineRepairStore.ValidationKey(inherited, refreshedMain))?.Outcome);
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
    public async Task SharedRepair_DifferentLanesAndRevisions_JoinsOneRepairAndReleasesBothOnLanding()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        fixture.Git("checkout", "main");
        var workspace = Workspace(fixture);
        var kernel = fixture.Kernel;
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var maintenance = await portfolio.AddEpicAsync("baseline-maintenance");
        var first = NewCandidate(fixture, kernel);
        await repository.SaveAsync(kernel);
        var verifier = new ControlledVerifier(fixture.Root);
        var driver = Driver(kernel, workspace, verifier);
        var path = Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName);
        var store = new SoloBaselineRepairStore(path);
        var intake = new GoalIntakeRequestStore(workspace.SqliteStatePath);

        AssertHold(Complete(driver, first, await ProduceFailure(fixture, first)), SoloHoldReasons.RepairPending);
        var firstFinding = Assert.Single(store.ReadFindings(first.Id.Value));
        Assert.True(firstFinding.Inherited, firstFinding.Reason);
        var group = Assert.IsType<SoloRepairGroup>(store.ReadGroup(firstFinding.GroupKey));
        var repair = kernel.GetGoal(new GoalId(Assert.IsType<string>(group.RepairGoal)));
        var requestKey = "solo-baseline-repair-" + group.Key;
        var request = Assert.IsType<GoalIntakeRequestRecord>(intake.Get(requestKey));
        Assert.Equal(repair.Id.Value, request.GoalId);
        Assert.Equal(maintenance.Id, (await portfolio.GetGoalMembershipAsync(repair.Id.Value))?.EpicId);

        RenameLaneOnMain(fixture);
        var second = NewCandidate(fixture, kernel);
        var differentTest = NewCandidate(fixture, kernel);
        await repository.SaveAsync(kernel);
        driver = Driver(kernel, workspace, verifier);
        AssertHold(Complete(driver, second, await ProduceFailure(fixture, second)), SoloHoldReasons.RepairPending);
        var secondFinding = Assert.Single(store.ReadFindings(second.Id.Value));
        Assert.True(secondFinding.Inherited, secondFinding.Reason);
        Assert.NotEqual(firstFinding.Check, secondFinding.Check);
        Assert.NotEqual(firstFinding.Baseline, secondFinding.Baseline);
        Assert.Equal((firstFinding.Project, firstFinding.Identity), (secondFinding.Project, secondFinding.Identity));
        Assert.Equal(group, store.ReadGroup(secondFinding.GroupKey));
        Assert.Equal(request, intake.Get(requestKey));
        Assert.Equal(1, SoloRepairReplayTests.Count(workspace.SqliteStatePath, "goal_intake_requests"));
        Assert.Equal(1, SoloRepairReplayTests.Count(path, "solo_repair_group"));
        Assert.Equal(2, SoloRepairReplayTests.Count(path, "solo_repair_dependency"));
        Assert.Equal(group.Key, ReadDependency(path, first));
        Assert.Equal(group.Key, ReadDependency(path, second));

        await fixture.Produce(baselineFails: true, attemptId: differentTest.Id.Value,
            candidateWorktree: Worktree(fixture, differentTest), goal: differentTest, singleRow: true, firstCase: "(value: 2)");
        AssertHold(Complete(driver, differentTest, fixture.Summary), SoloHoldReasons.RepairPending);
        var otherFinding = Assert.Single(store.ReadFindings(differentTest.Id.Value));
        Assert.True(otherFinding.Inherited, otherFinding.Reason);
        Assert.NotEqual(firstFinding.Identity, otherFinding.Identity);
        Assert.NotEqual(repair.Id.Value, Assert.IsType<SoloRepairGroup>(store.ReadGroup(otherFinding.GroupKey)).RepairGoal);
        Assert.Empty(verifier.FocusedRuns);

        var main = LandRepair(fixture, workspace, repair);
        verifier.Finding = firstFinding;
        foreach (var waiter in new[] { first, second })
        {
            RunGit(Worktree(fixture, waiter), "rebase", "main");
            AssertFreshRepairGate(driver.AdvanceOnce(waiter, Policy), waiter, main);
        }
        RunGit(Worktree(fixture, differentTest), "rebase", "main");
        AssertHold(driver.AdvanceOnce(differentTest, Policy), SoloHoldReasons.RepairPending);
        Assert.Equal((firstFinding.Request!, main), Assert.Single(verifier.FocusedRuns));
        Assert.Equal("passed", store.ReadRepairValidation(secondFinding, main)?.Outcome);
    }

    [Fact]
    public async Task LegacyGroupKey_ResolvesRecordedRepairAndReleasesWaitersOnLanding()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        fixture.Git("checkout", "main");
        var workspace = Workspace(fixture);
        var kernel = fixture.Kernel;
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        var maintenance = await new PortfolioStore(workspace.PortfolioStorePath).AddEpicAsync("baseline-maintenance");
        var path = Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName);
        var store = new SoloBaselineRepairStore(path);
        var intake = new GoalIntakeRequestStore(workspace.SqliteStatePath);
        var repairIntake = new CliSoloBaselineRepairIntake(workspace);

        async Task<(Goal Waiter, Goal Repair, SoloRepairFinding Finding, SoloRepairGroup Group)> SeedLegacyRepair()
        {
            var waiter = NewCandidate(fixture, kernel);
            var failure = await ProduceFailure(fixture, waiter);
            var receipt = Assert.IsType<SoloAcceptanceFailureReceipt>(failure.SoloFailureReceipt);
            kernel.RecordAcceptanceFailure(waiter.Id, failure.FailedChecks!, failure.BranchHeadSha, failure.MainHeadSha,
                failure.CheckAttributions, failure.BaselineAttestation, failure.AttemptId, receipt);
            kernel.RouteRecordedAcceptanceFailure(waiter.Id, "Fixture legacy repair hold.");
            var context = new SoloAcceptanceConsumptionContext(receipt.CandidateSha, receipt.MainSha,
                RunGit(Worktree(fixture, waiter), "rev-parse", "HEAD^{tree}"), receipt.BaselineSha);
            var finding = Assert.Single(SoloAcceptanceFailureReceiptReader.ReadRepairPartition(waiter, context)!.Findings);
            Assert.True(finding.Inherited, finding.Reason);
            var legacyKey = SoloBaselineRepairStore.Key(finding.Baseline.ToLowerInvariant(), finding.Check,
                finding.Project, finding.RequestIdentity, finding.BaselineCheck, finding.Identity);
            var requestKey = "solo-baseline-repair-" + legacyKey;
            var payload = repairIntake.PrepareRepair(requestKey, $"Repair {finding.Identity} in {finding.Check}", maintenance.Id);
            await repository.SaveAsync(kernel);
            store.ReserveGroup(legacyKey, JsonSerializer.Serialize(payload, OperatorIntentJson.Options));
            intake.Reserve(requestKey, payload.Fingerprint);
            repairIntake.ApplyReservedRepair(kernel, payload);
            var repair = kernel.GetGoal(new GoalId(Assert.IsType<string>(intake.Get(requestKey)?.GoalId)));
            store.AcknowledgeRepair(legacyKey, repair.Id.Value);
            store.Retain(waiter.Id.Value, [finding]);
            return (waiter, repair, finding, Assert.IsType<SoloRepairGroup>(store.ReadGroup(legacyKey)));
        }

        var first = await SeedLegacyRepair();
        RenameLaneOnMain(fixture);
        var second = await SeedLegacyRepair();
        Assert.Equal(first.Finding.GroupKey, second.Finding.GroupKey);
        Assert.NotEqual(first.Group.Key, second.Group.Key);
        Assert.NotEqual(first.Repair.Id, second.Repair.Id);
        var verifier = new ControlledVerifier(fixture.Root) { Finding = first.Finding };
        var driver = Driver(kernel, workspace, verifier);
        foreach (var legacy in new[] { first, second })
        {
            var held = driver.AdvanceOnce(legacy.Waiter, Policy);
            AssertHold(held, SoloHoldReasons.RepairPending);
            Assert.Contains(legacy.Repair.Id.Value, Assert.IsType<ConductorAdvanceOutcome.Held>(held.Outcome).Reason);
            Assert.Equal(legacy.Group.Key, ReadDependency(path, legacy.Waiter));
        }
        Assert.Empty(verifier.FocusedRuns);

        var main = LandRepair(fixture, workspace, first.Repair);
        foreach (var legacy in new[] { first, second })
        {
            RunGit(Worktree(fixture, legacy.Waiter), "rebase", "main");
            AssertFreshRepairGate(driver.AdvanceOnce(legacy.Waiter, Policy), legacy.Waiter, main);
            Assert.True(store.IsEvidenceCurrent(legacy.Waiter.Id.Value, [legacy.Finding],
                RunGit(Worktree(fixture, legacy.Waiter), "rev-parse", "HEAD"), main));
            Assert.Equal(legacy.Group, store.ReadGroup(legacy.Group.Key));
            Assert.Equal(legacy.Group.Key, ReadDependency(path, legacy.Waiter));
        }
        Assert.Equal((first.Finding.Request!, main), Assert.Single(verifier.FocusedRuns));
        Assert.Null(store.ReadGroup(first.Finding.GroupKey));
        Assert.Equal(2, SoloRepairReplayTests.Count(path, "solo_repair_group"));
        Assert.Equal(2, SoloRepairReplayTests.Count(workspace.SqliteStatePath, "goal_intake_requests"));
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
        var currentMain = fixture.Git("rev-parse", "HEAD");
        AssertFreshRepairGate(Complete(driver, otherBase, await ProduceFailure(fixture, otherBase)), otherBase, currentMain);
        var otherFinding = Assert.Single(store.ReadFindings(otherBase.Id.Value));
        Assert.Equal(finding.GroupKey, otherFinding.GroupKey);
        Assert.Equal(currentMain, otherFinding.Baseline);
        Assert.Equal(repair.Id.Value, store.ReadGroup(otherFinding.GroupKey)?.RepairGoal);
        Assert.Equal(1, SoloRepairReplayTests.Count(storePath, "solo_repair_group"));
        Assert.Equal(5, SoloRepairReplayTests.Count(storePath, "solo_repair_dependency"));
        Assert.Equal((otherFinding.Request!, currentMain), verifier.FocusedRuns[1]);
    }

    [Theory]
    [InlineData("unbound")]
    [InlineData("missing-tool")]
    [InlineData("missing-trx")]
    [InlineData("vstest-missing-trx")]
    public async Task MixedCompletion_LinksRepairRoutesOnlyIntroducedFeedbackAndRetainsUnresolvedRow(string fault)
    {
        using var fixture = new SoloAttributedFailureRouteFixture(vstest: fault == "vstest-missing-trx");
        fixture.Git("checkout", "main");
        var main = fixture.Git("rev-parse", "HEAD");
        var workspace = Workspace(fixture);
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var maintenance = await portfolio.AddEpicAsync("baseline-maintenance");
        var goal = NewCandidate(fixture, fixture.Kernel);
        await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(fixture.Kernel);
        await fixture.Produce(mixed: true, attemptId: goal.Id.Value, candidateWorktree: Worktree(fixture, goal), goal: goal,
            baselineMissingToolFor: fault == "missing-tool" ? Unknown : null,
            baselineCrashFor: fault is "missing-trx" or "vstest-missing-trx" ? Unknown : null);
        if (fault == "unbound")
        {
            fixture.ReplaceProof(proof => proof with
            {
                Bindings = proof.Bindings.Where(binding => binding.TestIdentity != Unknown).ToArray()
            });
        }
        else
        {
            var proof = fixture.Proof();
            var binding = Assert.Single(proof.Bindings.Where(binding => binding.TestIdentity == Unknown));
            var failed = Assert.Single(proof.BaselineChecks.Where(check => check.Check.Name == binding.BaselineCheckName));
            if (fault == "missing-tool")
            {
                Assert.Equal(AcceptanceFailureClassifications.MissingTool, failed.Check.FailureClassification);
                Assert.Equal("dotnet", failed.Check.LaneToolResolution?.MissingTool);
                Assert.Equal(FindingEvidenceArmDisposition.ApparatusFailure, proof.Baseline.Disposition);
            }
            else
            {
                Assert.False(failed.Check.Passed);
                Assert.Equal(AcceptanceFailureClassifications.FocusedSelectionApparatusFailure, failed.Check.FailureClassification);
                Assert.Equal(FindingEvidenceArmDisposition.ApparatusFailure, proof.Baseline.Disposition);
                Assert.True(failed.Check.CompletionDecision is { Passed: false, TimedOut: false, TrxOutcome: "missing" });
                Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<string>>(failed.Check.TestResultPaths));
                Assert.Null(failed.Check.LaneToolResolution?.MissingTool);
                Assert.Empty(failed.Artifacts);
            }
        }
        var request = $"{Project}: FullyQualifiedName~{Unknown}";
        var verifier = new ControlledVerifier(fixture.Root) { UnresolvedAttributionRequests = [request] };
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
    public async Task ConductorCompletion_KeepsExactSelectionWhenBaselineThrows()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        fixture.Git("checkout", "main");
        var workspace = Workspace(fixture);
        await new PortfolioStore(workspace.PortfolioStorePath).AddEpicAsync("baseline-maintenance");
        var kernel = fixture.Kernel;
        var goal = NewCandidate(fixture, kernel);
        var checkout = Worktree(fixture, goal);
        var candidate = RunGit(checkout, "rev-parse", "HEAD");
        var main = fixture.Git("rev-parse", "main");
        fixture.Context = new(candidate, main, RunGit(checkout, "rev-parse", "HEAD^{tree}"), main);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(kernel);

        const string dataCase = "(value: 1)";
        await fixture.Produce(baselineThrows: true, attemptId: goal.Id.Value, candidateWorktree: checkout,
            goal: goal, singleRow: true, firstCase: dataCase);
        var completion = fixture.Summary;
        Assert.True(completion.RequiresSoloFailureReceipt);
        Assert.Null(completion.SoloFailureReceipt);

        var request = $"{Project}: FullyQualifiedName~{First}";
        var verifier = new ControlledVerifier(fixture.Root) { UnresolvedAttributionRequests = [request] };
        AssertHold(Complete(Driver(kernel, workspace, verifier), goal, completion), SoloHoldReasons.RepairEvidenceRequired, "operator");
        await repository.SaveAsync(kernel);

        var restarted = await repository.LoadAsync();
        var reloaded = restarted.GetGoal(goal.Id);
        var failure = Assert.IsType<AcceptanceFailureSummary>(reloaded.LatestAcceptanceFailure);
        Assert.Null(failure.SoloFailureReceipt);
        var path = Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName);
        var unresolved = Assert.Single(new SoloBaselineRepairStore(path).ReadFindings(reloaded.Id.Value)
            .Where(finding => finding.Identity.Length > 0));
        Assert.Equal((candidate, main, completion.AttemptId), (unresolved.Candidate, unresolved.Main, unresolved.Attempt));
        Assert.Equal(First + dataCase, unresolved.Identity);
        Assert.Equal(Project, unresolved.Project);
        Assert.Equal(request, unresolved.Request);
        Assert.Equal(SoloHoldReasons.CaptureUnavailable, unresolved.Reason);
        Assert.False(unresolved.Inherited);
        Assert.Null(unresolved.Proof);
        Assert.Equal((request, candidate), Assert.Single(verifier.FocusedRuns));
        Assert.Equal(0, SoloRepairReplayTests.Count(path, "solo_repair_dependency"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiptlessEarlyHold_RetainsExactSelectionAcrossRestart(bool lostInheritedEnvelope)
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        fixture.Git("checkout", "main");
        var workspace = Workspace(fixture);
        var kernel = fixture.Kernel;
        var goal = NewCandidate(fixture, kernel);
        var checkout = Worktree(fixture, goal);
        var candidate = RunGit(checkout, "rev-parse", "HEAD");
        var main = fixture.Git("rev-parse", "main");
        fixture.Context = new(candidate, main, RunGit(checkout, "rev-parse", "HEAD^{tree}"), main);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(kernel);

        const string dataCase = "(value: 1)";
        var baselineException = lostInheritedEnvelope ? null : new DotnetBuildSlotsBusyException(new("fixture", []));
        await fixture.Produce(baselineFails: lostInheritedEnvelope, attemptId: goal.Id.Value, candidateWorktree: checkout,
            goal: goal, singleRow: true, firstCase: dataCase, baselineException: baselineException);
        var completion = fixture.Summary;
        var attribution = Assert.Single(completion.RequiredUnmetCriteria.SelectMany(check => check.FailingTestAttributions!));
        Assert.Equal(First + dataCase, attribution.TestIdentity);
        Assert.True(completion.RequiresSoloFailureReceipt);
        if (lostInheritedEnvelope)
        {
            Assert.Equal(AcceptanceTestFailureOrigin.Inherited, attribution.Origin);
            Assert.NotNull(completion.SoloFailureReceipt);
            completion = completion with
            {
                SoloFailureReceipt = null,
                UnmetCriteria = completion.RequiredUnmetCriteria.Select(check => check with { LaneToolResolution = null }).ToArray()
            };
        }
        else
        {
            Assert.Equal(AcceptanceTestFailureOrigin.Unattributed, attribution.Origin);
            Assert.Contains(nameof(DotnetBuildSlotsBusyException), attribution.Evidence, StringComparison.Ordinal);
            Assert.Null(completion.SoloFailureReceipt);
        }

        var request = $"{Project}: FullyQualifiedName~{First}";
        var verifier = new ControlledVerifier(fixture.Root) { UnresolvedAttributionRequests = [request] };
        var result = Complete(Driver(kernel, workspace, verifier), goal, completion);
        var events = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory).EventFilePath(goal.Id);
        var collaboration = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        if (lostInheritedEnvelope)
        {
            AssertHold(result, SoloHoldReasons.RepairPending);
            Assert.Empty(await collaboration.ListAsync(goal.Id.Value));
            if (File.Exists(events))
                Assert.DoesNotContain(File.ReadLines(events), line => line.Contains("\"eventType\":\"GoalEscalated\""));
        }
        else
        {
            var hold = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
            var disposition = AcceptanceApparatusDispositionFacts.FromRecordedFacts(hold.Decision!.Facts);
            Assert.Equal(AcceptanceApparatusDisposition.ApparatusRedRegateHold, disposition.Disposition);
            Assert.Equal(ApparatusRedClassifier.BaselineNotExecutedEvidenceKind, disposition.EvidenceKind);
        }
        Assert.Empty(verifier.FocusedRuns);
        await repository.SaveAsync(kernel);

        var restarted = await repository.LoadAsync();
        var reloaded = restarted.GetGoal(goal.Id);
        var path = Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName);
        var unresolved = Assert.Single(new SoloBaselineRepairStore(path).ReadFindings(reloaded.Id.Value));
        if (lostInheritedEnvelope) Assert.False(unresolved.LaneEnvironmentVerified);
        Assert.Equal((candidate, main, completion.AttemptId), (unresolved.Candidate, unresolved.Main, unresolved.Attempt));
        Assert.Equal((ManifestCheckName, First + dataCase, Project, request),
            (unresolved.Check, unresolved.Identity, unresolved.Project, unresolved.Request));
        Assert.Equal(SoloHoldReasons.CaptureUnavailable, unresolved.Reason);
        Assert.False(unresolved.Inherited);
        Assert.Null(unresolved.Proof);
        Assert.Equal(0, SoloRepairReplayTests.Count(path, "solo_repair_dependency"));

        var evidenceHold = Driver(restarted, workspace, verifier).AdvanceOnce(reloaded, Policy);
        AssertHold(evidenceHold, SoloHoldReasons.RepairEvidenceRequired, "operator");
        if (lostInheritedEnvelope)
        {
            Assert.Single(await collaboration.ListAsync(goal.Id.Value));
            var escalation = Assert.Single(File.ReadLines(events).Where(line => line.Contains("\"eventType\":\"GoalEscalated\"")));
            using var record = JsonDocument.Parse(escalation);
            var reason = Assert.IsType<string>(record.RootElement.GetProperty("reason").GetString());
            Assert.Contains(First + dataCase, reason, StringComparison.Ordinal);
            Assert.Contains($"The verifier did not execute a conclusive selection on {candidate}", reason, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(record.RootElement.GetProperty("deliveryId").GetString()));

            await repository.SaveAsync(restarted);
            var replay = await repository.LoadAsync();
            AssertHold(Driver(replay, workspace, verifier).AdvanceOnce(replay.GetGoal(goal.Id), Policy),
                SoloHoldReasons.RepairEvidenceRequired, "operator");
            Assert.Single(await collaboration.ListAsync(goal.Id.Value));
            Assert.Equal(escalation, Assert.Single(File.ReadLines(events).Where(line => line.Contains("\"eventType\":\"GoalEscalated\""))));
        }
        Assert.Equal((request, candidate), Assert.Single(verifier.FocusedRuns));
        Assert.Empty(verifier.NormalRuns);
        Assert.Equal(0, SoloRepairReplayTests.Count(path, "solo_repair_dependency"));
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
        var verifier = new ControlledVerifier(fixture.Root) { UnresolvedAttributionRequests = [request] };

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

    internal static SoloRepairCheckReceipt RecordPassedValidation(SoloBaselineRepairStore store, SoloRepairFinding finding,
        string main, bool projectBound = true)
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
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new SoloRepairVerifierResult(identity, validation, projectBound));
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
            }, repairIntake: new CliSoloBaselineRepairIntake(workspace))
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

    private static void RenameLaneOnMain(SoloAttributedFailureRouteFixture fixture)
    {
        const string relative = "config/acceptance-manifest.json";
        var path = Path.Combine(fixture.Repo, relative);
        File.WriteAllText(path, File.ReadAllText(path).Replace(ManifestCheckName, "alternate solo lane", StringComparison.Ordinal));
        fixture.Git("add", relative);
        fixture.Git("commit", "-m", "Rename the acceptance lane");
    }

    private static string LandRepair(SoloAttributedFailureRouteFixture fixture, OrchestratorWorkspace workspace, Goal repair)
    {
        fixture.Git("commit", "--allow-empty", "-m", $"Integrate goal/{repair.Id.Value}: repair baseline");
        var main = fixture.Git("rev-parse", "HEAD");
        var branch = GoalWorktrees.BranchName(repair.Id);
        GoalOperationJournal.RecordLandingIntent(fixture.Repo, repair, branch, "main", main, "fixture landing intent");
        new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory).AppendGoalLanded(repair.Id, "main",
            branch, LandingAdmissionReceipt.Recovered(main));
        return main;
    }

    private static string ReadDependency(string path, Goal goal)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT group_key FROM solo_repair_dependency WHERE goal = $goal";
        command.Parameters.AddWithValue("$goal", goal.Id.Value);
        using var rows = command.ExecuteReader();
        Assert.True(rows.Read());
        var key = rows.GetString(0);
        Assert.False(rows.Read());
        return key;
    }

    private static void AssertFreshRepairGate(ConductorAdvanceResult result, Goal goal, string main)
    {
        var resumed = Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal("FreshGate", resumed.Decision?.Action);
        Assert.Equal(SoloHoldReasons.RepairValidated, resumed.Decision?.DiscriminatingEvidence);
        Assert.Contains(new PolicyDecisionFact("mainSha", main), resumed.Decision!.Facts);
        Assert.Equal(GoalStatus.Verified, goal.Status);
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
        internal IReadOnlyList<string> UnresolvedAttributionRequests { get; init; } = [];
        internal bool? AttributionBaselinePass { get; init; }
        internal bool AttributionCandidatePass { get; init; }
        internal bool RefreshedBaselinePass { get; init; } = true;
        internal List<(string Request, string Sha)> FocusedRuns { get; } = [];
        internal List<(GoalId Goal, string Sha)> NormalRuns { get; } = [];
        internal bool NormalPass { get; set; }
        internal string NormalCheckName { get; init; } = "normal candidate acceptance";
        internal Func<string, string, bool, string>? FocusedSelection { get; init; }

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceOwnedAsync(string worktreePath, GoalId? goalId,
            string request, IAcceptanceFocusedVerificationOwner executionOwner, int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null, bool runBaselineArm = false)
        {
            Assert.NotNull(stableSlotLease);
            var sha = RunGit(worktreePath, "rev-parse", "HEAD");
            FocusedRuns.Add((request, sha));
            if (runBaselineArm && AttributionBaselinePass is null)
            {
                Assert.Contains(request, UnresolvedAttributionRequests);
                return Task.FromResult(new FocusedEvidenceRunResult(request, false, false, "The focused test did not execute.", []));
            }
            var finding = Assert.IsType<SoloRepairFinding>(Finding);
            Assert.Equal(finding.Request, request);
            if (FocusedSelection is { } select)
                finding = finding with { BaselineCheck = select(worktreePath, request, runBaselineArm) };
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
            var check = new AcceptanceCheckResult(NormalCheckName, NormalPass, NormalPass ? 0 : 1,
                NormalPass ? "pass" : "branch-local failure");
            return Task.FromResult(new AcceptanceVerificationResult(NormalPass, false, check.ExitCode, check.OutputTail,
                Checks: [check]));
        }
    }
}
