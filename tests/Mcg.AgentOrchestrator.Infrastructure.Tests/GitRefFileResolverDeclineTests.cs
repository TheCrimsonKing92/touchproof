using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = GitRefFileResolverParityTests.Fixture;

// Parallel-safe: the environment accessor is injected; no process environment is changed.
public sealed class GitRefFileResolverDeclineTests
{
    [Theory]
    [InlineData("reftable")]
    [InlineData("GIT_DIR")]
    [InlineData("GIT_COMMON_DIR")]
    [InlineData("malformed-loose")]
    [InlineData("symbolic-branch")]
    [InlineData("malformed-head")]
    [InlineData("uppercase")]
    [InlineData("wrong-format")]
    [InlineData("HEAD.lock")]
    [InlineData("refs/heads/fixture-main.lock")]
    [InlineData("packed-refs.lock")]
    [InlineData("packed-duplicate")]
    [InlineData("packed-malformed")]
    public void UnsafeRef_DeclinesAfterPositiveControl(string hazard)
    {
        using var fixture = new Fixture(sha256: hazard == "wrong-format");
        var repo = fixture.Repo;
        var metadata = Path.Combine(repo, ".git");
        const string revision = "refs/heads/fixture-main";
        var refPath = Path.Combine(metadata, revision.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(GitRefFileResolver.TryResolve(repo, revision, out var expected));
        Assert.Equal(fixture.Oracle(revision), expected);
        Func<string, string?> environment = _ => null;
        if (hazard.StartsWith("GIT_", StringComparison.Ordinal)) environment = key => key == hazard ? metadata : null;
        else if (hazard == "reftable") Directory.CreateDirectory(Path.Combine(metadata, "reftable"));
        else if (hazard.EndsWith(".lock", StringComparison.Ordinal))
            File.WriteAllText(Path.Combine(metadata, hazard.Replace('/', Path.DirectorySeparatorChar)), "");
        else if (hazard == "malformed-head") File.WriteAllText(Path.Combine(metadata, "HEAD"), "unknown\n");
        else if (hazard == "uppercase") File.WriteAllText(refPath, expected.ToUpperInvariant() + "\n");
        else if (hazard == "wrong-format") File.WriteAllText(refPath, expected[..40] + "\n");
        else
        {
            fixture.Git("pack-refs", "--all");
            Assert.False(File.Exists(refPath));
            Assert.True(GitRefFileResolver.TryResolve(repo, revision, out _));
            if (hazard == "malformed-loose") File.WriteAllText(refPath, "not an object id\n");
            else if (hazard == "symbolic-branch") File.WriteAllText(refPath, "ref: refs/heads/elsewhere\n");
            else if (hazard == "packed-duplicate")
                File.AppendAllText(Path.Combine(metadata, "packed-refs"), expected + " " + revision + "\n");
            else File.AppendAllText(Path.Combine(metadata, "packed-refs"), "malformed\n");
        }
        Assert.False(GitRefFileResolver.TryResolve(repo, revision, out var actual, environment));
        Assert.Empty(actual);
        Assert.False(GitRefFileResolver.TryResolve(repo, "HEAD", out _, environment));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("com1/child")]
    [InlineData("trailing./child")]
    [InlineData("Probe")]
    public void WindowsAmbiguity_DeclinesAfterPositiveControl(string name)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        Assert.True(GitRefFileResolver.TryResolve(fixture.Repo, "HEAD", out var id));
        var revision = "refs/heads/" + name;
        var metadata = Path.Combine(fixture.Repo, ".git");
        if (name == "Probe")
        {
            fixture.Git("branch", "probe");
            Assert.True(GitRefFileResolver.TryResolve(fixture.Repo, "refs/heads/probe", out _));
        }
        else
            File.WriteAllText(Path.Combine(metadata, "packed-refs"), "# pack-refs with: sorted\n" + id + " " + revision + "\n");
        Assert.False(GitRefFileResolver.TryResolve(fixture.Repo, revision, out var actual));
        Assert.Empty(actual);
    }

    [Theory]
    [InlineData("fixture-main")]
    [InlineData("HEAD^{commit}")]
    public void SupportedRevisionForms_MatchGit(string revision)
    {
        using var fixture = new Fixture();
        Assert.True(GitRefFileResolver.TryResolve(fixture.Repo, revision, out var actual));
        Assert.Equal(fixture.Oracle(revision), actual);
    }

    [Theory]
    [InlineData("HEAD~1")]
    [InlineData("refs/tags/topic")]
    [InlineData("refs/remotes/origin/main")]
    public void OtherRevisionForms_StayUndecided(string revision)
    {
        using var fixture = new Fixture();
        Assert.True(GitRefFileResolver.TryResolve(fixture.Repo, "HEAD", out _));
        Assert.False(GitRefFileResolver.TryResolve(fixture.Repo, revision, out var actual));
        Assert.Empty(actual);
    }
}
