using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static AdditiveConflictGitFixture;
using static ConductorConflictIntegrationRecordTests;
using static ConductorConflictIntegrationValidateTests;
using static ConductorDriverTests;

// Parallel-safe: each case owns its repository, worktree, kernel and journal. Git callbacks
// inject branch movements at command boundaries; no timing or shared process state is used.
public sealed class ConductorConflictIntegrationStaleRoundTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Validate_LaterCommit_PreservesBranchAndRetiresRound(bool operatorMerge)
    {
        using var fixture = new Fixture();
        fixture.Admit();
        var merge = fixture.Resolve(marker: true);
        var git = fixture.GitFixture;
        if (operatorMerge)
        {
            File.WriteAllText(Path.Combine(git.Repository, "src/MainLater.cs"), "main later\n");
            Commit(git.Repository, "Later main commit");
            Git(git.Worktree, "merge", "--no-ff", "--no-edit", "main");
        }
        else AddLaterCommit(git.Worktree);
        var later = Git(git.Worktree, "rev-parse", "HEAD").Trim();
        Assert.NotEqual(merge, later);

        var reason = fixture.Coordinator.Validate(fixture.Goal);

        Assert.StartsWith("pre-landing rebase conflict (src/Widget.cs)", reason);
        Assert.Contains(operatorMerge ? "scope check" : "conflict marker check", reason);
        Assert.Contains("branch preserved at " + later, reason);
        AssertPreserved(fixture, later, "restore");
        Assert.Equal(0, fixture.BuildCalls);
    }

    [Fact]
    public void Validate_OrdinaryCommit_PreservesWrongParentCandidate()
    {
        using var fixture = new Fixture();
        fixture.Admit();
        Git(fixture.GitFixture.Worktree, "merge", "--abort");
        var later = AddLaterCommit(fixture.GitFixture.Worktree);

        var reason = fixture.Coordinator.Validate(fixture.Goal);

        Assert.Contains("merge parent check", reason);
        AssertPreserved(fixture, later, "restore");
        Assert.Equal(0, fixture.BuildCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Validate_BuildMovesBranch_PreservesHeadObservedAfterBuild(bool success)
    {
        string? later = null;
        using var fixture = new Fixture(buildCheck: worktree =>
        {
            later = AddLaterCommit(worktree);
            return new(true, success ? 0 : 1, "build finished after branch moved");
        });
        fixture.Admit();
        fixture.Resolve();

        var reason = fixture.Coordinator.Validate(fixture.Goal);

        Assert.Contains(success ? "candidate moved during build check" : "build check: build finished after branch moved", reason);
        Assert.Equal(1, fixture.BuildCalls);
        Assert.NotNull(later);
        AssertPreserved(fixture, later, "restore");
    }

    [Theory]
    [InlineData("update-ref")]
    [InlineData("cleanup")]
    public void Validate_RestoreRace_PreservesInterveningCommit(string boundary)
    {
        string? later = null;
        var moved = false;
        var commands = new List<string>();
        using var fixture = new Fixture(restoreGit: (worktree, args) =>
        {
            commands.Add(string.Join(' ', args));
            if (!moved && (args[0] == boundary || (boundary == "cleanup" && args[0] != "update-ref")))
            {
                Assert.NotNull(later);
                // Move only the branch ref, leaving the round's uncommitted merge in place.
                Git(worktree, "update-ref", "HEAD", later);
                moved = true;
            }
            return GitCli.Run(worktree, args);
        });
        fixture.Admit();
        var git = fixture.GitFixture;
        var tree = Git(git.Worktree, "rev-parse", git.OriginalHead + "^{tree}").Trim();
        later = Git(git.Worktree, "commit-tree", tree, "-p", git.OriginalHead, "-m", "Intervening commit").Trim();

        var reason = fixture.Coordinator.Validate(fixture.Goal);

        Assert.True(moved, "The branch movement must run at the selected restore command.");
        Assert.Contains("merge still in progress", reason);
        AssertPreserved(fixture, later, "restore");
        if (boundary == "update-ref")
        {
            Assert.Single(commands);
            Assert.Equal(git.MainHead, Git(git.Worktree, "rev-parse", "MERGE_HEAD").Trim());
        }
    }

    [Fact]
    public void Validate_OwnMarkerMerge_RestoresWithoutStaleEvent()
    {
        using var fixture = new Fixture();
        fixture.Admit();
        fixture.Resolve(marker: true);

        var reason = fixture.Coordinator.Validate(fixture.Goal);

        Assert.Contains("conflict marker check: src/Widget.cs", reason);
        Assert.Contains("branch restored to " + fixture.GitFixture.OriginalHead[..12], reason);
        fixture.GitFixture.AssertRestored();
        Assert.Equal(GoalOperationStatus.Failed, fixture.Entry.Status);
        Assert.DoesNotContain(fixture.Goal.Timeline, item => item.Message.StartsWith("conflict-round-stale", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Validate_WorktreeNotOnGoalBranch_PreservesBothHeads(bool detached)
    {
        var restores = 0;
        using var fixture = new Fixture(restoreGit: (worktree, args) =>
        {
            restores++;
            return GitCli.Run(worktree, args);
        });
        fixture.Admit();
        var merge = fixture.Resolve(marker: true);
        var git = fixture.GitFixture;
        if (detached) Git(git.Worktree, "checkout", "--detach");
        else Git(git.Worktree, "checkout", "-b", "other-branch");
        var tree = Git(git.Worktree, "rev-parse", "HEAD^{tree}").Trim();
        var later = Git(git.Worktree, "commit-tree", tree, "-p", merge, "-m", "Later goal commit").Trim();
        var branch = "refs/heads/" + GoalWorktrees.BranchName(git.GoalId);
        Git(git.Repository, "update-ref", branch, later, merge);

        var reason = fixture.Coordinator.Validate(fixture.Goal);

        Assert.Contains("compare-and-swap restore failed; operator recovery required", reason);
        Assert.Equal(0, restores);
        Assert.Equal(later, Git(git.Repository, "rev-parse", branch).Trim());
        Assert.Equal(merge, Git(git.Worktree, "rev-parse", "HEAD").Trim());
        Assert.Equal(GoalOperationStatus.Failed, fixture.Entry.Status);
    }

    [Fact]
    public void Dispatch_HeadMoved_RetiresOldRoundAndAllowsFreshRound()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Coordinator.TryRecord(fixture.Goal).Started);
        var later = AddLaterCommit(fixture.GitFixture.Worktree);

        var result = fixture.Coordinator.IntegrateMainBeforeDeveloperDispatch(fixture.Goal);

        Assert.Equal(DeveloperBranchIntegrationStatus.Conflict, result.Status);
        AssertPreserved(fixture, later, "dispatch");
        Assert.Equal(1, GitCli.Run(fixture.GitFixture.Worktree, "rev-parse", "--verify", "--quiet", "MERGE_HEAD").ExitCode);
        Assert.DoesNotContain("<<<<<<<", fixture.GitFixture.Read());
        PassVerification(fixture.Kernel, fixture.Goal, fixture.Developer);
        var recorded = fixture.Coordinator.TryRecord(fixture.Goal);
        Assert.True(recorded.Started, recorded.Detail);
        Assert.Equal(GoalOperationStatus.Begin, fixture.Entry.Status);
        using var round = JsonDocument.Parse(fixture.Entry.Detail!);
        Assert.Equal(later, round.RootElement.GetProperty("OriginalHead").GetString());
    }

    [Fact]
    public void TryRecord_HeadMoved_RetiresOldRoundBeforeRecordingReplacement()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Coordinator.TryRecord(fixture.Goal).Started);
        var later = AddLaterCommit(fixture.GitFixture.Worktree);
        PassVerification(fixture.Kernel, fixture.Goal, fixture.Developer);

        var result = fixture.Coordinator.TryRecord(fixture.Goal);

        Assert.True(result.Started, result.Detail);
        var entries = GoalOperationJournal.Read(fixture.GitFixture.Repository, fixture.Goal.Id).Entries;
        Assert.Contains(entries, entry => entry.Operation == fixture.Entry.Operation && entry.Status == GoalOperationStatus.Failed &&
            entry.Detail!.Contains(fixture.GitFixture.OriginalHead, StringComparison.Ordinal));
        AssertStaleEvent(fixture, later, "record");
        using var round = JsonDocument.Parse(fixture.Entry.Detail!);
        Assert.Equal(later, round.RootElement.GetProperty("OriginalHead").GetString());
    }

    [Fact]
    public void Dispatch_OwnMerge_KeepsRoundForValidation()
    {
        using var fixture = new Fixture();
        fixture.Admit();
        fixture.Resolve(marker: true);

        fixture.Coordinator.IntegrateMainBeforeDeveloperDispatch(fixture.Goal);

        Assert.Equal(GoalOperationStatus.Begin, fixture.Entry.Status);
        Assert.Contains("conflict marker check", fixture.Coordinator.Validate(fixture.Goal));
        fixture.GitFixture.AssertRestored();
        Assert.DoesNotContain(fixture.Goal.Timeline, item => item.Message.StartsWith("conflict-round-stale", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dispatch_AdmissionFailure_RestoresOnlyUnmovedBranch(bool moveBranch)
    {
        string? later = null;
        var merged = false;
        using var fixture = new Fixture(mergeGit: (worktree, args) =>
        {
            var mergeResult = GitCli.Run(worktree, args);
            Assert.Equal(1, mergeResult.ExitCode);
            Assert.False(mergeResult.DrainTimedOut);
            merged = true;
            if (moveBranch)
            {
                Assert.NotNull(later);
                Git(worktree, "update-ref", "HEAD", later);
            }
            // An apparatus failure after Git leaves the merge must enter admission recovery.
            return mergeResult with { DrainTimedOut = true };
        });
        var git = fixture.GitFixture;
        Assert.True(fixture.Coordinator.TryRecord(fixture.Goal).Started);
        var tree = Git(git.Worktree, "rev-parse", git.OriginalHead + "^{tree}").Trim();
        later = Git(git.Worktree, "commit-tree", tree, "-p", git.OriginalHead, "-m", "Later goal commit").Trim();

        var result = fixture.Coordinator.IntegrateMainBeforeDeveloperDispatch(fixture.Goal);

        Assert.True(merged, "The real conflicted merge must run before admission recovery.");
        Assert.Equal(DeveloperBranchIntegrationStatus.Conflict, result.Status);
        if (moveBranch)
        {
            AssertPreserved(fixture, later, "dispatch");
            Assert.Equal(git.MainHead, Git(git.Worktree, "rev-parse", "MERGE_HEAD").Trim());
        }
        else
        {
            git.AssertRestored();
            Assert.Equal(GoalOperationStatus.Begin, fixture.Entry.Status);
            Assert.DoesNotContain(fixture.Goal.Timeline, item => item.Message.StartsWith("conflict-round-stale", StringComparison.Ordinal));
        }
    }

    private static string AddLaterCommit(string worktree)
    {
        File.AppendAllText(Path.Combine(worktree, WidgetPath), "// later goal change\n");
        Commit(worktree, "Later goal commit");
        return Git(worktree, "rev-parse", "HEAD").Trim();
    }

    private static void AssertPreserved(Fixture fixture, string later, string site)
    {
        var git = fixture.GitFixture;
        Assert.Equal(later, Git(git.Worktree, "rev-parse", "HEAD").Trim());
        Assert.Equal(later, Git(git.Repository, "rev-parse", "refs/heads/" + GoalWorktrees.BranchName(git.GoalId)).Trim());
        Assert.Equal(GoalOperationStatus.Failed, fixture.Entry.Status);
        Assert.False(File.Exists(MarkerPath(git.Worktree)));
        AssertStaleEvent(fixture, later, site);
        var builds = fixture.BuildCalls;
        Assert.Null(fixture.Coordinator.Validate(fixture.Goal));
        Assert.Equal(builds, fixture.BuildCalls);
    }

    private static void AssertStaleEvent(Fixture fixture, string later, string site)
    {
        var message = Assert.Single(fixture.Goal.Timeline,
            item => item.Message.StartsWith("conflict-round-stale", StringComparison.Ordinal)).Message;
        Assert.Equal($"conflict-round-stale original={fixture.GitFixture.OriginalHead} head={later} site={site}", message);
    }
}
