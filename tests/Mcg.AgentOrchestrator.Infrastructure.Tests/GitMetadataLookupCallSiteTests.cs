using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixture-owned repositories, durable stores and thread-local start observation.
public sealed class GitMetadataLookupCallSiteTests
{
    [Fact]
    public void MetadataExcludeAndMarker_LinkedWorktreeStartNoGit()
    {
        using var fixture = new GitMetadataLookupFixture();
        var admin = GitWorkspaceLaunchFixture.AdminDirectory(fixture.Linked);
        var marker = Path.Combine(admin, "mcg-conductor-integration.json");
        var head = GitCli.RunDirect(fixture.Linked, "rev-parse", "--verify", "HEAD").Output.Trim();
        File.WriteAllText(marker, JsonSerializer.Serialize(new
        {
            TaskId = TaskId.New().Value, RetryAt = DateTimeOffset.UnixEpoch,
            OriginalHead = head, MainSha = head
        }));
        using var recording = new GitWorkspaceLaunchFixture.Recording();

        var metadata = GoalWorktreeGitMetadata.Inspect(fixture.Linked,
            new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget));
        Assert.Equal(Path.Combine(admin, "index.lock"), metadata.IndexLockPath);
        Assert.True(metadata.CurrentProcessCanWriteIndexLock, metadata.Error);
        Assert.True(LocalGitExclude.TryAppendEntries(fixture.Linked, ["lookup-only-local/"]));
        Assert.Contains("lookup-only-local/", File.ReadAllText(Path.Combine(fixture.Main, ".git", "info", "exclude")));
        Assert.True(ConductorConflictIntegrationMarker.TryReadRecordedHeads(fixture.Linked, out var original, out var main));
        Assert.Equal(head, original);
        Assert.Equal(head, main);
        ConductorConflictIntegrationMarker.Remove(fixture.Linked);
        Assert.False(File.Exists(marker));

        Assert.Equal(0, recording.Close([]));
    }

    [Fact]
    public void LandingRepair_LinkedCandidateChecksEveryMarkerWithoutPathLaunches()
    {
        using var fixture = new GoalRepositoriesTestFixture();
        var goal = fixture.CreateGoal();
        var checkout = fixture.Crosswalk.WorktreePath(goal.Id, "crosswalk");
        fixture.Crosswalk.Git("worktree", "add", "-b", GoalRepositoryWorktreeLayout.BranchName(goal.Id, "crosswalk"),
            checkout, "main");
        var admin = GitWorkspaceLaunchFixture.AdminDirectory(checkout);
        // The final marker makes preflight refuse after all six path lookups, before repair mutates refs.
        File.WriteAllText(Path.Combine(admin, "rebase-apply"), string.Empty);
        var store = new MultiRepositoryLandingRecordStore(fixture.Workspace.OrchestratorDirectory);
        store.Write(new MultiRepositoryLandingFailure(goal.Id, "fixture-tuple", "default", "fixture",
            new Dictionary<string, IReadOnlyList<string>> { ["crosswalk"] = [fixture.Crosswalk.Git("rev-parse", "main")] },
            [], DateTimeOffset.UnixEpoch));
        var observedPaths = new List<string>();
        using var runner = LandingExecutor.UseGitRunner((root, arguments) =>
        {
            if (arguments.Contains("--git-path"))
            {
                Assert.Equal(Path.GetFullPath(checkout), Path.GetFullPath(root));
                observedPaths.Add(arguments[^1]);
            }
            return GitCli.Run(root, arguments);
        });
        using var recording = new GitWorkspaceLaunchFixture.Recording();

        var result = new MultiRepositoryLandingRepair(fixture.Workspace, fixture.Registry).Run(goal);

        Assert.False(result.Repaired);
        Assert.Equal("workspace-dirty", result.Reason);
        Assert.Equal(new[] { "MERGE_HEAD", "REVERT_HEAD", "CHERRY_PICK_HEAD", "sequencer", "rebase-merge", "rebase-apply" },
            observedPaths);
        _ = recording.Close(null);
        Assert.DoesNotContain(recording.Arguments, IsPathLookup);
        Assert.Contains(recording.Arguments, arguments => arguments[0] == "status");
    }

    [Fact]
    public void TreeDigest_LinkedWorktreeUsesNoPathLookupProcess()
    {
        using var fixture = new WorktreeTreeDigestFixture();
        var checkout = Path.Combine(fixture.Root, "linked");
        fixture.Git("worktree", "add", "--detach", checkout, "HEAD");
        var expected = fixture.Compute();
        fixture.Repo = checkout;

        Assert.Equal(expected, fixture.Compute());

        // Digest launches bypass the wrapper census; this fixture observes the actual start seam.
        Assert.DoesNotContain(fixture.Launches, launch => IsPathLookup(launch.Arguments));
        Assert.Equal(new[] { "status" }, fixture.Launches.Select(launch => launch.Arguments[0]));
    }

    private static bool IsPathLookup(string[] arguments) => arguments[0] == "rev-parse" &&
        arguments.Any(argument => argument is "--git-path" or "--show-toplevel" or "--git-dir" or
            "--git-common-dir" or "--is-inside-work-tree");
}
