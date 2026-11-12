using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixture-owned repositories; the exception observer counts only its calling thread.
public sealed class GitLookupDirectoryProbeTests
{
    [Fact]
    public void SharedProbe_LinkedWorktreeLookup_ReadsEachDirectoryOnce()
    {
        using var fixture = new DeepFixture();
        var probe = new GitLookupDirectoryProbe();
        Assert.True(GitRevisionReader.TryLocateRefStore(fixture.Linked, _ => null, probe,
            out var gitDirectory, out var commonDirectory));
        Assert.True(GitRevisionReader.TryLocateRefStore(fixture.Linked, _ => null,
            out var expectedGitDirectory, out var expectedCommonDirectory));
        Assert.Equal(expectedGitDirectory, gitDirectory);
        Assert.Equal(expectedCommonDirectory, commonDirectory);
        Assert.Equal(GitWorkspaceLaunchFixture.AdminDirectory(fixture.Linked), gitDirectory);
        Assert.Equal(Path.Combine(fixture.Repo, ".git"), commonDirectory);

        var distinct = Ancestors(fixture.Linked).Concat(Ancestors(gitDirectory))
            .Concat(Ancestors(commonDirectory)).Distinct(StringComparer.Ordinal).Count();
        Assert.InRange(probe.Reads, distinct, distinct + 12);
        var reads = probe.Reads;
        // Every endpoint and ancestor is now known, so these checks must do zero more reads.
        foreach (var path in new[] { fixture.Linked, gitDirectory, commonDirectory, fixture.FirstLevel })
            Assert.True(probe.HasOrdinaryDirectoryPath(path));
        Assert.Equal(reads, probe.Reads);
    }

    [Fact]
    public void FreshProbePerWalk_FiveWalks_ReadAtLeastFourTimesSharedProbe()
    {
        using var fixture = new DeepFixture();
        var shared = new GitLookupDirectoryProbe();
        Assert.True(GitRevisionReader.TryLocateRefStore(fixture.Linked, _ => null, shared,
            out var gitDirectory, out var commonDirectory));
        Assert.True(shared.Reads > 0);
        var freshReads = 0;
        foreach (var path in new[] { fixture.Linked, gitDirectory, commonDirectory, gitDirectory, commonDirectory })
        {
            var fresh = new GitLookupDirectoryProbe();
            Assert.True(fresh.HasOrdinaryDirectoryPath(path));
            Assert.Equal(Ancestors(path).Count(), fresh.Reads);
            freshReads += fresh.Reads;
        }
        Assert.True(freshReads >= 4 * shared.Reads, $"Fresh reads: {freshReads}; shared reads: {shared.Reads}");
    }

    [Fact]
    public void SecondLookup_AncestorReplacedByJunction_Declines()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Requires a Windows directory junction.");
        using var fixture = new DeepFixture();
        Assert.True(GitRevisionReader.TryLocateRefStore(fixture.Linked, _ => null, out _, out _));
        Directory.Move(fixture.FirstLevel, Path.Combine(fixture.Root, "moved"));
        var junctionCreated = false;
        try
        {
            var result = TestChildProcessCapture.Run(new ProcessStartInfo
            {
                FileName = "cmd.exe", WorkingDirectory = fixture.Root,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "/d", "/c", "mklink", "/J", fixture.FirstLevel, Path.Combine(fixture.Root, "moved") }
            });
            junctionCreated = result.ExitCode == 0;
            Assert.True(junctionCreated, result.Stdout + result.Stderr);
            Assert.True((File.GetAttributes(fixture.FirstLevel) & FileAttributes.ReparsePoint) != 0);
            Assert.True(Directory.Exists(fixture.Linked));
            // Git still sees the same valid repository through the junction: rejection must
            // come from revalidating the redirected ancestor, not from broken fixture metadata.
            Assert.Equal("true", GitWorkspaceLaunchFixture.Git(fixture.Linked, "rev-parse", "--is-inside-work-tree"));
            Assert.False(GitRevisionReader.TryLocateRefStore(fixture.Linked, _ => null, out _, out _));
        }
        finally
        {
            if (junctionCreated) Directory.Delete(fixture.FirstLevel);
        }
    }

    [Fact]
    public void MetadataEntryExists_MissingFileOrParent_ReturnsFalseWithoutThrowing()
    {
        var root = Directory.CreateTempSubdirectory("git-entry-").FullName;
        try
        {
            var missingFile = Path.Combine(root, "missing");
            var missingParent = Path.Combine(root, "absent", "entry");
            var existing = Path.Combine(root, "existing");
            File.WriteAllText(existing, "metadata");
            var threadId = Environment.CurrentManagedThreadId;
            var exceptions = 0;
            void CountException(object? sender, FirstChanceExceptionEventArgs args)
            {
                if (Environment.CurrentManagedThreadId == threadId) exceptions++;
            }
            bool fileExists, parentExists, existingExists, directoryExists;
            int absentExceptions, controlExceptions;
            AppDomain.CurrentDomain.FirstChanceException += CountException;
            try
            {
                fileExists = GitRevisionReader.WorkTreeMetadataEntryExists(missingFile);
                parentExists = GitRevisionReader.WorkTreeMetadataEntryExists(missingParent);
                existingExists = GitRevisionReader.WorkTreeMetadataEntryExists(existing);
                directoryExists = GitRevisionReader.WorkTreeMetadataEntryExists(root);
                absentExceptions = exceptions;
                // Positive control: prove this thread's first-chance observer is active.
                try { throw new InvalidOperationException("first-chance control"); }
                catch (InvalidOperationException) { }
                controlExceptions = exceptions - absentExceptions;
            }
            finally { AppDomain.CurrentDomain.FirstChanceException -= CountException; }
            Assert.False(fileExists);
            Assert.False(parentExists);
            Assert.True(existingExists);
            Assert.True(directoryExists);
            Assert.Equal(0, absentExceptions);
            Assert.Equal(1, controlExceptions);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    private static IEnumerable<string> Ancestors(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            yield return Path.TrimEndingDirectorySeparator(current.FullName);
    }

    private sealed class DeepFixture : IDisposable
    {
        private readonly GitWorkspaceLaunchFixture fixture = new();
        internal string Root => fixture.Root;
        internal string FirstLevel => Path.Combine(Root, "d");
        internal string Repo { get; }
        internal string Linked { get; }

        internal DeepFixture()
        {
            try
            {
                var levels = Enumerable.Repeat("d", 16).Append("repo").ToArray();
                Repo = fixture.CreateRepository(Path.Combine(levels));
                Linked = fixture.AddLinkedWorktree(Repo);
                Assert.True(Path.GetRelativePath(Root, Linked).Split(Path.DirectorySeparatorChar).Length >= 10);
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        public void Dispose() => fixture.Dispose();
    }
}
