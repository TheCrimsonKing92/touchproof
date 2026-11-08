using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every case owns its Git repositories, worktrees, SQLite and receipts.
public sealed class MultiRepositoryLandingRepairTests
{
    [Fact]
    public void Run_CompensatedTuple_RebuildsCandidateAndRequiresRegating()
    {
        using var f = FailedTuple();
        var store = Store(f);
        var failure = store.ReadFailure(f.Goal.Id)!;
        Assert.Single(failure.CompensatingCommits["crosswalk"]);
        Assert.NotNull(store.ReadAttempt(f.Goal.Id));
        var crosswalk = f.Repositories.Crosswalk;
        var integration = crosswalk.Git("rev-parse", "main");
        var homeIntegration = f.Repositories.Home.Git("rev-parse", "main");
        var candidate = Git(f.SecondaryWorktree, "rev-parse", "HEAD");
        var wanted = Git(f.SecondaryWorktree, "show", "HEAD:example.txt");
        Assert.Equal("seed", crosswalk.Git("show", "main:example.txt"));

        // Even a receipt refreshed since compensation must be invalidated by repair.
        f.Gate.Run(f.Goal, GoalLifecycleState.Verified, f.RunHome);
        Assert.NotNull(f.Gate.Store.TryRead(f.Goal.Id));
        var result = Repair(f).Run(f.Goal);

        Assert.True(result.Repaired, result.Detail);
        Assert.Equal(integration, crosswalk.Git("rev-parse", "main"));
        Assert.Equal(homeIntegration, f.Repositories.Home.Git("rev-parse", "main"));
        Git(f.SecondaryWorktree, "merge-base", "--is-ancestor", candidate, "HEAD");
        Assert.Null(store.ReadFailure(f.Goal.Id));
        Assert.Null(store.ReadAttempt(f.Goal.Id)); // Includes all write-ahead repair progress.
        Assert.Null(f.Gate.Store.TryRead(f.Goal.Id));
        var landing = new MultiRepositoryTupleLanding(f.Repositories.Workspace, f.Repositories.Registry);
        Assert.False(landing.RequiresRecovery(f.Goal));
        var checksBefore = f.Calls.Count;
        var next = MultiRepositoryAcceptanceExecution.Run(f.Goal, GoalLifecycleState.Verified,
            f.Gate, landing, f.RunHome, () => throw new InvalidOperationException("Must stop before re-landing."),
            () => null, () => "fixture stop after re-gating", () => { }, _ => { });
        Assert.Equal(checksBefore + 2, f.Calls.Count); // Both repositories must gate again.
        Assert.Equal("multi-repository-landing-blocked", next.Hold!.Decision!.DiscriminatingEvidence);
        Assert.NotEqual(MultiRepositoryLandingFailedHold.Kind, next.Hold.Decision.DiscriminatingEvidence);
        crosswalk.Git("merge", "--no-edit", Git(f.SecondaryWorktree, "rev-parse", "HEAD"));
        Assert.Equal(wanted, crosswalk.Git("show", "main:example.txt"));
        Assert.Equal("fixture secondary change", wanted);
    }

    [Theory]
    [InlineData(false, "no-failure-record")]
    [InlineData(true, "manual-repair-required")]
    public void Run_MandatoryRefusal_LeavesRefsWorktreesAndRecordsUnchanged(bool manual, string reason)
    {
        using var f = new MultiRepositoryAcceptanceGateFixture();
        f.Gate.Run(f.Goal, GoalLifecycleState.Verified, f.RunHome);
        Assert.NotNull(f.Gate.Store.TryRead(f.Goal.Id));
        var store = Store(f);
        var tuple = f.Tuple().Canonical;
        store.Write(new MultiRepositoryLandingAttempt(f.Goal.Id, tuple, []));
        if (manual)
            store.Write(new MultiRepositoryLandingFailure(f.Goal.Id, tuple, "default", "fixture",
                new Dictionary<string, IReadOnlyList<string>> { ["crosswalk"] = ["missing-commit"] },
                ["crosswalk"], DateTimeOffset.UtcNow));
        var before = Snapshot(f);

        var result = Repair(f).Run(f.Goal);

        Assert.False(result.Repaired);
        Assert.Equal(reason, result.Reason);
        Assert.Equal(before, Snapshot(f));
    }

