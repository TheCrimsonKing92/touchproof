using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixture-owned repositories/environments and test-identity-scoped census windows.
public sealed class GitRefFileResolverParityTests
{
    [Theory]
    [InlineData("loose")]
    [InlineData("packed")]
    [InlineData("symbolic")]
    [InlineData("symbolic-packed")]
    [InlineData("ambiguous")]
    [InlineData("ambiguous-packed")]
    [InlineData("annotated-tag")]
    [InlineData("annotated-tag-packed")]
    [InlineData("missing")]
    public void BranchAndCommitForms_MatchGitOrInvokeFallback(string shape)
    {
        using var fixture = new Fixture(commit: shape != "missing");
        var metadata = Path.Combine(fixture.Repo, ".git");
        if (shape.StartsWith("symbolic", StringComparison.Ordinal))
        {
            fixture.Git("branch", "target");
            fixture.Git("symbolic-ref", "refs/heads/fixture-main", "refs/heads/target");
            Assert.Equal("ref: refs/heads/target", File.ReadAllText(
                Path.Combine(metadata, "refs", "heads", "fixture-main")).Trim());
        }
        else if (shape.StartsWith("ambiguous", StringComparison.Ordinal) ||
                 shape.StartsWith("annotated-tag", StringComparison.Ordinal))
        {
            if (shape.StartsWith("annotated-tag", StringComparison.Ordinal))
                fixture.Git("branch", "-m", "real-main");
            fixture.Git("tag", "-a", "fixture-main", "-m", "tag before branch advances");
            fixture.Git("commit", "--allow-empty", "-m", "branch advances");
            Assert.NotEqual(fixture.Oracle("HEAD^{commit}"), fixture.Oracle("fixture-main^{commit}"));
            Assert.NotEqual(fixture.Oracle("fixture-main"), fixture.Oracle("fixture-main^{commit}"));
        }
        if (shape.EndsWith("packed", StringComparison.Ordinal))
        {
            fixture.Git("pack-refs", "--all");
            Assert.True(File.Exists(Path.Combine(metadata, "packed-refs")));
            // A symbolic branch remains loose; its target is packed instead.
            var packedBranch = shape == "symbolic-packed" ? "target" :
                shape == "annotated-tag-packed" ? "real-main" : "fixture-main";
            Assert.False(File.Exists(Path.Combine(metadata, "refs", "heads", packedBranch)));
        }

        var identity = HostCapacitySlotTimingWriter.CurrentTestName();
        Assert.NotEqual("<unknown>", identity);
        HostCapacitySlotGitCensus.EnsureInstalled(nameof(GitRefFileResolverParityTests));
        foreach (var revision in new[] { "fixture-main", "HEAD^{commit}", "fixture-main^{commit}" })
        {
            var expected = GitCli.Run(fixture.Repo, "rev-parse", "--verify", revision);
            Assert.Equal(shape != "missing", expected.Succeeded);
            var shouldResolve = shape is "loose" or "packed" ||
                revision == "HEAD^{commit}" && (shape.StartsWith("ambiguous", StringComparison.Ordinal) ||
                                               shape.StartsWith("annotated-tag", StringComparison.Ordinal));
            Assert.Equal(shouldResolve, GitRefFileResolver.TryResolve(fixture.Repo, revision, out var fileValue));
            Assert.Equal(shouldResolve ? expected.Output.Trim() : string.Empty, fileValue);

            // Isolate each read so fallback must use one-shot Git, including failed lookups.
            using var reader = new GitRevisionReader();
            var window = Assert.IsType<HostCapacitySlotGitCensus.Window>(HostCapacitySlotGitCensus.Shared.Open(identity));
            var resolved = reader.TryResolve(fixture.Repo, revision, out var actual);
            var delta = Assert.IsType<HostCapacitySlotGitCensus.GitLaunchDelta>(window.Close());
            Assert.Equal(expected.Succeeded, resolved);
            Assert.Equal(expected.Succeeded ? expected.Output.Trim() : string.Empty, actual);
            Assert.Equal(shouldResolve ? 0 : 1, delta.Launches);
            if (!shouldResolve) Assert.Equal(1, delta.Subcommands["rev-parse"]);
            Assert.Null(reader.GetReader(fixture.Repo));
        }
    }

