using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalWorktreeRecoveryRefusalTests : GoalWorktreeRecoveryTestBase
{
    [Xunit.Theory]
    [Xunit.InlineData("empty")]
    [Xunit.InlineData("partial")]
    [Xunit.InlineData("file")]
    public void ExistingPathIsPreserved(string kind)
    {
        using var f = new MissingFixture();
        if (kind == "file") File.WriteAllText(f.Path, "do not delete");
        else Directory.CreateDirectory(f.Path);
        var sentinel = kind == "file" ? f.Path : Path.Combine(f.Path, "dirty.txt");
        if (kind == "partial") File.WriteAllText(sentinel, "do not delete");
        var before = GoalWorktreeRegistrationListing.RunGit(f.Repo).Output;
        var result = f.Recover();
        Assert.False(result.Succeeded);
        Assert.Contains("incomplete", result.Observation);
        if (kind != "empty") Assert.Equal("do not delete", File.ReadAllText(sentinel));
        else Assert.Empty(Directory.EnumerateFileSystemEntries(f.Path));
        Assert.Equal(before, GoalWorktreeRegistrationListing.RunGit(f.Repo).Output);
        f.AssertTip();
    }

    [Xunit.Theory]
    [Xunit.InlineData("failed")]
    [Xunit.InlineData("not-started")]
    [Xunit.InlineData("drain-timeout")]
    [Xunit.InlineData("truncated")]
    [Xunit.InlineData("malformed")]
    [Xunit.InlineData("duplicate")]
    [Xunit.InlineData("foreign")]
    public void UncertainListingNeverMutates(string mode)
    {
        using var f = new MissingFixture();
        var before = GoalWorktreeRegistrationListing.RunGit(f.Repo).Output;
        var output = mode switch
        {
            "truncated" => before.TrimEnd('\0'),
            "malformed" => "worktree relative\0\0",
            "duplicate" => before + before,
            "foreign" => before.Replace($"refs/heads/{f.Branch}", "refs/heads/foreign"),
            _ => before
        };
        var hooks = new GoalWorktreeCleanupHooks
        {
            RunWorktreeList = _ => new GitCli.GitResult(mode == "failed" ? 1 : 0, output, "injected listing")
            { ProcessStarted = mode != "not-started", DrainTimedOut = mode == "drain-timeout" }
        };
        Assert.False(f.Recover(hooks: hooks).Succeeded);
        Assert.False(Directory.Exists(f.Path));
        Assert.Equal(before, GoalWorktreeRegistrationListing.RunGit(f.Repo).Output);
        f.AssertTip();
    }

    [Xunit.Fact]
    public void LockedStaleRegistrationIsPreserved()
    {
        using var f = new MissingFixture();
        RunGit(f.Repo, "worktree", "lock", f.Path);
        var before = GoalWorktreeRegistrationListing.RunGit(f.Repo).Output;
        var result = f.Recover();
        Assert.False(result.Succeeded);
        Assert.Contains("locked", result.Observation);
        Assert.Equal(before, GoalWorktreeRegistrationListing.RunGit(f.Repo).Output);
        f.AssertTip();
    }

    [Xunit.Fact]
    public void ExpectedBranchAtAnotherPathIsPreserved()
    {
        using var f = new MissingFixture(stale: false);
        var foreign = Path.Combine(f.Repo, "foreign-worktree");
        RunGit(f.Repo, "worktree", "add", foreign, f.Branch);
        var before = GoalWorktreeRegistrationListing.RunGit(f.Repo).Output;
        var result = f.Recover();
        Assert.False(result.Succeeded);
        Assert.Contains("another path", result.Observation);
        Assert.Equal(before, GoalWorktreeRegistrationListing.RunGit(f.Repo).Output);
        Assert.True(File.Exists(Path.Combine(foreign, "unique-goal.txt")));
        f.AssertTip();
    }

    [Xunit.Theory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, true)]
    public void RedirectedTargetOrAncestorIsRefused(bool ancestor, bool dangling)
    {
        using var f = new MissingFixture(stale: false);
        var destination = Path.Combine(f.Repo, "redirect-destination");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "sentinel"), "preserve");
        var redirect = ancestor ? Path.GetDirectoryName(f.Path)! : f.Path;
        if (ancestor) Directory.Delete(redirect);
        if (OperatingSystem.IsWindows())
        {
            // Junctions do not require the symbolic-link privilege on Windows.
            var result = TestChildProcessCapture.Run(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe", WorkingDirectory = f.Repo, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "/d", "/c", "mklink", "/J", redirect, destination }
            });
            Assert.Equal(0, result.ExitCode);
        }
        else Directory.CreateSymbolicLink(redirect, destination);
        if (dangling) DeleteDirectory(destination);
        try
        {
            var result = f.Recover();
            Assert.False(result.Succeeded);
            Assert.Contains("redirected", result.Observation);
            if (dangling) Assert.NotNull(new DirectoryInfo(redirect).LinkTarget);
            else Assert.Equal("preserve", File.ReadAllText(Path.Combine(destination, "sentinel")));
            f.AssertTip();
        }
        finally { Directory.Delete(redirect); }
    }

    [Xunit.Fact]
    public void ReceiptFailureWithholdsSuccessAndPreservesCreatedState()
    {
        using var f = new MissingFixture();
        var receipts = new List<GoalWorktreeRecoveryResult>();
        var result = GoalWorktrees.RecoverMissing(f.Repo, f.Goal.Id, () => null, receipt =>
        {
            if (receipt.Succeeded) throw new IOException("receipt unavailable");
            receipts.Add(receipt);
        });
        Assert.False(result.Succeeded);
        Assert.Contains("receipt unavailable", result.Observation);
        Assert.False(Assert.Single(receipts).Succeeded);
        f.AssertRestored();
    }

    [Xunit.Fact]
    public void CancellationAfterAddPreservesStateAndRecordsInterruption()
    {
        using var f = new MissingFixture();
        Assert.Throws<OperationCanceledException>(() => GoalWorktrees.RecoverMissing(f.Repo, f.Goal.Id, () => null, receipt =>
        {
            if (receipt.Succeeded) throw new OperationCanceledException("fixture interruption");
            f.Receipts.Add(receipt);
        }));
        Assert.Contains("interrupted", Assert.Single(f.Receipts).Observation);
        f.AssertRestored();
    }

    [Xunit.Fact]
    public void MissingBranchIsNeverRecreated()
    {
        using var f = new MissingFixture(stale: false);
        RunGit(f.Repo, "branch", "-D", f.Branch);
        Assert.False(f.Recover().Succeeded);
        Assert.False(GoalWorktrees.HasBranch(f.Repo, f.Goal.Id));
        Assert.False(Directory.Exists(f.Path));
    }

    [Xunit.Fact]
    public void ExistingLifecycleCannotCreateReplacementBranch()
    {
        using var f = new MissingFixture(stale: false);
        RunGit(f.Repo, "branch", "-D", f.Branch);
        Assert.Throws<InvalidOperationException>(() => GoalWorktrees.PrepareForDispatch(f.Repo, f.Goal.Id,
            allowInitialCreation: false, () => null, f.Receipts.Add));
        Assert.False(GoalWorktrees.HasBranch(f.Repo, f.Goal.Id));
        Assert.False(Directory.Exists(f.Path));
        Assert.False(Assert.Single(f.Receipts).Succeeded);
    }

    [Xunit.Fact]
    public void BranchMovementIsRefusedWithoutResettingIt()
    {
        using var f = new MissingFixture();
        var newTip = RunGitOutput(f.Repo, "rev-parse", "HEAD").Trim();
        var reads = 0;
        var result = f.Recover(() =>
        {
            if (++reads == 2) RunGit(f.Repo, "update-ref", $"refs/heads/{f.Branch}", newTip);
            return null;
        });
        Assert.False(result.Succeeded);
        Assert.Contains("branch tip changed", result.Observation);
        Assert.Equal(newTip, RunGitOutput(f.Repo, "rev-parse", f.Branch).Trim());
        Assert.False(Directory.Exists(f.Path));
    }

    [Xunit.Fact]
    public void FailedAddPreservesPartialStateAndNextAttemptRefusesIt()
    {
        using var f = new MissingFixture();
        var reads = 0;
        var result = f.Recover(() =>
        {
            if (++reads == 2)
            {
                Directory.CreateDirectory(f.Path);
                File.WriteAllText(Path.Combine(f.Path, "interrupted.txt"), "preserve partial work");
            }
            return null;
        });
        Assert.False(result.Succeeded);
        Assert.False(f.Recover().Succeeded);
        Assert.Equal("preserve partial work", File.ReadAllText(Path.Combine(f.Path, "interrupted.txt")));
        f.AssertTip();
    }
}