    [Theory]
    [InlineData("dirty", "workspace-dirty")]
    [InlineData("commit", "repository-unavailable")]
    [InlineData("branch", "repository-unavailable")]
    public void Run_UnsafeInputs_RefusesBeforeMutation(string input, string reason)
    {
        using var f = FailedTuple();
        if (input == "dirty") File.WriteAllText(Path.Combine(f.SecondaryWorktree, "untracked.txt"), "preserve");
        if (input == "commit")
        {
            var store = Store(f);
            store.Write(store.ReadFailure(f.Goal.Id)! with
            {
                CompensatingCommits = new Dictionary<string, IReadOnlyList<string>> { ["crosswalk"] = [new string('a', 40)] }
            });
        }
        if (input == "branch")
        {
            var branch = Git(f.SecondaryWorktree, "symbolic-ref", "--short", "HEAD");
            Git(f.SecondaryWorktree, "checkout", "--detach");
            f.Repositories.Crosswalk.Git("branch", "-D", branch);
        }
        var before = Snapshot(f);

        var result = Repair(f).Run(f.Goal);

        Assert.Equal(reason, result.Reason);
        Assert.Equal(before, Snapshot(f));
    }

    [Fact]
    public void Run_RevertConflict_PreservesCandidateAndCanRetry()
    {
        using var f = FailedTuple();
        // A later integration edit conflicts with reversing the compensation.
        GoalRepositoryWorktreeProvisionerFixture.CommitChange(f.Repositories.Crosswalk.CheckoutPath, "unrelated edit\n");
        var before = Git(f.SecondaryWorktree, "rev-parse", "HEAD");
        var integration = f.Repositories.Crosswalk.Git("rev-parse", "main");
        var result = Repair(f).Run(f.Goal);

        Assert.False(result.Repaired);
        Assert.Equal("repair-conflict", result.Reason);
        Assert.Equal(before, Git(f.SecondaryWorktree, "rev-parse", "HEAD"));
        Assert.Equal("", Git(f.SecondaryWorktree, "status", "--porcelain"));
        Assert.Equal(integration, f.Repositories.Crosswalk.Git("rev-parse", "main"));
        Assert.NotNull(Store(f).ReadFailure(f.Goal.Id));
        Assert.NotNull(Store(f).ReadAttempt(f.Goal.Id));
        // Remove the conflicting integration edit additively, then resume the same repair.
        f.Repositories.Crosswalk.Git("revert", "--no-edit", "HEAD");
        var retried = Repair(f).Run(f.Goal);
        Assert.True(retried.Repaired, retried.Detail);
    }

    [Fact]
    public void Run_RevertConflict_ResolvedCandidateCanRetry()
    {
        using var f = FailedTuple();
        var crosswalk = f.Repositories.Crosswalk;
        GoalRepositoryWorktreeProvisionerFixture.CommitChange(crosswalk.CheckoutPath, "unrelated edit\n");
        var integration = crosswalk.Git("rev-parse", "main");
        var startingTip = Git(f.SecondaryWorktree, "rev-parse", "HEAD");
        var conflicted = Repair(f).Run(f.Goal);
        Assert.Equal("repair-conflict", conflicted.Reason);
        Assert.Equal(startingTip, Git(f.SecondaryWorktree, "rev-parse", "HEAD"));
        Assert.Null(Store(f).ReadAttempt(f.Goal.Id)!.RepairProgress!["crosswalk"].RebuiltTip);

        // Resolve on the candidate: retain integration history but restore the compensation's
        // tree so its reversal can apply. The integration branch needs no operator edit.
        Git(f.SecondaryWorktree, "merge", "--no-edit", integration);
        var resolved = GoalRepositoryWorktreeProvisionerFixture.CommitChange(f.SecondaryWorktree, "seed\n");
        Assert.NotEqual(startingTip, resolved);

        var retried = Repair(f).Run(f.Goal);

        Assert.True(retried.Repaired, retried.Detail);
        Git(f.SecondaryWorktree, "merge-base", "--is-ancestor", resolved, "HEAD");
        Assert.Equal(integration, crosswalk.Git("rev-parse", "main"));
        Assert.Equal("fixture secondary change", Git(f.SecondaryWorktree, "show", "HEAD:example.txt"));
        Assert.Null(Store(f).ReadFailure(f.Goal.Id));
        Assert.Null(Store(f).ReadAttempt(f.Goal.Id));
    }