    [Theory]
    [InlineData(false, "uppercase")]
    [InlineData(false, "wrong-length")]
    [InlineData(true, "uppercase")]
    [InlineData(true, "wrong-length")]
    public void NoncanonicalRawIds_UseGitWithoutRecursivePeeling(bool sha256, string shape)
    {
        using var fixture = new Fixture(sha256);
        var original = fixture.Oracle("HEAD");
        var revision = shape == "uppercase" ? original.ToUpperInvariant()
            : sha256 ? original[..40] : original + new string('0', 24);
        Assert.NotEqual(original, revision);
        if (shape == "uppercase") Assert.Contains(revision, char.IsAsciiLetterUpper);
        else Assert.NotEqual(original.Length, revision.Length);

        foreach (var suffix in new[] { "", "^{commit}", "^{tree}" })
        {
            var expression = revision + suffix;
            var expected = GitCli.Run(fixture.Repo, "rev-parse", "--verify", expression);
            Assert.False(GitRefFileResolver.TryResolve(fixture.Repo, expression, out var fileValue));
            Assert.Empty(fileValue);
            using var reader = new GitRevisionReader();
            var observed = 0;
            reader.SuccessfulGitRead += _ => observed++;
            Assert.Equal(expected.Succeeded, reader.TryResolve(fixture.Repo, expression, out var actual));
            Assert.Equal(expected.Succeeded ? expected.Output.Trim() : string.Empty, actual);
            Assert.Equal(expected.Succeeded ? 1 : 0, observed);
            Assert.Null(reader.GetReader(fixture.Repo));
        }
    }

