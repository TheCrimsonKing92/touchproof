using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;
using static GitWorkspaceLaunchFixture;

// Each case owns all metadata it mutates; no process-wide environment changes.
public sealed class GitCommonDirectoryResolverTests
{
    [Theory]
    [InlineData("")]
    [InlineData("../..\nextra\n")]
    [InlineData("\uFEFF../..\n")]
    [InlineData("missing\n")]
    [InlineData(" ../..\n")]
    public void UnexpectedCommonDir_IsUndecided(string content)
    {
        using var fixture = new GitWorkspaceLaunchFixture();
        var linked = fixture.AddLinkedWorktree(fixture.CreateRepository());
        var path = Path.Combine(AdminDirectory(linked), "commondir");
        Assert.NotNull(GitRevisionReader.ResolveCommonDirectoryOrNull(linked, null));
        File.WriteAllText(path, content, new UTF8Encoding(false));
        Assert.Null(GitRevisionReader.ResolveCommonDirectoryOrNull(linked, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidUtf8OrOversizedCommonDir_IsUndecided(bool oversized)
    {
        using var fixture = new GitWorkspaceLaunchFixture();
        var linked = fixture.AddLinkedWorktree(fixture.CreateRepository());
        File.WriteAllBytes(Path.Combine(AdminDirectory(linked), "commondir"),
            oversized ? Encoding.UTF8.GetBytes(new string('x', 4097)) : [0xff, 0xfe]);
        Assert.Null(GitRevisionReader.ResolveCommonDirectoryOrNull(linked, null));
    }

    [Fact]
    public void UnreadableCommonDir_IsUndecidedThenRecovers()
    {
        using var fixture = new GitWorkspaceLaunchFixture();
        var linked = fixture.AddLinkedWorktree(fixture.CreateRepository());
        var expected = CommonDirectory(linked);
        using (var locked = new FileStream(Path.Combine(AdminDirectory(linked), "commondir"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Null(GitRevisionReader.ResolveCommonDirectoryOrNull(linked, null));
        Assert.Equal(Normalize(expected), Normalize(Assert.IsType<string>(
            GitRevisionReader.ResolveCommonDirectoryOrNull(linked, null))));
    }

    [Theory]
    [InlineData("GIT_DIR")]
    [InlineData("GIT_WORK_TREE")]
    [InlineData("GIT_COMMON_DIR")]
    [InlineData("GIT_CONFIG_COUNT")]
    [InlineData("GIT_FUTURE_DISCOVERY_SWITCH")]
    public void NonAllowlistedGitVariable_EvenEmpty_IsUndecided(string name)
    {
        using var fixture = new GitWorkspaceLaunchFixture();
        var repo = fixture.CreateRepository();
        Assert.NotNull(GitRevisionReader.ResolveCommonDirectoryFromEnvironment(repo, new Dictionary<string, string>()));
        Assert.Null(GitRevisionReader.ResolveCommonDirectoryFromEnvironment(repo,
            new Dictionary<string, string> { [name] = "" }));
    }

    [Fact]
    public void AllowedIdentityAndConfigVariables_KeepFileResolution()
    {
        using var fixture = new GitWorkspaceLaunchFixture();
        var repo = fixture.CreateRepository();
        var allowed = new[] { "GIT_AUTHOR_NAME", "GIT_AUTHOR_EMAIL", "GIT_AUTHOR_DATE", "GIT_COMMITTER_NAME",
            "GIT_COMMITTER_EMAIL", "GIT_COMMITTER_DATE", "GIT_CONFIG_NOSYSTEM", "GIT_CONFIG_GLOBAL", "GIT_TERMINAL_PROMPT" };
        Assert.Equal(Normalize(CommonDirectory(repo)), Normalize(Assert.IsType<string>(
            GitRevisionReader.ResolveCommonDirectoryFromEnvironment(repo, allowed.ToDictionary(name => name, _ => "")))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RelativeOrAbsoluteMetadata_ResolvesFromOwningFile(bool absolute)
    {
        using var fixture = new GitWorkspaceLaunchFixture();
        var repo = fixture.CreateRepository();
        var linked = fixture.AddLinkedWorktree(repo);
        var admin = AdminDirectory(linked);
        var expected = CommonDirectory(linked);
        var gitFile = Path.Combine(linked, ".git");
        // Open the existing hidden git file rather than replacing it on Windows.
        using (var writer = new StreamWriter(File.Open(gitFile, FileMode.Truncate, FileAccess.Write), new UTF8Encoding(false)))
            writer.Write("gitdir: " + (absolute ? admin : Path.GetRelativePath(linked, admin)) + "\n");
        File.WriteAllText(Path.Combine(admin, "commondir"),
            (absolute ? expected : Path.GetRelativePath(admin, expected)) + "\n");
        Assert.Equal(Normalize(expected), Normalize(Assert.IsType<string>(
            GitRevisionReader.ResolveCommonDirectoryOrNull(linked, null))));
    }
}
