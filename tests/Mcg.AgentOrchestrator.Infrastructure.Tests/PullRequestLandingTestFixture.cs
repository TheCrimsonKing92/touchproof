using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

internal sealed class PullRequestLandingTestFixture : IDisposable
{
    public const string Attempt = "solo-gate-attempt";
    public const string Url = "https://github.com/octo/sample/pull/7";
    public string Repository { get; } = LandingExecutorTests.CreateGitRepository();
    public OrchestratorWorkspace Workspace { get; }
    public AgentOrchestratorKernel Kernel { get; }
    public Goal Goal { get; }
    public string Candidate { get; }
    public string Main { get; }
    public Runner Forge { get; } = new();

    public PullRequestLandingTestFixture()
    {
        var prepared = LandingExecutorTests.CreateAcceptedCandidate(Repository, "src/delivery.txt");
        Workspace = prepared.Workspace with { IsProjectScoped = true, ProjectName = "sample", Delivery = ProjectDelivery.PullRequest };
        Kernel = prepared.Kernel;
        Goal = prepared.Goal;
        Candidate = LandingExecutorTests.ReadGit(Repository, "rev-parse", GoalWorktreeLayout.BranchName(Goal.Id));
        Main = LandingExecutorTests.ReadGit(Repository, "rev-parse", "main");
        var directory = Path.Combine(Workspace.OrchestratorDirectory, "acceptance-gate-attempts", Goal.Id.Value);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, Attempt + ".attempt.json"), JsonSerializer.Serialize(new
        {
            attemptId = Attempt, goalId = Goal.Id.Value, branchHeadSha = Candidate,
            kind = "gate", startedAt = DateTimeOffset.UtcNow, executionProtocol = "in-process"
        }));
        File.WriteAllText(Path.Combine(directory, Attempt + ".result.json"), JsonSerializer.Serialize(new
        {
            kind = "accepted", acceptance = new { passed = true, checks = new[] { new { name = "unit" } } }
        }));
    }

    public LandingResult Land() => LandingExecutor.Execute(Kernel, Goal, Workspace,
        pullRequestPublisher: new GitHubPullRequestPublisher(Forge));
    public void Dispose() => LandingExecutorTests.TryDeleteDirectory(Repository);

    internal sealed class Runner : IForgeProcessRunner
    {
        public List<(string Command, string[] Arguments)> Calls { get; } = [];
        public string? Failure { get; set; }
        public string? RemoteTip { get; set; }
        public bool ExistingPr { get; set; }
        public int Creates { get; private set; }
        public ForgeProcessResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            IReadOnlyDictionary<string, string> environment, TimeSpan timeout)
        {
            var args = arguments.ToArray();
            Calls.Add((fileName, args));
            if (fileName == "git")
            {
                if (args[0] == "remote") return Ok(Failure == "origin" ? "https://gitlab.com/a/b" : "https://github.com/octo/sample.git");
                if (args[0] == "ls-remote") return Ok(RemoteTip is null ? "" : RemoteTip + "\t" + args[^1]);
                if (Failure == "push") return new(true, 1, "", "push rejected");
                RemoteTip = args[^1].Split(':')[0];
                return Ok("");
            }
            if (Failure == "auth") return new(true, 4, "", "gh auth login");
            if (args[1] == "list") return Failure == "lookup" ? new(true, 1, "", "lookup failed") :
                Ok(ExistingPr ? """[{"number":9,"url":"https://github.com/octo/sample/pull/9"},{"number":7,"url":"https://github.com/octo/sample/pull/7"}]""" : "[]");
            if (Failure == "create") return new(true, 1, "", "create failed");
            Creates++;
            ExistingPr = true;
            return Ok(Url);
        }
        private static ForgeProcessResult Ok(string text) => new(true, 0, text, "");
    }
}
