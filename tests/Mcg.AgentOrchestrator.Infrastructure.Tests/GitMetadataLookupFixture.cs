using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

// Every case owns its repositories; only the measured seam call enters the launch census.
internal sealed class GitMetadataLookupFixture : IDisposable
{
    private readonly GitWorkspaceLaunchFixture owner = new();
    internal string Main { get; }
    internal string Linked { get; }

    internal GitMetadataLookupFixture()
    {
        Main = owner.CreateRepository();
        GitWorkspaceLaunchFixture.Git(Main, "tag", "lightweight");
        GitWorkspaceLaunchFixture.Git(Main, "-c", "user.name=Lookup Tests", "-c", "user.email=lookup@example.test",
            "-c", "tag.gpgSign=false", "tag", "-a", "annotated", "-m", "tag object");
        Linked = owner.AddLinkedWorktree(Main);
        GitWorkspaceLaunchFixture.Git(Linked, "switch", "-c", "linked");
    }

    internal static IEnumerable<string[]> Forms()
    {
        foreach (var absolute in new[] { false, true })
        {
            string[] prefix = absolute ? ["rev-parse", "--path-format=absolute"] : ["rev-parse"];
            foreach (var form in new[] { "--show-toplevel", "--git-dir", "--git-common-dir", "--is-inside-work-tree" })
                yield return [.. prefix, form];
            foreach (var name in new[] { "objects", "info/exclude", "index", "index.lock", "HEAD",
                "mcg-rematerialization", "mcg-conductor-integration.json", "sequencer", "rebase-merge",
                "rebase-apply", "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD" })
                yield return [.. prefix, "--git-path", name];
        }
        foreach (var reference in new[] { "HEAD", "refs/heads/main", "refs/tags/lightweight", "refs/tags/annotated" })
            yield return ["rev-parse", "--verify", reference];
        yield return ["branch", "--show-current"];
    }

    internal static void EqualBytes(GitCli.GitResult expected, GitCli.GitResult actual)
    {
        Assert.False(expected.DrainTimedOut);
        Assert.False(actual.DrainTimedOut);
        Assert.Equal(expected.ExitCode, actual.ExitCode);
        Assert.Equal(Encoding.UTF8.GetBytes(expected.Output), Encoding.UTF8.GetBytes(actual.Output));
        Assert.Equal(Encoding.UTF8.GetBytes(expected.Error), Encoding.UTF8.GetBytes(actual.Error));
    }

    internal static void Fallback(string directory, string[] arguments,
        IReadOnlyDictionary<string, string>? environment = null, bool checkHelper = false)
    {
        var expected = GitCli.RunDirect(directory, null, environment, arguments);
        using var recording = new GitWorkspaceLaunchFixture.Recording();
        var actual = environment is null ? GitCli.Run(directory, arguments)
            : GitCli.RunWithEnvironment(directory, environment, arguments);
        EqualBytes(expected, actual);
        Assert.True(actual.ProcessStarted);
        Assert.Equal(1, recording.Close(arguments[0]));
        if (checkHelper)
        {
            using var launches = InfrastructureTestSupport.CountGitProbeLaunches();
            var helper = InfrastructureTestSupport.RunGitProbe(directory, arguments);
            InfrastructureTestSupport.RequireCompleteGitOutput(helper);
            Assert.True(helper.ProcessStarted);
            Assert.False(helper.DrainTimedOut);
            Assert.False(helper.TimedOut);
            Assert.Equal(expected.ExitCode, helper.ExitCode);
            Assert.Equal(Encoding.UTF8.GetBytes(expected.Output), Encoding.UTF8.GetBytes(helper.StandardOutput));
            Assert.Equal(Encoding.UTF8.GetBytes(expected.Error), Encoding.UTF8.GetBytes(helper.StandardError));
            Assert.Equal(1, launches.Count);
        }
    }

    internal static void AssertInProcess(string directory, params string[] arguments)
    {
        var expected = GitCli.RunDirect(directory, arguments);
        Assert.True(expected.Succeeded, expected.Error);
        using var recording = new GitWorkspaceLaunchFixture.Recording();
        EqualBytes(expected, GitCli.Run(directory, arguments));
        Assert.Equal(0, recording.Close([]));
    }

    public void Dispose() => owner.Dispose();
}
