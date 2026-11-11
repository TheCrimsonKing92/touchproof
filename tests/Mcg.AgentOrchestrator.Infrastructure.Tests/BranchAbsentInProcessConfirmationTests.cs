using System.Reflection;
using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

// Each test owns its repositories and census window; no process-wide environment is mutated.
public sealed class BranchAbsentInProcessConfirmationTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("branch-absent-").FullName;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingBranch_DeclinesForLooseAndPackedRefs(bool packed)
    {
        var repo = CreateRepository();
        AssertProof(repo, "probe", true, false);
        Git(repo, "branch", "probe");
        if (packed)
        {
            Git(repo, "pack-refs", "--all");
            Assert.False(File.Exists(Metadata(repo, "refs/heads/probe")));
            Assert.Contains(" refs/heads/probe\n", File.ReadAllText(Metadata(repo, "packed-refs")));
        }
        else Assert.True(File.Exists(Metadata(repo, "refs/heads/probe")));
        AssertProof(repo, "probe", false, true);
    }

    [Theory]
    [InlineData(false, "sha1")]
    [InlineData(true, "sha1")]
    [InlineData(false, "sha256")]
    [InlineData(true, "sha256")]
    public void AbsentBranch_ConfirmsWithZeroLaunches(bool packed, string format)
    {
        var repo = CreateRepository(format);
        if (packed) Git(repo, "pack-refs", "--all");
        Assert.Equal(packed, File.Exists(Metadata(repo, "packed-refs")));
        AssertProof(repo, "goal/abc", true, false);
    }

    [Theory]
    [InlineData("goal", "goal/abc", false)]
    [InlineData("goal/abc/def", "goal/abc", false)]
    [InlineData("goal/abc", "goal", false)]
    [InlineData("goal/abc", "goal", true)]
    public void RefPathConflicts_Decline(string existing, string query, bool packed)
    {
        var repo = CreateRepository();
        AssertProof(repo, query, true, false);
        Git(repo, "branch", existing);
        if (packed)
        {
            Git(repo, "pack-refs", "--all");
            Assert.False(File.Exists(Metadata(repo, "refs/heads/" + existing)));
            var prefix = Metadata(repo, "refs/heads/goal");
            if (Directory.Exists(prefix)) Directory.Delete(prefix);
            Assert.False(Directory.Exists(prefix));
        }
        AssertProof(repo, query, false, false);
    }

    [Theory]
    [InlineData("refs/refs/heads/probe", false)]
    [InlineData("refs/tags/refs/heads/probe", false)]
    [InlineData("refs/heads/refs/heads/probe", false)]
    [InlineData("refs/remotes/refs/heads/probe", false)]
    [InlineData("refs/remotes/refs/heads/probe/HEAD", false)]
    [InlineData("refs/refs/heads/probe", true)]
    [InlineData("refs/tags/refs/heads/probe", true)]
    [InlineData("refs/heads/refs/heads/probe", true)]
    [InlineData("refs/remotes/refs/heads/probe", true)]
    [InlineData("refs/remotes/refs/heads/probe/HEAD", true)]
    public void AlternativeLookupNames_Decline(string reference, bool packed)
    {
        var repo = CreateRepository();
        AssertProof(repo, "probe", true, false);
        Git(repo, "update-ref", reference, "HEAD");
        if (packed)
        {
            Git(repo, "pack-refs", "--all");
            Assert.False(File.Exists(Metadata(repo, reference)));
            if (reference.EndsWith("/HEAD", StringComparison.Ordinal))
            {
                var prefix = Metadata(repo, "refs/remotes/refs/heads/probe");
                if (Directory.Exists(prefix)) Directory.Delete(prefix);
                Assert.False(Directory.Exists(prefix));
            }
        }
        AssertProof(repo, "probe", false, true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinkedWorktree_ReadsCommonRefs(bool relative)
    {
        var repo = CreateRepository();
        var linked = Path.Combine(root, "linked");
        Git(repo, "worktree", "add", "-b", "linked", linked);
        var entry = Path.Combine(linked, ".git");
        Assert.True(File.Exists(entry));
        var pointer = File.ReadAllText(entry);
        Assert.StartsWith("gitdir: ", pointer);
        if (relative)
        {
            using var stream = File.Open(entry, FileMode.Open, FileAccess.Write);
            var bytes = Encoding.UTF8.GetBytes("gitdir: " + Path.GetRelativePath(linked, pointer[8..].TrimEnd('\r', '\n')) + "\n");
            stream.Write(bytes);
            stream.SetLength(stream.Position);
        }
        AssertProof(linked, "probe", true, false);
        Git(repo, "branch", "probe");
        AssertProof(linked, "probe", false, true);
        AssertProof(linked, "absent", true, false);
    }

    [Fact]
    public void ReftableDirectory_Declines()
    {
        var repo = CreateRepository();
        AssertProof(repo, "absent", true, false);
        Directory.CreateDirectory(Metadata(repo, "reftable"));
        AssertProof(repo, "absent", false, false);
    }

    [Theory]
    [InlineData("[extensions]\n refStorage = files\n")]
    [InlineData("[extensions]\n unknownkey = true\n")]
    [InlineData("[core]\n unknownkey = true\n")]
    public void UnsupportedConfig_Declines(string config)
    {
        var repo = CreateRepository();
        AssertProof(repo, "absent", true, false);
        File.AppendAllText(Metadata(repo, "config"), config);
        AssertProof(repo, "absent", false, false);
    }

    [Fact]
    public void UnreadablePackedRefs_Declines()
    {
        var repo = CreateRepository();
        Git(repo, "pack-refs", "--all");
        AssertProof(repo, "absent", true, false);
        var path = Metadata(repo, "packed-refs");
        using (var locked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => { using var attempt = File.OpenRead(path); });
            AssertProof(repo, "absent", false, false);
        }
        AssertProof(repo, "absent", true, false);
    }

    [Fact]
    public void OversizedPackedRefs_Declines()
    {
        var repo = CreateRepository();
        AssertProof(repo, "absent", true, false);
        var oid = Git(repo, "rev-parse", "HEAD").Output.Trim();
        var text = new StringBuilder("# pack-refs with: sorted\n");
        for (var i = 0; text.Length <= 1024 * 1024; i++)
            text.Append(oid).Append(" refs/tags/item-").Append(i.ToString("D8")).Append('\n');
        File.WriteAllText(Metadata(repo, "packed-refs"), text.ToString());
        Assert.True(new FileInfo(Metadata(repo, "packed-refs")).Length > 1024 * 1024);
        AssertProof(repo, "absent", false, false);
    }

    [Theory]
    [InlineData("missing-header")]
    [InlineData("invalid-oid")]
    [InlineData("invalid-name")]
    [InlineData("orphan-peeled")]
    [InlineData("invalid-peeled")]
    [InlineData("invalid-utf8")]
    public void MalformedPackedRefs_Decline(string fixture)
    {
        var repo = CreateRepository();
        AssertProof(repo, "absent", true, false);
        var oid = Git(repo, "rev-parse", "HEAD").Output.Trim();
        const string header = "# pack-refs with: peeled sorted\n";
        var text = fixture switch
        {
            "missing-header" => oid + " refs/tags/other\n",
            "invalid-oid" => header + "invalid refs/tags/other\n",
            "invalid-name" => header + oid + " refs/tags/invalid name\n",
            "orphan-peeled" => header + "^" + oid + "\n",
            "invalid-peeled" => header + oid + " refs/tags/other\n^invalid\n",
            "invalid-utf8" => header,
            _ => throw new ArgumentOutOfRangeException(nameof(fixture))
        };
        File.WriteAllText(Metadata(repo, "packed-refs"), text);
        if (fixture == "invalid-utf8")
        {
            using var stream = File.Open(Metadata(repo, "packed-refs"), FileMode.Append);
            stream.WriteByte(0xff);
        }
        AssertProof(repo, "absent", false, false);
    }

    [Fact]
    public void ValidPeeledPackedRef_StillConfirmsAbsence()
    {
        var repo = CreateRepository();
        Git(repo, "tag", "-a", "other", "-m", "annotated");
        Git(repo, "pack-refs", "--all");
        Assert.Contains("\n^", File.ReadAllText(Metadata(repo, "packed-refs")));
        AssertProof(repo, "absent", true, false);
    }

    [Fact]
    public void GitDirOverride_DeclinesWithoutMutatingEnvironment()
    {
        var repo = CreateRepository();
        AssertProof(repo, "absent", true, false);
        Assert.False(GitRevisionReader.TryConfirmBranchAbsent(repo, "absent",
            key => key == "GIT_DIR" ? Metadata(repo, "") : null));
        Assert.False(RunGit(repo, "rev-parse", "--verify", "--quiet", "refs/heads/absent").Succeeded);
    }

    [Theory]
    [InlineData("sha1")]
    [InlineData("sha256")]
    public void DescribeName_DeclinesEvenWithoutRef(string format)
    {
        var repo = CreateRepository(format);
        AssertProof(repo, "probe", true, false);
        var abbreviation = Git(repo, "rev-parse", "--short", "HEAD").Output.Trim();
        var branch = "probe-1-g" + abbreviation;
        Assert.False(File.Exists(Metadata(repo, "refs/heads/" + branch)));
        AssertProof(repo, branch, false, true);
    }

    [Theory]
    [InlineData("probe-g1", false)]
    [InlineData("probe-1-ga", false)]
    [InlineData("probe-1-gA", false)]
    [InlineData("probe-1-g", true)]
    [InlineData("probe-1-g12z", true)]
    public void DescribeSuffix_RequiresTrailingHex(string branch, bool expectedProof)
    {
        var repo = CreateRepository();
        AssertProof(repo, branch, expectedProof, false);
    }

    [Theory]
    [InlineData("a..b")]
    [InlineData("a.lock")]
    [InlineData("a//b")]
    public void InvalidRefName_Declines(string branch)
    {
        var repo = CreateRepository();
        AssertProof(repo, branch, false, false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WindowsCaseDifference_Declines(bool packed)
    {
        if (!OperatingSystem.IsWindows()) return;
        var repo = CreateRepository();
        Git(repo, "branch", "Probe/Child");
        if (packed)
        {
            Git(repo, "pack-refs", "--all");
            Assert.False(File.Exists(Metadata(repo, "refs/heads/Probe/Child")));
            var prefix = Metadata(repo, "refs/heads/Probe");
            if (Directory.Exists(prefix)) Directory.Delete(prefix);
        }
        // Packed names are case sensitive to git; the proof deliberately declines either way.
        AssertProof(repo, "probe/child", false, !packed);
        AssertProof(repo, "probe", false, false);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("com1/child")]
    [InlineData("trailing./child")]
    public void WindowsPathAliases_Decline(string branch)
    {
        if (!OperatingSystem.IsWindows()) return;
        var repo = CreateRepository();
        AssertProof(repo, branch, false, false);
    }

    [Fact]
    public void NestedDirectory_RequiresFallback()
    {
        var repo = CreateRepository();
        var nested = Directory.CreateDirectory(Path.Combine(repo, "nested")).FullName;
        Assert.True(GitRevisionReader.TryConfirmWorkTree(nested));
        AssertProof(nested, "absent", false, false);
    }

    [Fact]
    public void BranchExists_AbsentBranchSkipsReaderAndGit()
    {
        var repo = CreateRepository();
        AssertProof(repo, "absent", true, false);
        var launches = new LaunchRecording();
        Assert.False(GoalWorktrees.BranchExists(repo, "absent", GitCli.DefaultTimeoutMilliseconds));
        launches.AssertNoLaunches();
    }

    private static string Metadata(string repo, string name) => Path.Combine(repo, ".git", name);

    private void AssertProof(string directory, string branch, bool expectedProof, bool expectedGitPresence)
    {
        var oracle = RunGit(directory, "rev-parse", "--verify", "--quiet", "refs/heads/" + branch);
        Assert.Equal(expectedGitPresence, oracle.Succeeded);
        Assert.True(oracle.ProcessStarted);
        var launches = new LaunchRecording();
        Assert.Equal(expectedProof, GitRevisionReader.TryConfirmBranchAbsent(directory, branch));
        launches.AssertNoLaunches();
    }

    private string CreateRepository(string format = "sha1")
    {
        var repo = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;
        Git(repo, "init", "--initial-branch=fixture-main", "--object-format=" + format);
        Git(repo, "commit", "--allow-empty", "-m", "seed");
        return repo;
    }

    private GitCli.GitResult Git(string directory, params string[] args)
    {
        var result = RunGit(directory, args);
        Assert.True(result.Succeeded, result.Error);
        return result;
    }

    private GitCli.GitResult RunGit(string directory, params string[] args) => GitCli.RunWithEnvironment(directory,
        new Dictionary<string, string>
        {
            ["GIT_AUTHOR_NAME"] = "Confirmation", ["GIT_AUTHOR_EMAIL"] = "confirmation@example.test",
            ["GIT_COMMITTER_NAME"] = "Confirmation", ["GIT_COMMITTER_EMAIL"] = "confirmation@example.test",
            ["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
            ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_COUNT"] = "2",
            ["GIT_CONFIG_KEY_0"] = "commit.gpgSign", ["GIT_CONFIG_VALUE_0"] = "false",
            ["GIT_CONFIG_KEY_1"] = "core.hooksPath", ["GIT_CONFIG_VALUE_1"] = Path.Combine(root, "no-hooks")
        }, args);

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(root);

    private sealed class LaunchRecording
    {
        private readonly HostCapacitySlotGitCensus.Window window;

        internal LaunchRecording()
        {
            var identity = HostCapacitySlotTimingWriter.CurrentTestName();
            Assert.NotEqual("<unknown>", identity);
            HostCapacitySlotGitCensus.EnsureInstalled(nameof(BranchAbsentInProcessConfirmationTests));
            window = Assert.IsType<HostCapacitySlotGitCensus.Window>(HostCapacitySlotGitCensus.Shared.Open(identity));
        }

        internal void AssertNoLaunches()
        {
            var delta = Assert.IsType<HostCapacitySlotGitCensus.GitLaunchDelta>(window.Close());
            Assert.Equal(0, delta.Launches);
            Assert.Empty(delta.Subcommands);
        }
    }
}
