using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Every git command uses the hermetic probe; repositories and configuration belong to this fixture.
internal sealed class AdoptedBuildPushFixture : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("adopted-push-").FullName;
    internal string Repository => Path.Combine(_root, "repo");
    internal string Remote => Path.Combine(_root, "remote.git");
    internal string OtherRemote => Path.Combine(_root, "other.git");
    internal string OwnerPath => AdoptedBuildLeakScanner.OwnerPatternPath(Path.Combine(_root, "data"));
    internal List<string> Events { get; } = [];
    internal List<string> Attention { get; } = [];
    internal List<string[]> Calls { get; } = [];
    internal string Baseline { get; }
    internal bool FailPush { get; set; }
    internal bool FailFetch { get; set; }
    internal bool FailVerification { get; set; }
    internal bool AdvanceBeforePush { get; set; }

    internal AdoptedBuildPushFixture()
    {
        Directory.CreateDirectory(Repository);
        Git("init", "--initial-branch=main", "--template=");
        Git("config", "user.name", "Fixture Author");
        Git("config", "user.email", "fixture@example.test");
        Git("config", "commit.gpgSign", "false");
        Baseline = Commit("baseline");
        Git("clone", "--bare", "--no-hardlinks", Repository, Remote);
        Git("clone", "--bare", "--no-hardlinks", Repository, OtherRemote);
        Git("remote", "add", "origin", Remote);
        Directory.CreateDirectory(Path.GetDirectoryName(OwnerPath)!);
        File.WriteAllText(OwnerPath, JsonSerializer.Serialize(new Dictionary<string, string>
            { ["fixture-owner"] = "fixture-priva[t]e-value" }));
    }

    internal string Commit(string content)
    {
        File.WriteAllText(Path.Combine(Repository, "payload.txt"), content + "\n");
        Git("add", "--", "payload.txt");
        Git("commit", "-m", "fixture change");
        return Git("rev-parse", "HEAD");
    }

    internal AdoptedBuildPushService Service() => new(Repository,
        AdoptedBuildLeakScannerTests.GenericPath, OwnerPath, Events.Add, Attention.Add, Run);

    internal GitCli.GitResult Run(string directory, string[] args)
    {
        Calls.Add(args.ToArray());
        if (args.Contains("push"))
        {
            if (FailPush) return new(1, "", "fixture push refusal");
            if (AdvanceBeforePush)
            {
                var next = Commit("concurrent remote tip");
                Git("--git-dir=" + Remote, "fetch", "--no-tags", Repository, "main");
                Git("--git-dir=" + Remote, "update-ref", "refs/heads/main", next);
            }
        }
        if (FailFetch && args.Contains("fetch")) return new(1, "", "fixture fetch refusal");
        if (FailVerification && args.Contains("ls-remote") && Calls.Any(call => call.Contains("push")))
            return new(1, "", "fixture query refusal");
        var result = Probe(directory, args);
        return new(result.ExitCode, result.Output, result.Error);
    }

    private GitCli.GitResult Probe(string directory, string[] args)
    {
        var result = InfrastructureTestSupport.RunGitProbe(directory,
            ["-c", "core.hooksPath=" + Path.Combine(_root, "no-hooks"), .. args]);
        InfrastructureTestSupport.RequireCompleteGitOutput(result);
        return new(result.ExitCode ?? -1, result.StandardOutput, result.StandardError,
            result.DrainTimedOut || result.DrainFailed || result.TimedOut, result.ProcessStarted);
    }

    internal string Git(params string[] args)
    {
        var result = Probe(Repository, args);
        Assert.True(result.Succeeded, result.Error);
        return result.Output.Trim();
    }

    internal string RemoteTip => Git("--git-dir=" + Remote, "rev-parse", "refs/heads/main");
    public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
}
