using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: refusals use call-local environment dictionaries and fixture-owned metadata.
public sealed class GitMetadataLookupSeamFallbackTests
{
    [Fact]
    public void EnvironmentRefusals_LaunchExactlyOnceWithGitBytes()
    {
        using var fixture = new GitMetadataLookupFixture();
        GitMetadataLookupFixture.AssertInProcess(fixture.Main, "rev-parse", "--show-toplevel");
        GitMetadataLookupFixture.AssertInProcess(fixture.Main, "rev-parse", "--verify", "HEAD");
        GitMetadataLookupFixture.AssertInProcess(fixture.Main, "branch", "--show-current");
        foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE",
            "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_CONFIG_PARAMETERS",
            "GIT_CONFIG_COUNT", "GIT_CONFIG", "GIT_NAMESPACE", "GIT_CEILING_DIRECTORIES",
            "GIT_DISCOVERY_ACROSS_FILESYSTEM" })
        foreach (var arguments in new[] { new[] { "rev-parse", "--show-toplevel" },
            ["rev-parse", "--verify", "HEAD"], ["branch", "--show-current"] })
        {
            var value = name switch
            {
                "GIT_DIR" or "GIT_COMMON_DIR" => Path.Combine(fixture.Main, ".git"),
                "GIT_WORK_TREE" => fixture.Main,
                "GIT_INDEX_FILE" => Path.Combine(fixture.Main, ".git", "other-index"),
                "GIT_OBJECT_DIRECTORY" or "GIT_ALTERNATE_OBJECT_DIRECTORIES" => Path.Combine(fixture.Main, ".git", "objects"),
                "GIT_CONFIG_PARAMETERS" => "'core.bare=false'",
                "GIT_CONFIG_COUNT" or "GIT_DISCOVERY_ACROSS_FILESYSTEM" => "0",
                "GIT_CONFIG" => Path.Combine(fixture.Main, ".git", "config"),
                "GIT_NAMESPACE" => "lookup-test",
                "GIT_CEILING_DIRECTORIES" => Path.GetDirectoryName(fixture.Main)!,
                _ => throw new ArgumentOutOfRangeException(nameof(name))
            };
            GitMetadataLookupFixture.Fallback(fixture.Main, arguments, new Dictionary<string, string> { [name] = value });
        }
        foreach (var value in new[] { "", "0" })
            GitMetadataLookupFixture.Fallback(fixture.Main, ["branch", "--show-current"],
                new Dictionary<string, string> { ["GIT_CONFIG_COUNT"] = value });
        if (OperatingSystem.IsWindows())
            GitMetadataLookupFixture.Fallback(fixture.Main, ["rev-parse", "--git-dir"],
                new Dictionary<string, string>(StringComparer.Ordinal) { ["git_dir"] = Path.Combine(fixture.Main, ".git") });
        foreach (var name in new[] { "GIT_NAMESPACE", "GIT_REPLACE_REF_BASE", "GIT_NO_REPLACE_OBJECTS", "GIT_CONFIG" })
            GitMetadataLookupFixture.Fallback(fixture.Main, ["rev-parse", "--verify", "HEAD"],
                new Dictionary<string, string> { [name] = name == "GIT_REPLACE_REF_BASE" ? "refs/replacements/" : "1" });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SubdirectoryAndPackedOnly_AllFormsLaunchExactlyOnce(bool linked)
    {
        using var fixture = new GitMetadataLookupFixture();
        var directory = linked ? fixture.Linked : fixture.Main;
        GitMetadataLookupFixture.AssertInProcess(directory, "rev-parse", "--git-dir");
        var child = Directory.CreateDirectory(Path.Combine(directory, "child")).FullName;
        foreach (var arguments in GitMetadataLookupFixture.Forms())
            GitMetadataLookupFixture.Fallback(child, arguments, checkHelper: true);
        GitWorkspaceLaunchFixture.Git(fixture.Main, "pack-refs", "--all");
        Assert.True(File.Exists(Path.Combine(fixture.Main, ".git", "packed-refs")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Main, ".git", "refs"), "*", SearchOption.AllDirectories));
        foreach (var arguments in GitMetadataLookupFixture.Forms())
            GitMetadataLookupFixture.Fallback(directory, arguments, checkHelper: true);
    }

    [Theory]
    [InlineData("abbreviated")]
    [InlineData("ambiguous")]
    [InlineData("full-ambiguous")]
    [InlineData("missing")]
    [InlineData("expression")]
    public void UnprovedRevisions_PreserveGitOutputAndWarnings(string shape)
    {
        using var fixture = new GitMetadataLookupFixture();
        GitMetadataLookupFixture.AssertInProcess(fixture.Linked, "rev-parse", "--verify", "HEAD");
        if (shape == "ambiguous") GitWorkspaceLaunchFixture.Git(fixture.Main, "tag", "main");
        if (shape == "full-ambiguous")
            GitWorkspaceLaunchFixture.Git(fixture.Main, "update-ref", "refs/heads/refs/heads/main", "HEAD");
        var revision = shape switch
        {
            "abbreviated" or "ambiguous" => "main",
            "full-ambiguous" => "refs/heads/main",
            "missing" => "refs/heads/missing",
            _ => "HEAD^{commit}"
        };
        var expected = GitCli.RunDirect(fixture.Linked, "rev-parse", "--verify", revision);
        if (shape.Contains("ambiguous", StringComparison.Ordinal)) Assert.Contains("ambiguous", expected.Error);
        // The helper already resolves peel expressions; the production seam must still launch git.
        GitMetadataLookupFixture.Fallback(fixture.Linked, ["rev-parse", "--verify", revision],
            checkHelper: shape != "expression");
    }

    [Fact]
    public void UnsupportedShapesAndPathSpellings_RetainGit()
    {
        using var fixture = new GitMetadataLookupFixture();
        GitMetadataLookupFixture.AssertInProcess(fixture.Main, "rev-parse", "--show-toplevel");
        foreach (var arguments in new[] { new[] { "rev-parse", "--verify", "--quiet", "HEAD" },
            ["rev-parse", "--short", "HEAD"], ["branch", "--show-current", "--no-color"],
            ["rev-parse", "--git-path", "unknown-name"], ["rev-parse", "--show-toplevel", "--git-dir"] })
            GitMetadataLookupFixture.Fallback(fixture.Main, arguments);
        if (OperatingSystem.IsWindows())
            GitMetadataLookupFixture.Fallback(fixture.Main.ToUpperInvariant(), ["rev-parse", "--show-toplevel"]);
        var unicode = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(fixture.Main)!, "caf\u00e9")).FullName;
        GitWorkspaceLaunchFixture.Git(unicode, "init", "--initial-branch=main", "--template=");
        GitMetadataLookupFixture.Fallback(unicode, ["rev-parse", "--show-toplevel"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LowercaseDrive_AllPathFormsLaunchWithGitBytes(bool linked)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new GitMetadataLookupFixture();
        var directory = linked ? fixture.Linked : fixture.Main;
        GitMetadataLookupFixture.AssertInProcess(directory, "rev-parse", "--show-toplevel");
        var lowercaseDrive = char.ToLowerInvariant(directory[0]) + directory[1..];
        Assert.NotEqual(directory, lowercaseDrive);
        Assert.True(Directory.Exists(lowercaseDrive));
        foreach (var arguments in GitMetadataLookupFixture.Forms().Where(arguments =>
            arguments[0] == "rev-parse" && arguments[1] != "--verify"))
            GitMetadataLookupFixture.Fallback(lowercaseDrive, arguments, checkHelper: true);
    }

    [Fact]
    public void ExplicitObserverAndStandardInput_KeepProcessContract()
    {
        using var fixture = new GitMetadataLookupFixture();
        GitMetadataLookupFixture.AssertInProcess(fixture.Linked, "rev-parse", "--verify", "HEAD");
        string[] arguments = ["rev-parse", "--verify", "HEAD"];
        var expected = GitCli.RunDirect(fixture.Linked, arguments);
        var completions = 0;
        using (var recording = new GitWorkspaceLaunchFixture.Recording())
        {
            var observed = GitCli.RunExecutableObserved("git", fixture.Linked, 60_000,
                _ => _ => completions++, TimeProvider.System, arguments);
            GitMetadataLookupFixture.EqualBytes(expected, observed);
            Assert.Equal(1, recording.Close("rev-parse"));
            Assert.Equal(1, completions);
        }
        using (var recording = new GitWorkspaceLaunchFixture.Recording())
        {
            var input = GitCli.RunWithStandardInput(fixture.Linked, 60_000, string.Empty, arguments);
            GitMetadataLookupFixture.EqualBytes(expected, input);
            Assert.Equal(1, recording.Close("rev-parse"));
        }
    }
}
