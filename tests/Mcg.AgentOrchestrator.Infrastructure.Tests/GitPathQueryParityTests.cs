using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = GitRefFileResolverParityTests.Fixture;

// Parallel-safe: checkout/metadata paths and fallback observations belong to each case.
public sealed class GitPathQueryParityTests
{
    [Fact]
    public void MalformedPointer_InvokesGitAndPreservesItsFailure()
    {
        using var fixture = new Fixture();
        var directory = Directory.CreateDirectory(Path.Combine(fixture.Root, "not-a-repository")).FullName;
        File.WriteAllText(Path.Combine(directory, ".git"), "gitdir: missing\n");
        var expected = GitCli.Run(directory, "rev-parse", "--git-dir");
        Assert.False(expected.Succeeded);
        var calls = 0;
        var actual = GitPathQuery.Run(directory, () =>
        {
            calls++;
            return GitCli.Run(directory, "rev-parse", "--git-dir");
        }, _ => null, "--git-dir");
        Assert.Equal(1, calls);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("linked")]
    [InlineData("separate")]
    public void SupportedPaths_MatchGitWithoutFallback(string layout)
    {
        using var fixture = new Fixture();
        var directory = fixture.Repo;
        fixture.Git("config", "user.name", "Path Tests");
        fixture.Git("config", "user.email", "paths@example.test");
        if (layout == "linked")
        {
            directory = Path.Combine(fixture.Root, "linked");
            fixture.Git("worktree", "add", "-b", "linked", directory);
        }
        else if (layout == "separate")
            fixture.Git("init", "--separate-git-dir=" + Path.Combine(fixture.Root, "metadata"));

        foreach (var form in new[] { new[] { "--show-toplevel" }, ["--git-dir"], ["--git-common-dir"],
            ["--git-path", "objects"], ["--git-path", "info/exclude"], ["--git-path", "index"],
            ["--git-path", "HEAD"], ["--git-path", "mcg-rematerialization"] })
        foreach (var absolute in new[] { false, true })
        {
            string[] arguments = absolute ? ["--path-format=absolute", .. form] : form;
            var expected = GitCli.Run(directory, ["rev-parse", .. arguments]);
            Assert.True(expected.Succeeded, expected.Error);
            var fallbacks = 0;
            var actual = GitPathQuery.Run(directory, () => { fallbacks++; return expected; },
                _ => null, arguments);
            Assert.Equal(0, fallbacks);
            Assert.Equal(expected.Output, actual.Output);
            Assert.Equal(expected.Error, actual.Error);
            Assert.Equal(expected.ExitCode, actual.ExitCode);
            Assert.False(actual.ProcessStarted);
        }
    }

    [Theory]
    [InlineData("subdirectory")]
    [InlineData("override")]
    [InlineData("config")]
    [InlineData("extension")]
    [InlineData("pointer")]
    [InlineData("bare")]
    [InlineData("arguments")]
    [InlineData("unknown-path")]
    [InlineData("relative-pointer")]
    [InlineData("external-common")]
    public void UnsupportedLayout_ReturnsFallbackReceiptUnchanged(string shape)
    {
        using var fixture = new Fixture();
        var directory = fixture.Repo;
        Func<string, string?> environment = _ => null;
        string[] arguments = ["--git-dir"];
        switch (shape)
        {
            case "subdirectory": directory = Directory.CreateDirectory(Path.Combine(directory, "child")).FullName; break;
            case "override": environment = name => name == "GIT_DIR" ? "elsewhere" : null; break;
            case "config": fixture.Git("config", "core.worktree", fixture.Repo); break;
            case "extension": File.AppendAllText(Path.Combine(directory, ".git", "config"), "\n[extensions]\nunrecognized = true\n"); break;
            case "pointer":
                directory = Directory.CreateDirectory(Path.Combine(fixture.Root, "broken")).FullName;
                File.WriteAllText(Path.Combine(directory, ".git"), "gitdir: missing\n"); break;
            case "bare": directory = Path.Combine(directory, ".git"); break;
            case "arguments": arguments = ["--git-dir", "HEAD"]; break;
            case "unknown-path": arguments = ["--git-path", "hooks"]; break;
            case "relative-pointer":
                fixture.Git("init", "--separate-git-dir=" + Path.Combine(fixture.Root, "metadata"));
                // Git hides this file on Windows; truncate the existing file without CREATE_ALWAYS.
                using (var writer = new StreamWriter(File.Open(Path.Combine(directory, ".git"), FileMode.Truncate, FileAccess.Write)))
                    writer.Write("gitdir: ../metadata\n");
                break;
            case "external-common":
                var common = Directory.CreateDirectory(Path.Combine(fixture.Root, "common")).FullName;
                Directory.CreateDirectory(Path.Combine(common, "refs"));
                Directory.CreateDirectory(Path.Combine(common, "objects"));
                File.Copy(Path.Combine(directory, ".git", "config"), Path.Combine(common, "config"));
                File.WriteAllText(Path.Combine(directory, ".git", "commondir"), common + "\n"); break;
        }
        var receipt = new GitCli.GitResult(128, "retained stdout\n", "retained diagnostic", true, false);
        var calls = 0;
        var actual = GitPathQuery.Run(directory, () => { calls++; return receipt; }, environment, arguments);
        Assert.Equal(1, calls);
        Assert.Equal(receipt, actual);
    }
}
