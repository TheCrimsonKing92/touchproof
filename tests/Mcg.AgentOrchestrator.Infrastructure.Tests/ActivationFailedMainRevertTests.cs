using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ActivationFailedMainRevertTests
{
    [Fact]
    public void RevertedActivationPublishesOneFirstParentRevert()
    {
        using var repo = new Repository();
        var worktrees = repo.Worktrees;
        Assert.Empty(repo.Git("status", "--porcelain"));
        Assert.Equal("after", File.ReadAllText(Path.Combine(repo.Root, "change.txt")));

        repo.RecordReverted(repo.Supervisor());

        var revert = repo.Tip;
        Assert.NotEqual(repo.Merge, revert);
        Assert.Equal($"{revert} {repo.Merge}", repo.Git("rev-list", "--parents", "-1", revert));
        Assert.Equal(repo.Git("rev-parse", repo.FirstParent + "^{tree}"), repo.Git("rev-parse", revert + "^{tree}"));
        Assert.Equal("1", repo.Git("rev-list", "--count", $"{repo.Merge}..{revert}"));
        Assert.Collection(repo.Lines,
            line => Assert.StartsWith("ACTIVATION_REVERTED ", line),
            line => Assert.Equal($"ACTIVATION_MAIN_REVERTED failedCommit={repo.Merge} revertCommit={revert}", line));
        Assert.Empty(repo.Git("status", "--porcelain"));
        Assert.Equal("before", File.ReadAllText(Path.Combine(repo.Root, "change.txt")));
        repo.AssertRevertCleanedUp(worktrees);
    }

    [Fact]
    public void DirtyIntegrationCheckoutHoldsWithoutMovingBranch()
    {
        using var repo = new Repository();
        var worktrees = repo.Worktrees;
        repo.MakeCheckoutDirty();
        var stagedEntries = repo.Git("ls-files", "--stage");
        var status = repo.Git("status", "--porcelain");

        repo.RecordReverted(repo.Supervisor());

        Assert.Equal(repo.Merge, repo.Tip);
        Assert.Contains("root checkout", repo.HeldEvent(repo.Merge), StringComparison.Ordinal);
        Assert.Equal(stagedEntries, repo.Git("ls-files", "--stage"));
        Assert.Equal(status, repo.Git("status", "--porcelain"));
        Assert.Equal("after", File.ReadAllText(Path.Combine(repo.Root, "change.txt")));
        Assert.Equal("unstaged", File.ReadAllText(Path.Combine(repo.Root, "staged.txt")));
        Assert.Equal("untracked", File.ReadAllText(Path.Combine(repo.Root, "untracked.txt")));
        repo.AssertRevertCleanedUp(worktrees);
    }

    [Fact]
    public void CheckoutSyncFailureKeepsPublishedRevertOutcome()
    {
        using var repo = new Repository();
        var worktrees = repo.Worktrees;
        var syncAttempted = false;
        using var runner = LandingExecutor.UseGitRunner((directory, args) =>
        {
            if (directory == repo.Root && args is ["read-tree", "-m", "-u", _, _])
            {
                syncAttempted = true;
                return new GitCli.GitResult(1, "", "fixture checkout sync failed");
            }
            return GitCli.Run(directory, args);
        });

        repo.RecordReverted(repo.Supervisor());

        Assert.True(syncAttempted);
        Assert.NotEqual(repo.Merge, repo.Tip);
        Assert.Collection(repo.Lines,
            line => Assert.StartsWith("ACTIVATION_REVERTED ", line),
            line => Assert.Equal($"ACTIVATION_MAIN_REVERTED failedCommit={repo.Merge} revertCommit={repo.Tip}", line));
        repo.AssertRevertCleanedUp(worktrees);
    }

    [Theory]
    [InlineData("non-merge", "not a two-parent merge")]
    [InlineData("unreachable", "not reachable")]
    [InlineData("previous-revert", "already reverted")]
    [InlineData("conflict", "did not apply cleanly")]
    [InlineData("manual-revert", "already reverted")]
    [InlineData("empty", "revert is empty")]
    public void IneligibleOrConflictingMergeHolds(string setup, string reason)
    {
        using var repo = new Repository();
        var failed = repo.Merge;
        switch (setup)
        {
            case "non-merge":
                failed = repo.FirstParent;
                break;
            case "unreachable":
                repo.Git("checkout", "--detach", repo.FirstParent);
                repo.Git("update-ref", $"refs/heads/{Repository.Branch}", repo.FirstParent, repo.Merge);
                break;
            case "previous-revert":
                repo.Git("revert", "--no-edit", "-m", "1", repo.Merge);
                break;
            case "conflict":
                repo.CommitFile("change.txt", "later conflicting change", "Later conflicting change");
                break;
            case "manual-revert":
                repo.CommitFile("change.txt", "before", "Revert goal 91849616 landing (manual)");
                break;
            case "empty":
                repo.CommitFile("change.txt", "before", "Remove change without revert metadata");
                break;
        }
        var tip = repo.Tip;
        var worktrees = repo.Worktrees;
        var checkout = repo.MakeCheckoutDirty();
        string? temporary = null;
        string? aborted = null;
        using var runner = LandingExecutor.UseGitRunner((directory, args) =>
        {
            if (args is ["worktree", "add", "--detach", var path, _])
                temporary = path;
            if (args is ["revert", "--abort"])
                aborted = directory;
            return GitCli.Run(directory, args);
        });

        repo.RecordReverted(repo.Supervisor(), failed);

        Assert.Equal(tip, repo.Tip);
        Assert.Contains(reason, repo.HeldEvent(failed), StringComparison.Ordinal);
        repo.AssertCheckoutPreserved(checkout, worktrees);
        if (setup is "conflict" or "empty")
        {
            Assert.NotNull(temporary);
            Assert.Equal(temporary, aborted);
            Assert.False(Directory.Exists(temporary));
        }
    }

    [Fact]
    public void RepeatedActivationAddsOnlyOneRevert()
    {
        using var repo = new Repository();
        repo.SetPushPolicy(true);
        repo.Git("config", "revert.reference", "true");
        var pushed = new List<string>();
        var supervisor = repo.Supervisor(push: (_, commit, _) => { pushed.Add(commit); return true; });

        repo.RecordReverted(supervisor);
        var revert = repo.Tip;
        repo.RecordReverted(supervisor);

        Assert.Equal(revert, repo.Tip);
        Assert.Equal("1", repo.Git("rev-list", "--count", $"{repo.Merge}..{repo.Tip}"));
        Assert.Equal(revert, Assert.Single(pushed));
        Assert.Equal(2, repo.Events.Entries.Count(evt => evt.Operation == "activation" && evt.Status == "reverted"));
        Assert.Equal($"ACTIVATION_MAIN_REVERTED failedCommit={repo.Merge} revertCommit={revert}",
            Assert.Single(repo.Lines, line => line.StartsWith("ACTIVATION_MAIN_REVERTED ", StringComparison.Ordinal)));
        Assert.Contains("already reverted", repo.HeldEvent(repo.Merge), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RevertedRevertHoldsWithBothCommits(bool manual)
    {
        using var repo = new Repository();
        if (manual)
            repo.CommitFile("change.txt", "before", "Revert goal 91849616 landing");
        else
            repo.Git("revert", "--no-edit", "-m", "1", repo.Merge);
        var revert = repo.Tip;
        repo.Git("revert", "--no-edit", revert);
        var undo = repo.Tip;

        repo.RecordReverted(repo.Supervisor());

        Assert.Equal(undo, repo.Tip);
        var held = repo.HeldEvent(repo.Merge);
        Assert.Contains(revert, held, StringComparison.Ordinal);
        Assert.Contains(undo, held, StringComparison.Ordinal);
        Assert.Contains("operator review required", held, StringComparison.Ordinal);
    }

    [Fact]
    public void ReapplyingAnEarlierRevertRestoresItsEffect()
    {
        using var repo = new Repository();
        repo.Git("revert", "--no-edit", "-m", "1", repo.Merge);
        repo.Git("revert", "--no-edit", repo.Tip);
        repo.Git("revert", "--no-edit", repo.Tip);
        var tip = repo.Tip;

        repo.RecordReverted(repo.Supervisor());

        Assert.Equal(tip, repo.Tip);
        Assert.Contains("already reverted", repo.HeldEvent(repo.Merge), StringComparison.Ordinal);
    }

    [Fact]
    public void UnrelatedManualRevertDoesNotBlockCompensationOrLoseLaterChanges()
    {
        using var repo = new Repository();
        repo.CommitFile("later.txt", "keep this", "Revert goal 91849617 landing");
        var before = repo.Tip;

        repo.RecordReverted(repo.Supervisor());

        Assert.Equal(before, repo.Git("rev-parse", repo.Tip + "^"));
        Assert.Equal("keep this", repo.Git("show", repo.Tip + ":later.txt"));
        Assert.Equal("before", repo.Git("show", repo.Tip + ":change.txt"));
    }

    [Theory]
    [InlineData("missing-root")]
    [InlineData("non-repository")]
    [InlineData("missing-branch")]
    [InlineData("blank-commit")]
    [InlineData("none")]
    [InlineData("unknown-commit")]
    public void MissingTargetProducesNoCompensationEvent(string setup)
    {
        using var repo = new Repository();
        var workspace = setup switch
        {
            "missing-root" => repo.Workspace with { ExecutionDirectory = Path.Combine(repo.Sandbox, "missing") },
            "non-repository" => repo.Workspace with { ExecutionDirectory = repo.Sandbox },
            "missing-branch" => repo.Workspace with { IntegrationBranch = "missing" },
            _ => repo.Workspace
        };
        var failed = setup switch
        {
            "blank-commit" => " ",
            "none" => "none",
            "unknown-commit" => new string('0', repo.Merge.Length),
            _ => repo.Merge
        };
        repo.SetPushPolicy(true);
        var pushes = 0;

        repo.RecordReverted(repo.Supervisor(workspace: workspace, push: (_, _, _) => { pushes++; return true; }), failed);

        Assert.Equal(0, pushes);
        Assert.Equal(repo.Merge, repo.Tip);
        Assert.StartsWith("ACTIVATION_REVERTED ", Assert.Single(repo.Lines));
        Assert.Contains(repo.Events.Entries, evt => evt.Operation == "activation" && evt.Status == "reverted");
    }

    [Fact]
    public void CompetingBranchUpdateSurvivesPublication()
    {
        using var repo = new Repository();
        var competing = repo.Git("commit-tree", repo.Merge + "^{tree}", "-p", repo.Merge, "-m", "Competing branch update");
        repo.Git("checkout", "--detach", repo.Merge);
        var worktrees = repo.Worktrees;
        var checkout = repo.MakeCheckoutDirty();
        var refName = $"refs/heads/{Repository.Branch}";
        var publicationSeen = false;
        using var runner = LandingExecutor.UseGitRunner((directory, args) =>
        {
            if (args is ["update-ref", var reference, _, var expected] && reference == refName)
            {
                Assert.Equal(repo.Merge, expected);
                repo.Git("update-ref", refName, competing, repo.Merge);
                publicationSeen = true;
            }
            return GitCli.Run(directory, args);
        });

        repo.RecordReverted(repo.Supervisor());

        Assert.True(publicationSeen);
        Assert.Equal(competing, repo.Tip);
        Assert.Contains("prepared tip", repo.HeldEvent(repo.Merge), StringComparison.Ordinal);
        repo.AssertCheckoutPreserved(checkout, worktrees);
    }

    internal sealed class Repository : IDisposable
    {
        internal const string Branch = "integration-activation";
        internal string Sandbox { get; } = Directory.CreateTempSubdirectory("mcg-activation-revert-test-").FullName;
        internal string Root => Path.Combine(Sandbox, "repo");
        internal OrchestratorWorkspace Workspace { get; }
        internal string FirstParent { get; }
        internal string Merge { get; }
        internal string Tip => Git("rev-parse", $"refs/heads/{Branch}");
        internal string Worktrees => Git("worktree", "list", "--porcelain");
        internal List<string> Lines { get; } = [];
        internal RecordingEvents Events { get; } = new();

        internal Repository()
        {
            Directory.CreateDirectory(Root);
            Git("init", $"--initial-branch={Branch}", "--template=");
            Git("config", "user.name", "Activation Fixture");
            Git("config", "user.email", "activation@example.test");
            Git("config", "commit.gpgSign", "false");
            Git("config", "revert.reference", "false");
            Git("config", "core.hooksPath", Directory.CreateDirectory(Path.Combine(Sandbox, "hooks")).FullName);
            CommitFile("change.txt", "before", "Baseline");
            Git("checkout", "-b", "goal/91849616");
            CommitFile("change.txt", "after", "Goal change");
            Git("checkout", Branch);
            CommitFile("integration.txt", "integration", "Integration-only change");
            FirstParent = Tip;
            Git("merge", "--no-ff", "-m", "Integrate goal/91849616", "goal/91849616");
            Merge = Tip;
            Workspace = OrchestratorWorkspace.ForDirectory(Root, Root, OrchestratorWorkspace.DefaultTenantName,
                new OrchestratorProjectRegistry(Path.Combine(Sandbox, "registry"), Sandbox)) with
            {
                IntegrationBranch = Branch,
                OrchestratorDirectory = Path.Combine(Sandbox, "policy")
            };
        }

        internal void CommitFile(string filename, string contents, string message)
        {
            File.WriteAllText(Path.Combine(Root, filename), contents);
            Git("add", "--", filename);
            Git("commit", "-m", message);
        }

        internal string Git(params string[] args)
        {
            var result = GitCli.Run(Root, args);
            Assert.True(result.Succeeded && !result.DrainTimedOut, $"git {args[0]}: {result.Output} {result.Error}");
            return result.Output.Trim();
        }

        internal void SetPushPolicy(bool enabled)
        {
            Directory.CreateDirectory(Workspace.OrchestratorDirectory);
            File.WriteAllText(Path.Combine(Workspace.OrchestratorDirectory, "conductor-policy.json"),
                (ConductorAutonomyPolicy.Conservative with { AdoptedBuildPushEnabled = enabled }).ToJson());
        }

        internal ConductorContinuitySupervisor Supervisor(
            Func<string, string, string, ActivationFailedMainRevert.Result>? revert = null,
            Func<string, string, string, bool>? push = null,
            OrchestratorWorkspace? workspace = null) =>
            new(new UnusedHost(), Events, appendConductEvent: (_, _, line) => Lines.Add(line))
            {
                ActivationRevertWorkspaceFactory = () => workspace ?? Workspace,
                RevertFailedActivation = revert,
                PushActivationRevert = push
            };

        internal void RecordReverted(ConductorContinuitySupervisor supervisor, string? failed = null) =>
            supervisor.RecordActivation("reverted", 1, new(failed ?? Merge, "candidate", null, null),
                new(FirstParent, "prior", null, null), ConductorActivationRevertReason.ReadinessNeverReported, "fixture");

        internal string HeldEvent(string failed)
        {
            var line = Assert.Single(Lines, evt => evt.StartsWith("ACTIVATION_MAIN_REVERT_HELD ", StringComparison.Ordinal));
            Assert.StartsWith($"ACTIVATION_MAIN_REVERT_HELD failedCommit={failed} reason=", line);
            return line;
        }

        internal byte[] MakeCheckoutDirty()
        {
            File.WriteAllText(Path.Combine(Root, "staged.txt"), "staged");
            Git("add", "--", "staged.txt");
            File.WriteAllText(Path.Combine(Root, "staged.txt"), "unstaged");
            File.WriteAllText(Path.Combine(Root, "untracked.txt"), "untracked");
            return File.ReadAllBytes(Path.Combine(Root, ".git", "index"));
        }

        internal void AssertCheckoutPreserved(byte[] index, string worktrees)
        {
            Assert.Equal(index, File.ReadAllBytes(Path.Combine(Root, ".git", "index")));
            Assert.Equal("unstaged", File.ReadAllText(Path.Combine(Root, "staged.txt")));
            Assert.Equal("untracked", File.ReadAllText(Path.Combine(Root, "untracked.txt")));
            AssertRevertCleanedUp(worktrees);
        }

        internal void AssertRevertCleanedUp(string worktrees)
        {
            Assert.False(File.Exists(Path.Combine(Root, ".git", "REVERT_HEAD")));
            Assert.False(Directory.Exists(Path.Combine(Root, ".git", "sequencer")));
            Assert.Equal(worktrees.Split('\n').Where(line => line.StartsWith("worktree ", StringComparison.Ordinal)),
                Worktrees.Split('\n').Where(line => line.StartsWith("worktree ", StringComparison.Ordinal)));
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Sandbox);

        private sealed class UnusedHost : IConductorSupervisorProcessHost
        {
            public Task<ConductorSupervisorProcessResult> RunAsync(
                ConductorSupervisorProcessRequest request, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Recording an activation does not launch a process.");
        }
    }

    internal sealed class RecordingEvents : IRunEventStore
    {
        internal List<RunEventAppend> Entries { get; } = [];

        public Task<RunEventRecord> AppendAsync(RunEventAppend evt, CancellationToken cancellationToken = default)
        {
            Entries.Add(evt);
            return Task.FromResult(new RunEventRecord(Entries.Count, evt.EventId ?? Guid.NewGuid().ToString("N"),
                evt.OccurredAt ?? DateTimeOffset.MinValue, evt.EventType, evt.GoalId, evt.Operation,
                evt.Status, evt.Detail, evt.PayloadJson));
        }

        public Task<IReadOnlyList<RunEventRecord>> ReadSinceAsync(long afterSequence = 0,
            string? goalId = null, int maxCount = 500, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RunEventRecord>>([]);
    }
}