    [Fact]
    public void Run_CandidateMovesBeforePublication_RestartsWithItsNewWork()
    {
        using var f = FailedTuple();
        var integration = f.Repositories.Crosswalk.Git("rev-parse", "main");
        var branch = Git(f.SecondaryWorktree, "symbolic-ref", "--short", "HEAD");
        var interrupted = 0;
        using (LandingExecutor.UseGitRunner((root, args) =>
        {
            if (args.SequenceEqual(new[] { "rev-parse", "refs/heads/" + branch }))
            {
                interrupted++;
                File.WriteAllText(Path.Combine(f.SecondaryWorktree, "later.txt"), "preserve this candidate work\n");
                Git(f.SecondaryWorktree, "add", "later.txt");
                Git(f.SecondaryWorktree, "commit", "-m", "Later candidate work");
            }
            return GitCli.Run(root, args);
        }))
        {
            var result = Repair(f).Run(f.Goal);
            Assert.Equal("repair-conflict", result.Reason);
            Assert.Contains("candidate changed before publication", result.Detail);
        }
        Assert.Equal(1, interrupted);
        var progress = Store(f).ReadAttempt(f.Goal.Id)!.RepairProgress!["crosswalk"];
        Assert.NotNull(progress.RebuiltTip);
        Assert.True(Directory.Exists(progress.WorktreePath));
        var movedTip = Git(f.SecondaryWorktree, "rev-parse", "HEAD");
        Assert.NotEqual(progress.StartingTip, movedTip);
        Assert.Equal(1, GitCli.Run(f.SecondaryWorktree,
            ["merge-base", "--is-ancestor", progress.RebuiltTip!, movedTip]).ExitCode);

        var retried = Repair(f).Run(f.Goal);

        Assert.True(retried.Repaired, retried.Detail);
        Assert.False(Directory.Exists(progress.WorktreePath));
        Git(f.SecondaryWorktree, "merge-base", "--is-ancestor", movedTip, "HEAD");
        Assert.Equal("preserve this candidate work", Git(f.SecondaryWorktree, "show", "HEAD:later.txt"));
        Assert.Equal("fixture secondary change", Git(f.SecondaryWorktree, "show", "HEAD:example.txt"));
        Assert.Equal(integration, f.Repositories.Crosswalk.Git("rev-parse", "main"));
        Assert.Null(Store(f).ReadFailure(f.Goal.Id));
        Assert.Null(Store(f).ReadAttempt(f.Goal.Id));
    }

