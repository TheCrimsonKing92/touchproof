using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: isolated repository, reader cache, clock and owned process handles.
// The process-wide Shared reader is only inspected, never disposed by these tests.
public sealed class AssemblyTempRootCleanupFixtureSharedReaderTests
{
    [Fact]
    public async Task Dispose_LiveReader_ClosesBeforeRootRelease()
    {
        var root = Directory.CreateTempSubdirectory("fixture-shared-reader-").FullName;
        using var readers = new GitRevisionReader(clock: new ManualStewardTimeProvider());
        try
        {
            var repo = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;
            var environment = new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = "Reader", ["GIT_AUTHOR_EMAIL"] = "reader@example.test",
                ["GIT_COMMITTER_NAME"] = "Reader", ["GIT_COMMITTER_EMAIL"] = "reader@example.test",
                ["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
                ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_COUNT"] = "2",
                ["GIT_CONFIG_KEY_0"] = "commit.gpgSign", ["GIT_CONFIG_VALUE_0"] = "false",
                ["GIT_CONFIG_KEY_1"] = "core.hooksPath", ["GIT_CONFIG_VALUE_1"] = Path.Combine(root, "no-hooks")
            };
            foreach (var args in new[]
                     {
                         new[] { "init", "--initial-branch=fixture-main" },
                         new[] { "commit", "--allow-empty", "-m", "seed" },
                         new[] { "commit", "--allow-empty", "-m", "second" }
                     })
            {
                var result = GitCli.RunWithEnvironment(repo, environment, args);
                Assert.True(result.Succeeded, result.Error);
            }

            // HEAD~1 requires ancestry resolution, which the file resolver declines.
            // These reads reach Git. Keep reading until
            // admission creates a persistent reader, with a bound that fails loudly.
            const int maximumAdmissionReads = 32;
            for (var attempt = 0; attempt < maximumAdmissionReads && readers.GetReader(repo) is null; attempt++)
            {
                Assert.True(readers.TryResolve(repo, "HEAD~1", out _));
            }
            var process = Assert.IsType<GitRevisionReader.ReaderProcess>(readers.GetReader(repo));
            Assert.False(process.HasExited);
            Assert.Equal(1, readers.RetainedCount);
            int? retainedAtRelease = null;
            bool? exitedAtRelease = null;
            TempRootDeleteOutcome? outcome = null;
            var fixture = new AssemblyTempRootCleanupFixture(() =>
            {
                retainedAtRelease = readers.RetainedCount;
                exitedAtRelease = process.HasExited;
                return outcome = AssemblyTempRedirect.DeleteTree(root);
            }, TextWriter.Null, sharedReaders: readers);

            await fixture.DisposeAsync();

            Assert.Equal(0, retainedAtRelease);
            Assert.Equal(true, exitedAtRelease);
            Assert.NotNull(outcome);
            Assert.Equal(TempRootDeleteStatus.Deleted, outcome.Status);
            Assert.False(Directory.Exists(root));
            Assert.Null(readers.GetReader(repo));
            Assert.Equal(0, readers.RetainedCount);
        }
        finally
        {
            readers.Dispose();
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Fact]
    public void Resolve_InfrastructureLoaded_ReturnsProcessSharedReader() =>
        Assert.Same(GitRevisionReader.Shared, AssemblyTempRootCleanupFixture.ResolveSharedGitReaders());
}
