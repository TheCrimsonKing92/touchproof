using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Real Git/SQLite contract fixture: every instance owns its repository, worktree and stores.
internal sealed class GoalBranchCommitTestFixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "mcg-branch-commit", Guid.NewGuid().ToString("N"));
    internal AgentOrchestratorKernel Kernel { get; private set; } = new();
    internal GoalId GoalId { get; }
    internal Goal Goal => Kernel.GetGoal(GoalId);
    internal OrchestratorWorkspace Workspace { get; }
    internal SqliteOrchestratorStateRepository Repository { get; }
    internal SqliteOperatorIntentStore Store { get; }
    internal string Worktree { get; }
    internal string PreviousHead { get; }
    internal string Head => Git("rev-parse", "HEAD");
    internal string ResumeCommand => $"goal-branch-commit {GoalId.Value} --resume --project=default";

    internal GoalBranchCommitTestFixture()
    {
        Directory.CreateDirectory(Root);
        RunGit(Root, "init", "-b", "main");
        RunGit(Root, "config", "user.email", "branch-commit@example.invalid");
        RunGit(Root, "config", "user.name", "Branch Commit Tests");
        RunGit(Root, "config", "commit.gpgsign", "false");
        RunGit(Root, "config", "core.autocrlf", "false");
        var hooks = Path.Combine(Root, ".git", "empty-hooks");
        Directory.CreateDirectory(hooks);
        RunGit(Root, "config", "core.hooksPath", hooks);
        File.WriteAllText(Path.Combine(Root, "seed.txt"), "original\n");
        RunGit(Root, "add", "seed.txt");
        RunGit(Root, "commit", "-m", "Seed");
        Workspace = OrchestratorWorkspace.ForDirectory(Root);
        StateDbMigrations.EnsureUpToDate(Workspace.SqliteStatePath);
        Repository = new SqliteOrchestratorStateRepository(Workspace.SqliteStatePath);
        var goal = Kernel.CreateGoal("Commit and rebind", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        GoalId = goal.Id;
        Kernel.ActivateGoal(GoalId, AgentCatalog.Default().Agents);
        Kernel.RecordGoalRefinement(GoalId, new RefinedSpec("Five criteria",
            Enumerable.Range(1, 5).Select(n => $"Requirement {n}").ToArray(), VerificationClass.TestVerifiable, [], []));
        Worktree = GoalWorktrees.Ensure(Root, GoalId, trunkBranch: "main");
        PreviousHead = Head;
        for (var index = 0; index < 5; index++)
            Kernel.MapCriterionEvidenceOwner(GoalId, index, 1,
                index == 3 ? CriterionEvidenceOwner.Operator : CriterionEvidenceOwner.Acceptance,
                "test operator", index == 3 ? "operator:observation" : CriterionEvidenceScopes.FullAcceptanceGate,
                $"original-{index}", index == 2 ? new string('c', 40) : PreviousHead);
        // Legacy unbound obligations can be restored, but cannot be created by a mapping intent.
        var snapshot = Kernel.ExportGoalSnapshot(GoalId);
        Kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [snapshot with { CriterionEvidenceObligations = snapshot.CriterionEvidenceObligations!
                .Select(duty => duty.CriterionIndex == 4 ? duty with { ExpectedCandidateSha = null } : duty).ToArray() }], []));
        Save();
        Store = SqliteOperatorIntentStore.ForDirectories(Workspace.OrchestratorDirectory, Workspace.LogDirectory);
        Assert.Equal(GoalWorktrees.BranchName(GoalId), Git("branch", "--show-current"));
        Assert.Equal(PreviousHead, Goal.CriterionEvidenceObligations.Single(d => d.CriterionIndex == 0).ExpectedCandidateSha);
        Assert.Null(Goal.CriterionEvidenceObligations.Single(d => d.CriterionIndex == 4).ExpectedCandidateSha);
    }

    internal void StageChange()
    {
        File.WriteAllText(Path.Combine(Worktree, "change.txt"), "operator change\n");
        Git("add", "change.txt");
        Assert.Equal("change.txt", Git("diff", "--cached", "--name-only"));
        Assert.Equal("", Git("diff", "--name-only"));
    }

    internal (int ExitCode, string Output, string Error) Run(bool resume = false, IOperatorIntentStore? store = null)
    {
        string[] args = resume ? ["goal-branch-commit", GoalId.Value[..8], "--resume"]
            : ["goal-branch-commit", GoalId.Value[..8], "--message", "Operator test fix"];
        var exitCode = -1;
        var output = "";
        var error = AsyncLocalConsoleRouter.Error.CaptureLocal(() =>
            output = AsyncLocalConsoleRouter.Capture(() =>
                exitCode = CliGoalBranchCommitCommand.Run(args, Workspace, store ?? Store)));
        return (exitCode, output, error);
    }

    internal void Save() => Repository.SaveAsync(Kernel).GetAwaiter().GetResult();

    internal string StateJson() => JsonSerializer.Serialize(Repository.LoadAsync().GetAwaiter().GetResult().ExportSnapshot());

    internal OperatorIntentRecord[] Intents() => Store.ListForGoalAsync(GoalId.Value, int.MaxValue).GetAwaiter().GetResult().ToArray();

    internal void ApplyPending()
    {
        Kernel = Repository.LoadAsync().GetAwaiter().GetResult();
        var coordinator = new OperatorIntentCoordinator(Store, goalHeadResolver: _ => Head);
        for (var tick = 0; tick < 10; tick++)
        {
            var pending = Intents().Where(intent => intent.Status is OperatorIntentStatus.Pending or OperatorIntentStatus.Claimed).ToArray();
            if (pending.Length == 0)
            {
                Assert.All(Intents(), intent => Assert.Equal(OperatorIntentStatus.Applied, intent.Status));
                return;
            }
            Assert.True(coordinator.ExecutePending(Kernel, Goal).MutatedGoalState, "Queued intents were not applied.");
            Save();
            coordinator.CompletePersisted([GoalId]);
        }
        Assert.Fail("Operator intents did not finish after ten coordinator ticks.");
    }

    internal string Git(params string[] args) => RunGit(Worktree, args);

    private static string RunGit(string directory, params string[] args)
    {
        var result = GitCli.RunForRepository(directory, args);
        Assert.True(result.Succeeded, $"git {string.Join(' ', args)} failed: {result.Error}");
        return result.Output.Trim();
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(Root, recursive: true);
    }
}
