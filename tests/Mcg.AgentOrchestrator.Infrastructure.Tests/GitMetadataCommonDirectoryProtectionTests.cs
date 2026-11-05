using Mcg.AgentOrchestrator.Infrastructure;
using static GitWorkspaceLaunchFixture;

// Isolated repositories, per-test census and thread-local start observer; no ambient mutation.
public sealed class GitMetadataCommonDirectoryProtectionTests
{
    [Fact]
    public void LinkedAndPrimaryCheckout_ProtectWithoutGitLaunch()
    {
        using var fixture = new GitWorkspaceLaunchFixture();
        var repo = fixture.CreateRepository();
        var linked = fixture.AddLinkedWorktree(repo);
        foreach (var checkout in new[] { linked, repo })
        {
            var expected = CommonDirectory(checkout); // Real git oracle outside the window.
            var labeler = new Labeler();
            using var launches = new Recording();
            DispatchProcessHost.ProtectGitMetadata(checkout, labeler, GitRevisionReader.ResolveCommonDirectoryOrNull);
            launches.Close();
            var common = (Normalize(expected), "(OI)(CI)M", false);
            Assert.Equal(checkout == linked
                ? [(Normalize(Path.Combine(linked, ".git")), "M", false), common]
                : new[] { common }, labeler.Calls);
        }
    }

    [Fact]
    public void Prepare_LinkedWorkspace_ResolvesCommonDirectoryWithoutGitLaunch()
    {
        using var fixture = new GitWorkspaceLaunchFixture();
        var repo = fixture.CreateRepository();
        var linked = fixture.AddLinkedWorktree(repo, "t-caller-layout");
        var expected = CommonDirectory(linked);
        Assert.Equal(Normalize(expected), Normalize(Assert.IsType<string>(
            GitRevisionReader.ResolveCommonDirectoryOrNull(linked, null))));
        var labeler = new Labeler();
        using var scope = AcceptanceWorkspaceIntegrityPreparer.PushIntegrityLabelerForTests(labeler);
        using var launches = new Recording();
        AcceptanceWorkspaceIntegrityPreparer.Prepare(linked);
        launches.Close();
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains((Normalize(Path.Combine(linked, ".git")), "M", false), labeler.Calls);
            Assert.Contains((Normalize(expected), "(OI)(CI)M", false), labeler.Calls);
        }
        TestContext.Current.TestOutputHelper!.WriteLine("Caller-layout resolver decided 1/1; Prepare git launches=0.");
    }

    [Fact]
    public void InjectedGitDirectory_FallsBackToOneRevParse()
    {
        using var fixture = new GitWorkspaceLaunchFixture();
        var repo = fixture.CreateRepository();
        var linked = fixture.AddLinkedWorktree(repo);
        var other = fixture.CreateRepository("other");
        var environment = new Dictionary<string, string> { ["GIT_DIR"] = Path.Combine(other, ".git") };
        var expected = CommonDirectory(linked, environment);
        Assert.NotEqual(Normalize(CommonDirectory(linked)), Normalize(expected));
        Assert.Null(GitRevisionReader.ResolveCommonDirectoryOrNull(linked, environment));
        var labeler = new Labeler();
        using var launches = new Recording();
        DispatchProcessHost.ProtectGitMetadata(linked, labeler,
            GitRevisionReader.ResolveCommonDirectoryOrNull, environment);
        launches.Close("rev-parse");
        Assert.Equal(["rev-parse", "--git-common-dir"], Assert.Single(launches.Arguments));
        Assert.Equal([(Normalize(Path.Combine(linked, ".git")), "M", false),
            (Normalize(expected), "(OI)(CI)M", false)], labeler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LabelingFailure_PreservesDiagnosticAndDoesNotFallback(bool gitFile)
    {
        using var fixture = new GitWorkspaceLaunchFixture();
        var linked = fixture.AddLinkedWorktree(fixture.CreateRepository());
        var common = Assert.IsType<string>(GitRevisionReader.ResolveCommonDirectoryOrNull(linked, null));
        var target = gitFile ? Path.Combine(linked, ".git") : common;
        var labeler = new Labeler(target);
        using var launches = new Recording();
        var failure = Assert.Throws<InvalidOperationException>(() => DispatchProcessHost.ProtectGitMetadata(
            linked, labeler, GitRevisionReader.ResolveCommonDirectoryOrNull));
        launches.Close();
        Assert.Equal(gitFile ? $"Failed to protect linked worktree git file '{target}'."
            : $"Failed to protect git common dir '{target}'.", failure.Message);
        Assert.Equal(gitFile ? 1 : 2, labeler.Calls.Count);
    }
}
