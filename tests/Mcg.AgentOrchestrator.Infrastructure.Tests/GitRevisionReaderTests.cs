using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fact owns its repositories, reader cache and process handles. Observer
// census windows use the current test identity; no observer or environment is replaced.
public sealed class GitRevisionReaderTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("git-revision-reader-").FullName;
    private readonly GitRevisionReader reader = new(clock: new ManualClock(), admissionReads: 0);

    [Theory]
    [InlineData("HEAD")]
    [InlineData("topic")]
    [InlineData("refs/heads/topic")]
    [InlineData("topic^{commit}")]
    public void Resolve_CommonExpressions_MatchOneShot(string revision)
    {
        var repo = CreateRepository();
        Git(repo, "branch", "topic");
        AssertResolved(reader, repo, revision, Oracle(repo, revision));
        Assert.Null(reader.GetReader(repo));
    }

    [Fact]
    public void Resolve_TreeAndTag_ReturnUnpeeledObjectIds()
    {
        var repo = CreateRepository();
        Git(repo, "tag", "-a", "annotated", "-m", "tag");
        AssertResolved(reader, repo, "HEAD^{tree}", Oracle(repo, "HEAD^{tree}"));
        AssertResolved(reader, repo, "annotated", Oracle(repo, "annotated"));
        Assert.NotEqual(Oracle(repo, "HEAD"), Oracle(repo, "annotated"));
    }

    [Fact]
    public void Resolve_RefMovedExternally_UsesNewTarget()
    {
        var repo = CreateRepository();
        var first = Oracle(repo, "HEAD");
        Git(repo, "commit", "--allow-empty", "-m", "second");
        var second = Oracle(repo, "HEAD");
        Git(repo, "branch", "moving", first);
        AssertResolved(reader, repo, "moving", first);
        AssertResolved(reader, repo, "moving~0", first);
        var process = Assert.IsType<GitRevisionReader.ReaderProcess>(reader.GetReader(repo));
        Git(repo, "update-ref", "refs/heads/moving", second);
        AssertResolved(reader, repo, "moving", second);
        Assert.Same(process, reader.GetReader(repo));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Resolve_DeletedBranch_ConfirmsMissing()
    {
        var repo = CreateRepository();
        Git(repo, "branch", "deleted");
        AssertResolved(reader, repo, "deleted~0", Oracle(repo, "deleted"));
        Git(repo, "branch", "-D", "deleted");
        var launches = new LaunchRecording();
        Assert.False(reader.TryResolve(repo, "deleted", out var objectId));
        Assert.Empty(objectId);
        launches.AssertCommands("rev-parse");
    }

    [Fact]
    public void Resolve_RefsPackedBetweenQueries_SeesPackedTarget()
    {
        var repo = CreateRepository();
        Git(repo, "branch", "packed");
        var expected = Oracle(repo, "packed");
        AssertResolved(reader, repo, "packed", expected);
        Git(repo, "pack-refs", "--all");
        Assert.False(File.Exists(Path.Combine(repo, ".git", "refs", "heads", "packed")));
        AssertResolved(reader, repo, "packed", expected);

        Git(repo, "commit", "--allow-empty", "-m", "packed update");
        var updated = Oracle(repo, "HEAD");
        Git(repo, "update-ref", "refs/heads/packed", updated);
        Git(repo, "pack-refs", "--all");
        Assert.NotEqual(expected, updated);
        AssertResolved(reader, repo, "packed", updated);
    }

    [Fact]
    public void Resolve_PackedRefReplacedWithSameMetadata_SeesNewTarget()
    {
        var repo = CreateRepository();
        Git(repo, "branch", "packed");
        Git(repo, "pack-refs", "--all");
        var original = Oracle(repo, "packed");
        AssertResolved(reader, repo, "packed", original);
        Git(repo, "commit", "--allow-empty", "-m", "new target");
        var updated = Oracle(repo, "HEAD");
        Assert.NotEqual(original, updated);

        var packedRefs = Path.Combine(repo, ".git", "packed-refs");
        var contents = File.ReadAllText(packedRefs);
        var timestamp = File.GetLastWriteTimeUtc(packedRefs);
        var length = new FileInfo(packedRefs).Length;
        Assert.Contains(original + " refs/heads/packed", contents, StringComparison.Ordinal);
        var replacement = packedRefs + ".replacement";
        File.WriteAllText(replacement, contents.Replace(original + " refs/heads/packed",
            updated + " refs/heads/packed", StringComparison.Ordinal), new System.Text.UTF8Encoding(false));
        File.SetLastWriteTimeUtc(replacement, timestamp);
        File.Move(replacement, packedRefs, overwrite: true);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(packedRefs));
        Assert.Equal(length, new FileInfo(packedRefs).Length);
        Assert.False(File.Exists(Path.Combine(repo, ".git", "refs", "heads", "packed")));
        Assert.Equal(updated, Oracle(repo, "packed"));

        AssertResolved(reader, repo, "packed", updated);
        AssertResolved(reader, repo, "packed", updated); // Replacement reader also sees the target.
    }

    [Fact]
    public void Resolve_LinkedWorktree_SeesCommonPackedRefsChange()
    {
        var repo = CreateRepository();
        var original = Oracle(repo, "HEAD");
        Git(repo, "branch", "packed");
        Git(repo, "pack-refs", "--all");
        var linked = Path.Combine(root, "linked");
        Git(repo, "worktree", "add", "--detach", linked, original);
        AssertResolved(reader, linked, "packed", original);

        Git(repo, "commit", "--allow-empty", "-m", "new common target");
        var updated = Oracle(repo, "HEAD");
        Assert.NotEqual(original, updated);
        Git(repo, "update-ref", "refs/heads/packed", updated);
        Git(repo, "pack-refs", "--all");
        Assert.False(File.Exists(Path.Combine(repo, ".git", "refs", "heads", "packed")));

        AssertResolved(reader, linked, "packed", updated);
        AssertResolved(reader, linked, "HEAD", original);
    }

    [Fact]
    public void Resolve_ExternalCommit_IsVisibleOnNextQuery()
    {
        var repo = CreateRepository();
        var original = Oracle(repo, "HEAD");
        AssertResolved(reader, repo, "HEAD", original);
        Git(repo, "commit", "--allow-empty", "-m", "external commit");
        var expected = Oracle(repo, "HEAD");
        Assert.NotEqual(original, expected);
        AssertResolved(reader, repo, "HEAD", expected);
        AssertResolved(reader, repo, expected, expected);
        AssertResolved(reader, repo, expected[..12], expected);
    }

    [Fact]
    public void Resolve_TwoWorktrees_KeepDistinctHeadsAndAllowRemoval()
    {
        var repo = CreateRepository();
        var original = Oracle(repo, "HEAD~0");
        Git(repo, "commit", "--allow-empty", "-m", "main only");
        var main = Oracle(repo, "HEAD~0");
        var linked = Path.Combine(root, "linked");
        Git(repo, "worktree", "add", "--detach", linked, original);
        Assert.NotEqual(main, original);
        AssertResolved(reader, repo, "HEAD~0", main);
        AssertResolved(reader, linked, "HEAD~0", original);
        Assert.Equal(2, reader.RetainedCount);
        Git(repo, "worktree", "remove", "--force", linked);
        Assert.False(Directory.Exists(linked));
    }

    [Fact]
    public void Resolve_MissingRef_ConfirmsEveryAnswer()
    {
        var repo = CreateRepository();
        var launches = new LaunchRecording();
        for (var index = 0; index < 2; index++)
        {
            Assert.False(reader.TryResolve(repo, "refs/heads/missing", out var objectId));
            Assert.Empty(objectId);
        }
        launches.AssertCommands("cat-file");
        Assert.Equal(1, reader.RetainedCount);
    }

    [Fact]
    public void Resolve_TerminatedReader_FallsBackThenRestarts()
    {
        var repo = CreateRepository();
        var expected = Oracle(repo, "HEAD~0");
        AssertResolved(reader, repo, "HEAD~0", expected);
        var killed = Assert.IsType<GitRevisionReader.ReaderProcess>(reader.GetReader(repo));
        killed.KillProcess();
        Assert.True(killed.HasExited, "The owned reader process did not exit.");
        var launches = new LaunchRecording();
        AssertResolved(reader, repo, "HEAD~0", expected);
        Assert.Equal(0, reader.RetainedCount);
        AssertResolved(reader, repo, "HEAD~0", expected);
        Assert.NotSame(killed, reader.GetReader(repo));
        launches.AssertCommands("rev-parse", "cat-file");
    }

    [Fact]
    public void Resolve_200Queries_RecordsOneLaunch()
    {
        var repo = CreateRepository();
        var expected = Oracle(repo, "HEAD~0");
        var launches = new LaunchRecording();
        for (var index = 0; index < 200; index++) AssertResolved(reader, repo, "HEAD~0", expected);
        launches.AssertCommands("cat-file");
        Assert.Equal(1, reader.RetainedCount);
    }

    [Fact]
    public void Resolve_FullAndAbbreviatedIds_ConfirmsOnlyAbbreviations()
    {
        var repo = CreateRepository();
        var expected = Oracle(repo, "HEAD");
        var launches = new LaunchRecording();
        AssertResolved(reader, repo, expected, expected);
        AssertResolved(reader, repo, expected[..12], expected);
        launches.AssertCommands("cat-file", "rev-parse");
    }

    [Fact]
    public void Cache_17Directories_EvictsLeastRecentlyUsed()
    {
        var repos = Enumerable.Range(0, 17).Select(index => CreateRepository(index.ToString())).ToArray();
        for (var index = 0; index < 16; index++)
            Assert.True(reader.TryResolve(repos[index], "HEAD~0", out _));
        var first = Assert.IsType<GitRevisionReader.ReaderProcess>(reader.GetReader(repos[0]));
        var oldest = Assert.IsType<GitRevisionReader.ReaderProcess>(reader.GetReader(repos[1]));
        Assert.True(reader.TryResolve(repos[0], "HEAD~0", out _));
        Assert.True(reader.TryResolve(repos[16], "HEAD~0", out _));
        Assert.True(oldest.HasExited, "The least recently used reader process did not exit.");
        Assert.False(first.HasExited);
        Assert.Same(first, reader.GetReader(repos[0]));
        Assert.Null(reader.GetReader(repos[1]));
        Assert.Equal(16, reader.RetainedCount);
    }

    [Fact]
    public void Cache_FullPathAliases_ReuseReader()
    {
        var repo = CreateRepository();
        var expected = Oracle(repo, "HEAD~0");
        var launches = new LaunchRecording();
        AssertResolved(reader, repo, "HEAD~0", expected);
        AssertResolved(reader, Path.Combine(repo, "."), "HEAD~0", expected);
        AssertResolved(reader, repo + Path.DirectorySeparatorChar, "HEAD~0", expected);
        launches.AssertCommands("cat-file");
    }

    [Fact]
    public void Idle_Deadline_ClosesWithoutAnotherQuery()
    {
        var repo = CreateRepository();
        var clock = new ManualClock();
        using var timedReader = new GitRevisionReader(clock: clock, admissionReads: 0);
        Assert.True(timedReader.TryResolve(repo, "HEAD~0", out _));
        var process = Assert.IsType<GitRevisionReader.ReaderProcess>(timedReader.GetReader(repo));
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(process.HasExited);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(process.HasExited, "The idle timer did not close its owned reader.");
        Assert.Equal(0, timedReader.RetainedCount);
    }

    [Fact]
    public void Idle_QueryCompletion_RestartsDeadline()
    {
        var repo = CreateRepository();
        var clock = new ManualClock();
        using var timedReader = new GitRevisionReader(clock: clock, admissionReads: 0);
        Assert.True(timedReader.TryResolve(repo, "HEAD~0", out _));
        var process = Assert.IsType<GitRevisionReader.ReaderProcess>(timedReader.GetReader(repo));
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.True(timedReader.TryResolve(repo, "HEAD~0", out _));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(process.HasExited);
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.True(process.HasExited, "The timer did not expire 60 seconds after query completion.");
        Assert.Equal(0, timedReader.RetainedCount);
    }

    [Fact]
    public void Dispose_RetainedReaders_ClosesEveryProcess()
    {
        var first = CreateRepository("first");
        var second = CreateRepository("second");
        Assert.True(reader.TryResolve(first, "HEAD~0", out _));
        Assert.True(reader.TryResolve(second, "HEAD~0", out _));
        var processes = new[] { reader.GetReader(first)!, reader.GetReader(second)! };
        reader.Dispose();
        Assert.All(processes, process => Assert.True(process.HasExited));
        Assert.Equal(0, reader.RetainedCount);
    }

    [Fact]
    public async Task Resolve_ConcurrentQueries_SerializesProtocol()
    {
        var repo = CreateRepository();
        var first = Oracle(repo, "HEAD~0");
        Git(repo, "branch", "first");
        Git(repo, "commit", "--allow-empty", "-m", "second");
        var second = Oracle(repo, "HEAD~0");
        Assert.NotEqual(first, second);
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, 12).Select(index => Task.Factory.StartNew(() =>
        {
            Assert.True(start.Wait(TimeSpan.FromSeconds(30)), "Concurrent query start was not signaled.");
            var revision = index % 2 == 0 ? "first~0" : "HEAD~0";
            AssertResolved(reader, repo, revision, index % 2 == 0 ? first : second);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        start.Set();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, reader.RetainedCount);
    }

    [Theory]
    [InlineData("HEAD\nHEAD")]
    [InlineData(" HEAD")]
    [InlineData("HEAD:missing")]
    public void Resolve_UnsafeBatchInput_UsesOneShot(string revision)
    {
        var repo = CreateRepository();
        var expected = GitCli.Run(repo, "rev-parse", "--verify", revision);
        var launches = new LaunchRecording();
        var success = reader.TryResolve(repo, revision, out var objectId);
        Assert.Equal(expected.Succeeded && !string.IsNullOrWhiteSpace(expected.Output), success);
        Assert.Equal(success ? expected.Output.Trim() : string.Empty, objectId);
        launches.AssertCommands("rev-parse");
        Assert.Equal(0, reader.RetainedCount);
    }

    private string CreateRepository(string name = "repo")
    {
        var repo = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        Git(repo, "init", "--initial-branch=fixture-main");
        Git(repo, "commit", "--allow-empty", "-m", "seed");
        return repo;
    }

    private void Git(string repo, params string[] args)
    {
        var environment = new Dictionary<string, string>
        {
            ["GIT_AUTHOR_NAME"] = "Reader", ["GIT_AUTHOR_EMAIL"] = "reader@example.test",
            ["GIT_COMMITTER_NAME"] = "Reader", ["GIT_COMMITTER_EMAIL"] = "reader@example.test",
            ["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
            ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_COUNT"] = "2",
            ["GIT_CONFIG_KEY_0"] = "commit.gpgSign", ["GIT_CONFIG_VALUE_0"] = "false",
            ["GIT_CONFIG_KEY_1"] = "core.hooksPath", ["GIT_CONFIG_VALUE_1"] = Path.Combine(root, "no-hooks")
        };
        var result = GitCli.RunWithEnvironment(repo, environment, args);
        Assert.True(result.Succeeded, result.Error);
    }

    private static string Oracle(string repo, string revision)
    {
        var result = GitCli.Run(repo, "rev-parse", "--verify", revision);
        Assert.True(result.Succeeded, result.Error);
        Assert.False(string.IsNullOrWhiteSpace(result.Output));
        return result.Output.Trim();
    }

    private static void AssertResolved(GitRevisionReader reader, string repo, string revision, string expected)
    {
        Assert.True(reader.TryResolve(repo, revision, out var actual), $"Could not resolve {revision}.");
        Assert.Equal(expected, actual);
    }

    public void Dispose()
    {
        reader.Dispose();
        SharedTestSupport.RemoveTempDirectory(root);
    }

    private sealed class LaunchRecording
    {
        private readonly HostCapacitySlotGitCensus.Window window;

        internal LaunchRecording()
        {
            var identity = HostCapacitySlotTimingWriter.CurrentTestName();
            Assert.NotEqual("<unknown>", identity);
            HostCapacitySlotGitCensus.EnsureInstalled(nameof(GitRevisionReaderTests));
            window = Assert.IsType<HostCapacitySlotGitCensus.Window>(HostCapacitySlotGitCensus.Shared.Open(identity));
        }

        internal void AssertCommands(params string[] expected)
        {
            var delta = Assert.IsType<HostCapacitySlotGitCensus.GitLaunchDelta>(window.Close());
            Assert.Equal(expected.Length, delta.Launches);
            Assert.Equal(expected.GroupBy(command => command).ToDictionary(group => group.Key, group => group.Count())
                .OrderBy(pair => pair.Key, StringComparer.Ordinal), delta.Subcommands.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        private ManualTimer? timer;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        internal void Advance(TimeSpan duration)
        {
            ticks += duration.Ticks;
            timer!.FireIfDue();
        }

        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private long due = long.MaxValue;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.ticks + dueTime.Ticks;
                return true;
            }
            internal void FireIfDue()
            {
                if (due > clock.ticks) return;
                due = long.MaxValue;
                callback(state);
            }
            public void Dispose() => due = long.MaxValue;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
