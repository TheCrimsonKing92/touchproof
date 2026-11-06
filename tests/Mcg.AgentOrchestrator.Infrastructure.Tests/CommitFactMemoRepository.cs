using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe helper: unique repositories; synchronous thread-local launch observation.
internal sealed class CommitFactMemoRepository : IDisposable
{
    internal string Root { get; } = InfrastructureTestSupport.CreateTempDirectory();
    internal string Base { get; }

    internal CommitFactMemoRepository()
    {
        Git("init", "--initial-branch=main");
        Git("config", "user.name", "Memo fixture");
        Git("config", "user.email", "memo@example.test");
        Git("config", "commit.gpgSign", "false");
        Git("config", "core.hooksPath", Path.Combine(Root, "no-hooks"));
        Git("commit", "--allow-empty", "-m", "base");
        Base = Git("rev-parse", "HEAD");
    }

    internal string Git(params string[] args) => GitAt(Root, args);

    internal static string GitAt(string root, params string[] args)
    {
        var result = InfrastructureTestSupport.RunGitProbe(root, args);
        Assert.True(result.Succeeded, result.StandardError);
        return result.StandardOutput.Trim();
    }

    internal string Commit(string path, string text)
    {
        var full = Path.Combine(Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        Git("add", "--", path);
        Git("commit", "-m", "change " + path);
        return Git("rev-parse", "HEAD");
    }

    internal string MergeTip()
    {
        Git("checkout", "-B", "side", Base);
        Commit("side.txt", "side");
        Git("checkout", "-B", "candidate", Base);
        Commit("candidate.txt", "candidate");
        Git("merge", "--no-ff", "--no-edit", "side");
        return Git("rev-parse", "HEAD");
    }

    internal static List<string[]> Capture(Action action)
    {
        var launches = new List<string[]>();
        var previous = GitCli.StartInfoObserver;
        GitCli.StartInfoObserver = info =>
        {
            previous?.Invoke(info);
            launches.Add(info.ArgumentList.Skip(GitCli.HardeningArguments.Count).ToArray());
        };
        try { action(); }
        finally { GitCli.StartInfoObserver = previous; }
        return launches;
    }

    public void Dispose() => TempRootJanitor.DeleteTree(Root);
}