    [Fact]
    public void Run_ConflictCleanupFails_RetainsNamedReasonAndCanRetry()
    {
        using var f = FailedTuple();
        var candidate = Git(f.SecondaryWorktree, "rev-parse", "HEAD");
        var integration = f.Repositories.Crosswalk.Git("rev-parse", "main");
        var reverts = 0;
        var removals = 0;
        using (LandingExecutor.UseGitRunner((root, args) =>
        {
            if (args[0] == "revert" && args.Contains("--no-edit"))
            {
                reverts++;
                return new GitCli.GitResult(1, "", "fixture revert failed");
            }
            if (args[0] == "worktree" && args[1] == "remove")
            {
                removals++;
                return new GitCli.GitResult(1, "", "fixture cleanup failed");
            }
            return GitCli.Run(root, args);
        }))
        {
            var result = Repair(f).Run(f.Goal);
            Assert.Equal("repair-conflict", result.Reason);
            Assert.Contains("fixture revert failed", result.Detail);
            Assert.Contains("fixture cleanup failed", result.Detail);
        }
        Assert.Equal(1, reverts);
        Assert.Equal(1, removals);
        var progress = Store(f).ReadAttempt(f.Goal.Id)!.RepairProgress!["crosswalk"];
        Assert.Null(progress.RebuiltTip);
        Assert.True(Directory.Exists(progress.WorktreePath));
        Assert.NotNull(Store(f).ReadFailure(f.Goal.Id));
        Assert.Equal(candidate, Git(f.SecondaryWorktree, "rev-parse", "HEAD"));
        Assert.Equal(integration, f.Repositories.Crosswalk.Git("rev-parse", "main"));

        var retried = Repair(f).Run(f.Goal);

        Assert.True(retried.Repaired, retried.Detail);
        Assert.False(Directory.Exists(progress.WorktreePath));
        Assert.Equal(integration, f.Repositories.Crosswalk.Git("rev-parse", "main"));
        Assert.Equal("fixture secondary change", Git(f.SecondaryWorktree, "show", "HEAD:example.txt"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_PublicationInterrupted_ResumesWithoutDoubleRevert(bool published)
    {
        using var f = FailedTuple();
        var candidate = Git(f.SecondaryWorktree, "rev-parse", "HEAD");
        var integration = f.Repositories.Crosswalk.Git("rev-parse", "main");
        var intercepted = 0;
        using (LandingExecutor.UseGitRunner((root, args) =>
        {
            if (Path.GetFullPath(root) == Path.GetFullPath(f.SecondaryWorktree) &&
                args[0] == "merge" && args.Contains("--ff-only"))
            {
                intercepted++;
                if (published) Assert.True(GitCli.Run(root, args).Succeeded);
                throw new IOException("fixture interrupted publication");
            }
            return GitCli.Run(root, args);
        }))
        {
            Assert.Equal("repair-conflict", Repair(f).Run(f.Goal).Reason);
        }
        Assert.Equal(1, intercepted);
        var progress = Assert.Single(Store(f).ReadAttempt(f.Goal.Id)!.RepairProgress!).Value;
        Assert.NotNull(progress.RebuiltTip);
        Assert.NotNull(Store(f).ReadFailure(f.Goal.Id));
        Assert.Equal(published ? progress.RebuiltTip : candidate, Git(f.SecondaryWorktree, "rev-parse", "HEAD"));

        var result = Repair(f).Run(f.Goal);

        Assert.True(result.Repaired, result.Detail);
        Assert.Equal(progress.RebuiltTip, Git(f.SecondaryWorktree, "rev-parse", "HEAD"));
        Assert.Equal(integration, f.Repositories.Crosswalk.Git("rev-parse", "main"));
        Assert.Equal("fixture secondary change", Git(f.SecondaryWorktree, "show", "HEAD:example.txt"));
        Assert.Null(Store(f).ReadAttempt(f.Goal.Id));
    }

    [Fact]
    public void Run_EmptyCompensation_ClearsRecoveryWithoutGitMutation()
    {
        using var f = new MultiRepositoryAcceptanceGateFixture();
        Store(f).Write(new MultiRepositoryLandingFailure(f.Goal.Id, f.Tuple().Canonical,
            "default", "fixture", new Dictionary<string, IReadOnlyList<string>>(), [], DateTimeOffset.UtcNow));
        var before = Repositories(f);
        Assert.True(Repair(f).Run(f.Goal).Repaired);
        Assert.Equal(before, Repositories(f));
        Assert.Null(Store(f).ReadFailure(f.Goal.Id));
        Assert.Null(Store(f).ReadAttempt(f.Goal.Id));
    }

    [Fact]
    public void Run_LaterRepositoryFails_RetainsEarlierRebuildForResume()
    {
        using var f = new MultiRepositoryAcceptanceGateFixture();
        f.HomeLanding = () =>
        {
            var landing = LandingExecutor.Execute(f.Repositories.Kernel, f.Goal, f.Repositories.Workspace);
            Assert.True(landing.MainAdvanced, landing.Message);
            GoalOperationJournal.Completed(f.Repositories.Home.CheckoutPath, f.Goal, "conductor:land");
            throw new IOException("fixture failure after both repositories landed");
        };
        Assert.IsType<ConductorAdvanceOutcome.Held>(f.Driver().AdvanceOnce(f.Goal,
            ConductorAutonomyPolicy.Conservative).Outcome);
        var failure = Store(f).ReadFailure(f.Goal.Id)!;
        Assert.Empty(failure.ManualRepairKeys);
        Assert.Equal(2, failure.CompensatingCommits.Count);
        var integration = f.Repositories.Crosswalk.Git("rev-parse", "main");
        var homeIntegration = f.Repositories.Home.Git("rev-parse", "main");
        var homeStart = Git(f.HomeWorktree, "rev-parse", "HEAD");
        var interrupted = 0;
        using (LandingExecutor.UseGitRunner((root, args) =>
        {
            if (root.Contains("-default-", StringComparison.Ordinal) && args[0] == "revert" && args.Contains("--no-edit"))
            {
                interrupted++;
                // The preceding repository's prepared/published tip must already be durable.
                var saved = Store(f).ReadAttempt(f.Goal.Id)!.RepairProgress!["crosswalk"];
                Assert.Equal(saved.RebuiltTip, Git(f.SecondaryWorktree, "rev-parse", "HEAD"));
                return new GitCli.GitResult(1, "", "fixture later repository failed");
            }
            return GitCli.Run(root, args);
        }))
        {
            Assert.Equal("repair-conflict", Repair(f).Run(f.Goal).Reason);
        }
        Assert.Equal(1, interrupted);
        Assert.Equal(homeStart, Git(f.HomeWorktree, "rev-parse", "HEAD"));
        var rebuilt = Git(f.SecondaryWorktree, "rev-parse", "HEAD");
        // Candidate work added after a completed rebuild remains intact on retry.
        File.WriteAllText(Path.Combine(f.SecondaryWorktree, "later.txt"), "preserve this candidate work\n");
        Git(f.SecondaryWorktree, "add", "later.txt");
        Git(f.SecondaryWorktree, "commit", "-m", "Later candidate work");
        var later = Git(f.SecondaryWorktree, "rev-parse", "HEAD");

        var result = Repair(f).Run(f.Goal);

        Assert.True(result.Repaired, result.Detail);
        Assert.Equal(later, Git(f.SecondaryWorktree, "rev-parse", "HEAD"));
        Git(f.SecondaryWorktree, "merge-base", "--is-ancestor", rebuilt, "HEAD");
        Assert.Equal(integration, f.Repositories.Crosswalk.Git("rev-parse", "main"));
        Assert.Equal(homeIntegration, f.Repositories.Home.Git("rev-parse", "main"));
        Assert.Equal("fixture home change", Git(f.HomeWorktree, "show", "HEAD:example.txt"));
        Assert.Null(Store(f).ReadFailure(f.Goal.Id));
        Assert.Null(Store(f).ReadAttempt(f.Goal.Id));
    }

    [Fact]
    public void Run_InterruptedPreparation_RestartsFromUntouchedCandidate()
    {
        using var f = FailedTuple();
        var before = Git(f.SecondaryWorktree, "rev-parse", "HEAD");
        var interrupted = 0;
        using (LandingExecutor.UseGitRunner((root, args) =>
        {
            var result = GitCli.Run(root, args);
            if (args[0] == "revert" && args.Contains("--no-edit"))
            {
                Assert.True(result.Succeeded, result.Error);
                interrupted++;
                throw new IOException("fixture stop after revert before progress write");
            }
            return result;
        }))
        {
            Assert.Equal("repair-conflict", Repair(f).Run(f.Goal).Reason);
        }
        Assert.Equal(1, interrupted);
        Assert.Equal(before, Git(f.SecondaryWorktree, "rev-parse", "HEAD"));
        Assert.Null(Store(f).ReadAttempt(f.Goal.Id)!.RepairProgress!["crosswalk"].RebuiltTip);
        var result = Repair(f).Run(f.Goal);
        Assert.True(result.Repaired, result.Detail);
        Assert.Equal("fixture secondary change", Git(f.SecondaryWorktree, "show", "HEAD:example.txt"));
    }

    [Fact]
    public void Run_MultipleCompensations_UndoesThemInReverseApplicationOrder()
    {
        using var f = new MultiRepositoryAcceptanceGateFixture();
        var first = Git(f.SecondaryWorktree, "rev-parse", "HEAD");
        var second = GoalRepositoryWorktreeProvisionerFixture.CommitChange(f.SecondaryWorktree, "second dependent edit\n");
        var crosswalk = f.Repositories.Crosswalk;
        crosswalk.Git("merge", "--ff-only", second);
        crosswalk.Git("revert", "--no-edit", second);
        var compensation1 = crosswalk.Git("rev-parse", "HEAD");
        crosswalk.Git("revert", "--no-edit", first);
        var compensation2 = crosswalk.Git("rev-parse", "HEAD");
        Store(f).Write(new MultiRepositoryLandingFailure(f.Goal.Id, f.Tuple().Canonical, "default", "fixture",
            new Dictionary<string, IReadOnlyList<string>> { ["crosswalk"] = [compensation1, compensation2] }, [], DateTimeOffset.UtcNow));
        Assert.Equal("seed", crosswalk.Git("show", "main:example.txt"));

        var result = Repair(f).Run(f.Goal);

        Assert.True(result.Repaired, result.Detail);
        Assert.Equal(compensation2, crosswalk.Git("rev-parse", "main"));
        Assert.Equal("second dependent edit", Git(f.SecondaryWorktree, "show", "HEAD:example.txt"));
        crosswalk.Git("merge", "--no-edit", Git(f.SecondaryWorktree, "rev-parse", "HEAD"));
        Assert.Equal("second dependent edit", crosswalk.Git("show", "main:example.txt"));
    }

    private static MultiRepositoryAcceptanceGateFixture FailedTuple()
    {
        var f = new MultiRepositoryAcceptanceGateFixture();
        f.HomeLanding = () => throw new IOException("fixture default landing failed");
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(f.Driver().AdvanceOnce(f.Goal,
            ConductorAutonomyPolicy.Conservative).Outcome);
        Assert.Equal(MultiRepositoryLandingFailedHold.Kind, held.Decision!.DiscriminatingEvidence);
        Assert.Empty(Store(f).ReadFailure(f.Goal.Id)!.ManualRepairKeys);
        return f;
    }

    private static MultiRepositoryLandingRepair Repair(MultiRepositoryAcceptanceGateFixture f) =>
        new(f.Repositories.Workspace, f.Repositories.Registry);
    private static MultiRepositoryLandingRecordStore Store(MultiRepositoryAcceptanceGateFixture f) =>
        new(f.Repositories.Workspace.OrchestratorDirectory);
    private static string Git(string root, params string[] args) => GoalRepositoryWorktreeProvisionerFixture.Run(root, args);
    private static string Repositories(MultiRepositoryAcceptanceGateFixture f) => JsonSerializer.Serialize(
        new[] { f.Repositories.Home.CheckoutPath, f.Repositories.Crosswalk.CheckoutPath, f.HomeWorktree, f.SecondaryWorktree }
            .Select(path => new { Refs = Git(path, "show-ref"), Head = Git(path, "rev-parse", "HEAD"),
                Status = Git(path, "status", "--porcelain"), Diff = Git(path, "diff", "HEAD"),
                Worktrees = Git(path, "worktree", "list", "--porcelain") }));
    private static string Snapshot(MultiRepositoryAcceptanceGateFixture f) => JsonSerializer.Serialize(new
    {
        Repos = Repositories(f), Failure = Store(f).ReadFailure(f.Goal.Id),
        Attempt = Store(f).ReadAttempt(f.Goal.Id), Landing = Store(f).ReadLanding(f.Goal.Id),
        Receipt = f.Gate.Store.TryRead(f.Goal.Id)
    });
}
