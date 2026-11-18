namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

// Real Git fixture repositories and OS file-sharing behavior require the host-bound lane.
[Xunit.Collection("ProcessSpawningHostBound")]
public sealed class DisposableTrialRootProtectedPathTests
{
    [Xunit.Fact]
    public void DefaultProtectionIgnoresRuntimeChurnDuringCaptureAndTeardown()
    {
        using var fixture = new RepositoryFixture();
        var runtime = Path.Combine(fixture.Source, "runtime");
        Directory.CreateDirectory(runtime);
        var sidecar = Path.Combine(runtime, "state.db-shm");
        var changed = Path.Combine(runtime, "state.db");
        var created = Path.Combine(runtime, "created-during-capture");
        File.WriteAllText(sidecar, "temporary");
        File.WriteAllText(changed, "before");
        using var ignoredLock = new FileStream(Path.Combine(runtime, "sandbox.lock"),
            FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var untrackedLock = new FileStream(Path.Combine(fixture.Source, "untracked.lock"),
            FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        // Protect current work-tree contents, including local modifications, rather than blobs.
        File.WriteAllText(fixture.Tracked, "local modification before capture");
        var hookObserved = false;
        var factory = CreateFactory(path =>
        {
            if (path == fixture.Tracked)
            {
                hookObserved = true;
                File.Delete(sidecar);
                File.WriteAllText(created, "created");
            }
        });

        using var lease = factory.Create(fixture.Request());
        Xunit.Assert.True(hookObserved);
        Xunit.Assert.False(File.Exists(sidecar));
        Xunit.Assert.True(File.Exists(created));
        File.Delete(created);
        File.WriteAllText(changed, "after");
        File.WriteAllText(Path.Combine(runtime, "another-sidecar"), "new");
        File.WriteAllText(Path.Combine(fixture.Source, "newly-tracked.txt"), "new");
        RunGit(fixture.Source, "add", "newly-tracked.txt");

        var report = lease.Destroy();

        Xunit.Assert.Empty(report.OutsideWrites);
        Xunit.Assert.True(report.Clean, string.Join(Environment.NewLine, report.Diagnostics));
    }

    [Xunit.Fact]
    public void DefaultProtectionReportsTrackedMutation()
    {
        using var fixture = new RepositoryFixture();
        using var lease = CreateFactory().Create(fixture.Request());
        File.WriteAllText(fixture.Tracked, "changed after creation");

        var report = lease.Destroy();

        Xunit.Assert.Equal([fixture.Tracked], report.OutsideWrites);
        Xunit.Assert.False(report.Clean);
    }

    [Xunit.Fact]
    public void InitiallyAbsentTrackedFileIsProtectedWithMissingMarker()
    {
        using var fixture = new RepositoryFixture();
        File.Delete(fixture.Tracked);
        var snapshot = DisposableTrialRoot.CaptureProtectedPath(fixture.Tracked, ProtectedPathCaptureMode.TrackedPath);
        Xunit.Assert.Equal(new ProtectedFileFingerprint(-2, "<missing>"), snapshot.Files[fixture.Tracked]);
        using var lease = CreateFactory().Create(fixture.Request());
        File.WriteAllText(fixture.Tracked, "restored after capture");

        var report = lease.Destroy();

        Xunit.Assert.Equal([fixture.Tracked], report.OutsideWrites);
        Xunit.Assert.False(report.Clean);
    }

    [Xunit.Fact]
    public void TrackedDirectoryEntryNeverRecursesDuringCaptureOrTeardown()
    {
        using var fixture = new RepositoryFixture();
        var nested = Path.Combine(fixture.Source, "nested");
        Directory.CreateDirectory(nested);
        RunGit(fixture.Source, "update-index", "--add", "--cacheinfo", $"160000,{fixture.Commit},nested");
        var snapshot = DisposableTrialRoot.CaptureProtectedPath(nested, ProtectedPathCaptureMode.TrackedPath);
        Xunit.Assert.Equal(new ProtectedFileFingerprint(-1, "<directory>"), Xunit.Assert.Single(snapshot.Files).Value);
        using var nestedLock = new FileStream(Path.Combine(nested, "runtime.lock"),
            FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var lease = CreateFactory().Create(fixture.Request());
        File.WriteAllText(Path.Combine(nested, "new-runtime-content"), "unrelated");

        var report = lease.Destroy();

        Xunit.Assert.Empty(report.OutsideWrites);
        Xunit.Assert.True(report.Clean, string.Join(Environment.NewLine, report.Diagnostics));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(true, true)]
    public void ExplicitCaptureRefusesObservedAccessFailureBeforeAnySetupProcess(bool recursive, bool unreadable)
    {
        using var fixture = new RepositoryFixture();
        var protectedDirectory = Path.Combine(fixture.Root, "protected");
        Directory.CreateDirectory(protectedDirectory);
        var protectedFile = Path.Combine(protectedDirectory, "protected.txt");
        File.WriteAllText(protectedFile, "protected");
        Directory.CreateDirectory(fixture.Trials);
        FileStream? locked = null;
        var hookObserved = false;
        var factory = CreateFactory(path =>
        {
            Xunit.Assert.Equal(protectedFile, path);
            hookObserved = true;
            if (unreadable)
            {
                locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            else
            {
                File.Delete(path);
            }
        });
        var setupLaunches = 0;
        var previousObserver = GitCli.StartInfoObserver;
        GitCli.StartInfoObserver = _ => setupLaunches++;
        try
        {
            // Prove the launch observation works before measuring the Create call.
            RunGit(fixture.Source, "status", "--porcelain");
            Xunit.Assert.True(setupLaunches > 0);
            setupLaunches = 0;
            var request = fixture.Request() with
            {
                BaseCommit = "definitely-not-a-revision",
                ProtectedPaths = [recursive ? protectedDirectory : protectedFile]
            };

            var error = Xunit.Assert.Throws<TrialProtectedPathException>(() => factory.Create(request));

            Xunit.Assert.True(hookObserved);
            Xunit.Assert.Contains(protectedFile, error.Message, StringComparison.Ordinal);
            Xunit.Assert.Contains(unreadable ? "unreadable" : "vanished", error.Message, StringComparison.Ordinal);
            Xunit.Assert.Contains("clean exact-commit checkout", error.Message, StringComparison.Ordinal);
            Xunit.Assert.Contains("stop the writer", error.Message, StringComparison.Ordinal);
            Xunit.Assert.IsAssignableFrom<IOException>(error.InnerException);
            if (!unreadable)
            {
                Xunit.Assert.IsType<FileNotFoundException>(error.InnerException);
            }

            Xunit.Assert.Equal(0, setupLaunches);
            Xunit.Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Trials));
        }
        finally
        {
            GitCli.StartInfoObserver = previousObserver;
            locked?.Dispose();
        }
    }

    [Xunit.Fact]
    public void InitiallyMissingExplicitPathIsRecordedAndLaterCreationIsReported()
    {
        using var fixture = new RepositoryFixture();
        var missing = Path.Combine(fixture.Root, "missing");
        var snapshot = DisposableTrialRoot.CaptureProtectedPath(missing);
        Xunit.Assert.Equal(new ProtectedFileFingerprint(-2, "<missing>"), snapshot.Files[missing]);
        using var lease = CreateFactory().Create(fixture.Request() with { ProtectedPaths = [missing] });
        File.WriteAllText(missing, "created after capture");

        var report = lease.Destroy();

        Xunit.Assert.Equal([missing], report.OutsideWrites);
        Xunit.Assert.False(report.Clean);
    }

    [Xunit.Fact]
    public void ExplicitDirectoryProtectionStillReportsRecursiveChanges()
    {
        using var fixture = new RepositoryFixture();
        var directory = Path.Combine(fixture.Root, "protected");
        Directory.CreateDirectory(directory);
        var nested = Path.Combine(directory, "nested.txt");
        File.WriteAllText(nested, "before");
        using var lease = CreateFactory().Create(fixture.Request() with { ProtectedPaths = [directory] });
        File.WriteAllText(nested, "after");

        var report = lease.Destroy();

        Xunit.Assert.Equal([nested], report.OutsideWrites);
        Xunit.Assert.False(report.Clean);
    }

    [Xunit.Fact]
    public void EmptyExplicitProtectionProtectsNothing()
    {
        using var fixture = new RepositoryFixture();
        using var lease = CreateFactory().Create(fixture.Request() with { ProtectedPaths = [] });
        File.WriteAllText(fixture.Tracked, "outside the explicit empty set");

        var report = lease.Destroy();

        Xunit.Assert.Empty(report.OutsideWrites);
        Xunit.Assert.True(report.Clean, string.Join(Environment.NewLine, report.Diagnostics));
    }

    private static DisposableTrialRoot CreateFactory(Action<string>? beforeProtectedFingerprint = null) =>
        new(new NoOpIntegrityLabeler(), new SystemTrialProcessInventory(),
            useContainedJob: false, useLowIntegrityProcess: false,
            beforeProtectedFingerprint: beforeProtectedFingerprint);

    private static string RunGit(string directory, params string[] arguments)
    {
        var result = GitCli.Run(directory, arguments);
        Xunit.Assert.True(result.Succeeded && !result.DrainTimedOut, result.Error);
        return result.Output;
    }

    private sealed class NoOpIntegrityLabeler : IWorkerIntegrityLabeler
    {
        public IntegrityLabelState Query(string path) => new(true, false, false);

        public bool SetIntegrity(string path, string level, bool recursive) => true;
    }

    private sealed class RepositoryFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "mcg-trial-protected-tests", Guid.NewGuid().ToString("N"));

        public string Source { get; }

        public string Trials { get; }

        public string Tracked { get; }

        public string Commit { get; }

        public RepositoryFixture()
        {
            Source = Path.Combine(Root, "source");
            Trials = Path.Combine(Root, "trials");
            Tracked = Path.Combine(Source, "tracked.txt");
            Directory.CreateDirectory(Source);
            RunGit(Source, "init", "--initial-branch=main");
            RunGit(Source, "config", "user.name", "Trial Protection Test");
            RunGit(Source, "config", "user.email", "trial-protection@example.invalid");
            File.WriteAllText(Tracked, "baseline");
            File.WriteAllText(Path.Combine(Source, ".gitignore"), "runtime/\n");
            File.WriteAllText(Path.Combine(Source, "tracked with spaces.txt"), "another tracked path");
            RunGit(Source, "add", ".gitignore", "tracked.txt", "tracked with spaces.txt");
            RunGit(Source, "commit", "-m", "fixture");
            Commit = RunGit(Source, "rev-parse", "HEAD").Trim();
        }

        public TrialRootRequest Request() => new(Source, Commit, Trials, "protected-paths");

        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Root, recursive: true);
        }
    }
}
