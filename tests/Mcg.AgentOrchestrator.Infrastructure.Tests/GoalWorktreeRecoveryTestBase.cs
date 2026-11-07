using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every case owns its Git repository, SQLite database and root mutex.
public abstract class GoalWorktreeRecoveryTestBase : GoalWorktreeTestBase
{
    private protected sealed class MissingFixture : IDisposable
    {
        internal string Repo { get; } = CreateSeededRepository();
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal Goal Goal { get; }
        internal string Path { get; }
        internal string Branch => GoalWorktrees.BranchName(Goal.Id);
        internal string Tip { get; }
        internal string MainBranch { get; }
        internal List<GoalWorktreeRecoveryResult> Receipts { get; } = [];

        internal MissingFixture(bool stale = true, bool uniqueCommit = true)
        {
            // Test probes omit global Git config, while production checkout inherits it.
            // Give both paths the same repository-local line-ending contract.
            RunGit(Repo, "config", "core.autocrlf", "false");
            Goal = Kernel.CreateGoal("Recover fixture workspace", [new(TaskId.New(), "Implement fixture change", AgentRole.Developer)]);
            Kernel.ActivateGoal(Goal.Id, AgentCatalog.Default().Agents);
            Kernel.RecordGoalRefinement(Goal.Id, new RefinedSpec(Goal.Objective, ["Preserve the existing branch"], VerificationClass.TestVerifiable, [], []));
            MainBranch = RunGitOutput(Repo, "branch", "--show-current").Trim();
            // Normal candidate/context preparation needs a manifest restored from Git too.
            var config = System.IO.Path.Combine(Repo, "config");
            Directory.CreateDirectory(config);
            File.WriteAllText(System.IO.Path.Combine(config, "acceptance-manifest.json"), """
                {
                  "version": 1,
                  "engine": { "enforceStructuralCoverage": false },
                  "checks": [
                    { "name": "fixture check", "type": "command", "command": "dotnet", "arguments": ["--info"] }
                  ],
                  "forbiddenChangedPathGlobs": []
                }
                """);
            RunGit(Repo, "add", "config/acceptance-manifest.json");
            RunGit(Repo, "commit", "-m", "Seed recovery acceptance manifest");
            Path = GoalWorktrees.Ensure(Repo, Goal.Id);
            if (uniqueCommit)
            {
                File.WriteAllText(System.IO.Path.Combine(Path, "unique-goal.txt"), "preserved goal work");
                RunGit(Path, "add", "unique-goal.txt");
                RunGit(Path, "commit", "-m", "Unique goal commit");
            }
            Tip = RunGitOutput(Repo, "rev-parse", $"refs/heads/{Branch}").Trim();
            Assert.Equal(string.Empty, RunGitOutput(Path, "status", "--porcelain=v1", "--untracked-files=no"));
            File.WriteAllText(System.IO.Path.Combine(Repo, "new-main.txt"), "later main change");
            RunGit(Repo, "add", "new-main.txt");
            RunGit(Repo, "commit", "-m", "Advance main after goal");
            DeleteDirectory(Path); // Only this fixture's checkout disappears; branch and metadata survive.
            if (!stale) RunGit(Repo, "worktree", "remove", "--force", Path);
        }

        internal GoalWorktreeRecoveryResult Recover(Func<string?>? owner = null, GoalWorktreeCleanupHooks? hooks = null) =>
            GoalWorktrees.RecoverMissing(Repo, Goal.Id, owner ?? (() => null), Receipts.Add, hooks);

        internal GoalEvidenceOperationCoordinator Coordinator() => new(
            new ReconcileSweepRemediationStore(SetUpStateDatabase(System.IO.Path.Combine(Repo, "recovery-state.db"))),
            Repo, TimeSpan.FromMinutes(30), pidProbe: new ConductLockPidProbe());

        internal void AssertTip()
        {
            Assert.Equal(Tip, RunGitOutput(Repo, "rev-parse", $"refs/heads/{Branch}").Trim());
        }

        internal void AssertRestored()
        {
            AssertTip();
            Assert.Equal(Tip, RunGitOutput(Path, "rev-parse", "HEAD").Trim());
            Assert.Equal(Branch, RunGitOutput(Path, "branch", "--show-current").Trim());
            var listing = GoalWorktreeRegistrationListing.Read(Repo, new());
            Assert.True(listing.Succeeded, listing.FailureReason);
            Assert.Single(listing.Records.Where(r => r.Branch == $"refs/heads/{Branch}"));
            Assert.Equal(Path, GoalWorktrees.TryResolve(Repo, Goal.Id));
        }

        public void Dispose() => DeleteDirectory(Repo);
    }
}
