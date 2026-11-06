using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = GitRefFileResolverParityTests.Fixture;

// Parallel-safe: isolated reader owners/repositories and instance-owned read observations.
public sealed class GitRevisionReaderAdmissionTests(ITestOutputHelper output)
{
    [Fact]
    public void QualifyingReads_AdmitOnlyAfterCompletedOneShotBoundary()
    {
        using var fixture = new Fixture();
        using var reader = new GitRevisionReader();
        fixture.Git("commit", "--allow-empty", "-m", "second");
        fixture.Git("tag", "-a", "annotated", "-m", "tag");
        var revisions = new[] { "HEAD~1", "HEAD^{tree}", "annotated", fixture.Oracle("HEAD")[..12] };
        var expected = revisions.Select(fixture.Oracle).ToArray();
        var completed = new List<bool>();
        reader.SuccessfulGitRead += _ => completed.Add(reader.GetReader(fixture.Repo) is not null);
        for (var i = 0; i < GitRevisionReader.DefaultAdmissionReads; i++)
        {
            Assert.True(reader.TryResolve(fixture.Repo, revisions[0], out var value));
            Assert.Equal(expected[0], value);
            Assert.Null(reader.GetReader(fixture.Repo));
            Assert.Equal(i + 1, completed.Count);
            Assert.All(completed, admitted => Assert.False(admitted));
        }
        Assert.Equal(GitRevisionReader.DefaultAdmissionReads, completed.Count);
        GitRevisionReader.ReaderProcess? process = null;
        for (var i = 0; i < revisions.Length; i++)
        {
            Assert.True(reader.TryResolve(fixture.Repo, revisions[i], out var value));
            Assert.Equal(expected[i], value);
            Assert.NotNull(reader.GetReader(fixture.Repo));
            process ??= reader.GetReader(fixture.Repo);
            Assert.Same(process, reader.GetReader(fixture.Repo));
            Assert.Equal(GitRevisionReader.DefaultAdmissionReads + i + 1, completed.Count);
            Assert.True(completed[^1]);
        }
        Assert.False(reader.TryResolve(fixture.Repo, "refs/heads/missing", out var missing));
        Assert.Empty(missing);
    }

    [Fact]
    public void FileHitsAndFailedReads_DoNotAdvanceAdmission()
    {
        using var fixture = new Fixture();
        using var reader = new GitRevisionReader();
        for (var i = 0; i < 4; i++)
        {
            Assert.True(reader.TryResolve(fixture.Repo, "HEAD", out _));
            Assert.False(reader.TryResolve(fixture.Repo, "absent", out _));
        }
        Assert.True(reader.TryResolve(fixture.Repo, "HEAD^{tree}", out _));
        Assert.Null(reader.GetReader(fixture.Repo));
        Assert.True(reader.TryResolve(fixture.Repo, "HEAD^{tree}", out _));
        Assert.NotNull(reader.GetReader(fixture.Repo));
    }

    [Fact]
    public void LinkedWorktrees_KeepIndependentAdmissionAndHeads()
    {
        using var fixture = new Fixture();
        using var reader = new GitRevisionReader();
        var linked = Path.Combine(fixture.Root, "linked");
        fixture.Git("worktree", "add", "-b", "linked", linked);
        Assert.True(reader.TryResolve(fixture.Repo, "HEAD^{tree}", out _));
        Assert.True(reader.TryResolve(fixture.Repo, "HEAD^{tree}", out _));
        Assert.NotNull(reader.GetReader(fixture.Repo));
        Assert.True(reader.TryResolve(linked, "HEAD^{tree}", out var first));
        Assert.Null(reader.GetReader(linked));
        Assert.True(reader.TryResolve(linked, "HEAD^{tree}", out var second));
        Assert.Equal(first, second);
        Assert.NotSame(reader.GetReader(fixture.Repo), reader.GetReader(linked));
        Assert.NotNull(reader.GetReader(linked));
    }

    [Fact]
    public void ReadCosts_ReportOneShotAndRunningReaderMeasurements()
    {
        using var fixture = new Fixture();
        using var reader = new GitRevisionReader(admissionReads: 0);
        const int samples = 32;
        var expected = fixture.Oracle("HEAD^{tree}");
        var start = Stopwatch.GetTimestamp();
        Assert.True(reader.TryResolve(fixture.Repo, "HEAD^{tree}", out var first));
        var startup = Stopwatch.GetElapsedTime(start);
        Assert.Equal(expected, first);
        Assert.NotNull(reader.GetReader(fixture.Repo));
        start = Stopwatch.GetTimestamp();
        for (var i = 0; i < samples; i++)
        {
            Assert.True(reader.TryResolve(fixture.Repo, "HEAD^{tree}", out var actual));
            Assert.Equal(expected, actual);
        }
        var batch = Stopwatch.GetElapsedTime(start).TotalMilliseconds / samples;
        start = Stopwatch.GetTimestamp();
        for (var i = 0; i < samples; i++) Assert.Equal(expected, fixture.Oracle("HEAD^{tree}"));
        var oneShot = Stopwatch.GetElapsedTime(start).TotalMilliseconds / samples;
        output.WriteLine($"samples={samples}; reader-start-and-first-query-ms={startup.TotalMilliseconds:F3}; " +
            $"running-reader-ms={batch:F3}; one-shot-ms={oneShot:F3}; admission={GitRevisionReader.DefaultAdmissionReads}");
        // Measurements are evidence, never a machine-speed correctness assertion.
    }
}