    [Theory]
    [InlineData("packed")]
    [InlineData("replacement")]
    [InlineData("ambiguous")]
    [InlineData("missing")]
    [InlineData("corrupt")]
    public void UnsupportedRawObjectIds_RetainGitFallback(string shape)
    {
        using var fixture = new Fixture();
        var revision = fixture.Oracle("HEAD");
        if (shape == "packed") fixture.Git("gc", "--prune=now");
        else if (shape == "replacement")
        {
            fixture.Git("commit", "--allow-empty", "-m", "replacement");
            fixture.Git("replace", revision, fixture.Oracle("HEAD"));
        }
        else if (shape == "ambiguous") fixture.Git("tag", revision);
        else if (shape == "missing") revision = new string('f', revision.Length);
        else
        {
            var path = Path.Combine(fixture.Repo, ".git", "objects", revision[..2], revision[2..]);
            File.SetAttributes(path, FileAttributes.Normal);
            File.WriteAllText(path, "not a compressed Git object");
        }
        var expected = GitCli.Run(fixture.Repo, "rev-parse", "--verify", revision);
        Assert.False(GitRefFileResolver.TryResolve(fixture.Repo, revision, out var fileValue));
        Assert.Empty(fileValue);
        using var reader = new GitRevisionReader();
        var observed = 0;
        reader.SuccessfulGitRead += _ => observed++;
        Assert.Equal(expected.Succeeded, reader.TryResolve(fixture.Repo, revision, out var actual));
        Assert.Equal(expected.Succeeded ? expected.Output.Trim() : string.Empty, actual);
        Assert.Equal(expected.Succeeded ? 1 : 0, observed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AmbiguousBranchShorthand_LeavesTagPrecedenceToGit(bool packed)
    {
        using var fixture = new Fixture();
        fixture.Git("tag", "-a", "fixture-main", "-m", "old tag");
        fixture.Git("commit", "--allow-empty", "-m", "branch moves");
        if (packed) fixture.Git("pack-refs", "--all");
        Assert.NotEqual(fixture.Oracle("fixture-main"), fixture.Oracle("refs/heads/fixture-main"));
        Assert.False(GitRefFileResolver.TryResolve(fixture.Repo, "fixture-main", out _));
        using var reader = new GitRevisionReader();
        Assert.True(reader.TryResolve(fixture.Repo, "fixture-main", out var actual));
        Assert.Equal(fixture.Oracle("fixture-main"), actual);
    }

    [Theory]
    [InlineData("packed-object")]
    [InlineData("ambiguous-tag")]
    [InlineData("packed-ambiguous-tag")]
    [InlineData("replacement")]
    [InlineData("tag")]
    public void UnsupportedCommitExpressions_UseGitAndPreserveItsValue(string shape)
    {
        using var fixture = new Fixture();
        var revision = "fixture-main^{commit}";
        if (shape == "packed-object") fixture.Git("gc", "--prune=now");
        else if (shape == "replacement")
        {
            var first = fixture.Oracle("HEAD");
            fixture.Git("commit", "--allow-empty", "-m", "replacement");
            fixture.Git("replace", first, fixture.Oracle("HEAD"));
            revision = first + "^{tree}";
        }
        else
        {
            var tag = shape == "tag" ? "annotated" : "fixture-main";
            fixture.Git("tag", "-a", tag, "-m", "tag");
            fixture.Git("commit", "--allow-empty", "-m", "branch advances");
            if (shape == "packed-ambiguous-tag") fixture.Git("pack-refs", "--all");
            revision = tag + "^{commit}";
        }
        var expected = fixture.Oracle(revision);
        Assert.False(GitRefFileResolver.TryResolve(fixture.Repo, revision, out var fileValue));
        Assert.Empty(fileValue);
        using var reader = new GitRevisionReader();
        var observed = 0;
        reader.SuccessfulGitRead += _ => observed++;
        Assert.True(reader.TryResolve(fixture.Repo, revision, out var actual));
        Assert.Equal(expected, actual);
        Assert.Equal(1, observed); // A file miss really traversed the Git path.
        Assert.Null(reader.GetReader(fixture.Repo));
    }

    [Theory]
    [InlineData("loose")]
    [InlineData("packed")]
    [InlineData("override")]
    [InlineData("detached")]
    [InlineData("symbolic")]
    [InlineData("symbolic-packed")]
    [InlineData("linked")]
    [InlineData("sha256")]
    [InlineData("unborn-head")]
    [InlineData("unborn-branch")]
    public void FileRef_MatchesGit(string shape)
    {
        using var fixture = new Fixture(shape == "sha256", !shape.StartsWith("unborn-", StringComparison.Ordinal));
        var repo = fixture.Repo;
        var revision = shape is "detached" or "symbolic" or "symbolic-packed" or "linked" or "unborn-head"
            ? "HEAD" : "refs/heads/fixture-main";
        var loose = Path.Combine(repo, ".git", "refs", "heads", "fixture-main");
        if (shape is "packed" or "override" or "symbolic-packed")
        {
            var original = fixture.Oracle("HEAD");
            fixture.Git("tag", "-a", "annotated", "-m", "peel line");
            fixture.Git("pack-refs", "--all");
            Assert.False(File.Exists(loose));
            if (shape == "override")
            {
                fixture.Git("commit", "--allow-empty", "-m", "new loose value");
                Assert.NotEqual(original, fixture.Oracle("HEAD"));
                Assert.Contains(original + " refs/heads/fixture-main", File.ReadAllText(Path.Combine(repo, ".git", "packed-refs")));
                Assert.Equal(fixture.Oracle("HEAD"), File.ReadAllText(loose).Trim());
            }
        }
        else if (shape == "detached") fixture.Git("checkout", "--detach");
        else if (shape == "linked")
        {
            var original = fixture.Oracle("HEAD");
            var linked = Path.Combine(fixture.Root, "linked");
            fixture.Git("worktree", "add", "-b", "other", linked);
            fixture.Git("commit", "--allow-empty", "-m", "main advances");
            Assert.NotEqual(original, fixture.Oracle("HEAD"));
            repo = linked;
        }
        else if (!shape.StartsWith("unborn-", StringComparison.Ordinal)) Assert.True(File.Exists(loose));

        var expected = GitCli.Run(repo, "rev-parse", "--verify", revision);
        var decided = GitRefFileResolver.TryResolve(repo, revision, out var actual);
        Assert.Equal(expected.Succeeded, decided);
        Assert.Equal(decided ? expected.Output.Trim() : string.Empty, actual);
    }

    internal sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("git-ref-files-").FullName;
        internal string Repo { get; }
        internal Fixture(bool sha256 = false, bool commit = true)
        {
            Repo = Directory.CreateDirectory(Path.Combine(Root, "repo")).FullName;
            Git("init", "--initial-branch=fixture-main", sha256 ? "--object-format=sha256" : "--object-format=sha1");
            if (commit) Git("commit", "--allow-empty", "-m", "seed");
        }
        internal void Git(params string[] args)
        {
            var result = GitCli.RunWithEnvironment(Repo, new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = "Reader", ["GIT_AUTHOR_EMAIL"] = "reader@example.test",
                ["GIT_COMMITTER_NAME"] = "Reader", ["GIT_COMMITTER_EMAIL"] = "reader@example.test",
                ["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
                ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_COUNT"] = "2",
                ["GIT_CONFIG_KEY_0"] = "commit.gpgSign", ["GIT_CONFIG_VALUE_0"] = "false",
                ["GIT_CONFIG_KEY_1"] = "core.hooksPath", ["GIT_CONFIG_VALUE_1"] = Path.Combine(Root, "no-hooks")
            }, args);
            Assert.True(result.Succeeded, result.Error);
        }
        internal string Oracle(string revision)
        {
            var result = GitCli.Run(Repo, "rev-parse", "--verify", revision);
            Assert.True(result.Succeeded, result.Error);
            return result.Output.Trim();
        }
        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
