using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Each invocation binds the real CLI to the fixture's own repository, registry and state.
public sealed class BacklogIntakeContinuationCliTests : CliCommandTestBase
{
    [Fact]
    public async Task Intake_ReopenedItem_ReportsOneSuccessor()
    {
        var root = CreateShortAcceptanceRepository();
        GoalId? predecessorId = null;
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var backlog = new BacklogStore(workspace.BacklogStorePath);
            var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            var claims = new SourceBacklogClaimStore(workspace.SqliteStatePath);
            var item = await backlog.AddAsync("Exercise the reopened continuation CLI");
            await Run(workspace, "backlog-intake", item.Id, "--create-goal", "--pipeline", "developer-reviewer",
                "--backlog-coverage", "slice");
            var kernel = await repository.LoadAsync();
            var predecessor = Assert.Single(kernel.Goals);
            predecessorId = predecessor.Id;
            foreach (var task in predecessor.Tasks)
            {
                kernel.ReportTaskProgress(predecessor.Id, task.Id, WorkTaskStatus.Completed, "Fixture task complete.");
                kernel.RecordTaskVerification(predecessor.Id, task.Id,
                    new TaskVerificationRecord("fixture-check", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            }
            Assert.Equal(GoalStatus.Verified, predecessor.Status);
            await repository.SaveAsync(kernel);
            CommitGoalWork(root, predecessor.Id, "slice-result.txt", "First slice result");
            RunGit(root, "merge", "--ff-only", GoalWorktrees.BranchName(predecessor.Id));
            await Run(workspace, "goal-mark-landed", predecessor.Id.Value, "--confirm-goal-mark-landed");
            Assert.Equal(GoalStatus.Completed, (await repository.LoadAsync()).GetGoal(predecessor.Id).Status);
            // Slice landings can leave work open. Record the dismissal and reopen it through the CLI.
            if ((await backlog.GetByExactIdAsync(item.Id))?.Status == BacklogItemStatus.Open)
                Assert.True(await backlog.TryCloseByIdAsync(item.Id, "First slice landed"));
            await Run(workspace, "backlog-reopen", item.Id, "Continue the next slice");
            Assert.Equal(BacklogItemStatus.Open, (await backlog.GetByExactIdAsync(item.Id))?.Status);
            var originalClaim = claims.ResolveClaim(await repository.LoadAsync(), item.Id)!;
            string[] command = ["backlog-intake", item.Id, "--create-goal", "--pipeline", "scout",
                "--backlog-coverage", "slice", "--force-reclaim"];

            var output = await Run(workspace, command);

            var state = await repository.LoadAsync();
            Assert.Equal(2, state.Goals.Count);
            var successor = Assert.Single(state.Goals, goal => goal.Id != predecessor.Id);
            var lineage = Assert.Single(claims.ListContinuationLineage(item.Id));
            Assert.Equal(predecessor.Id.Value, lineage.PredecessorGoalId);
            Assert.Equal(successor.Id.Value, lineage.SuccessorGoalId);
            Assert.Equal(originalClaim.Version + 1, lineage.ClaimVersionAfter);
            Assert.Contains($"BACKLOG_INTAKE_CONTINUED backlogItem={item.Id} predecessor={predecessor.Id.Value} " +
                $"successor={successor.Id.Value} claimVersionBefore={originalClaim.Version} " +
                $"claimVersionAfter={lineage.ClaimVersionAfter} transferredAt={lineage.TransferredAt:O}", output);
            Assert.Contains(item.Title, await Run(workspace, "backlog-list"));
            Assert.Contains(successor.Id.Value[..8], await Run(workspace, "goals"));
            var claim = claims.ResolveClaim(state, item.Id);
            Assert.Equal(successor.Id.Value, claim?.OwnerGoalId);
            var records = new BacklogIntakeRecordStore(workspace.SqliteStatePath);
            var completed = records.Get(item.Id);
            Assert.Equal(successor.Id.Value, completed?.GoalId);
            Assert.Equal("GoalCreated", completed?.Status);

            var replay = await Run(workspace, command);

            Assert.Contains($"successor={successor.Id.Value}", replay);
            var replayState = await repository.LoadAsync();
            Assert.Equal(state.Goals.Select(goal => goal.Id), replayState.Goals.Select(goal => goal.Id));
            Assert.Equal(lineage, Assert.Single(claims.ListContinuationLineage(item.Id)));
            Assert.Equal(claim, claims.ResolveClaim(replayState, item.Id));
            Assert.Equal(completed, records.Get(item.Id));
        }
        finally
        {
            CleanupAcceptanceRepository(root, predecessorId);
        }
    }

    private static async Task<string> Run(OrchestratorWorkspace workspace, params string[] args)
    {
        var root = workspace.ExecutionDirectory;
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(CliArgumentParser).Assembly.Location);
        foreach (var arg in (string[])["--repository-root", root, "--expected-state-directory",
                     workspace.OrchestratorDirectory, "--disposable-fixture", .. args])
            start.ArgumentList.Add(arg);
        start.Environment.Clear();
        foreach (var name in new[]
        {
            "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "USERPROFILE", "LOCALAPPDATA",
            "APPDATA", "ProgramFiles", "ProgramFiles(x86)", "DOTNET_ROOT", "DOTNET_ROOT(x86)"
        })
        {
            if (Environment.GetEnvironmentVariable(name) is { } value)
                start.Environment[name] = value;
        }
        start.Environment[OrchestratorProjectRegistry.RegistryHomeEnvironmentVariable] = Path.Combine(root, "registry");
        start.Environment[OrchestratorDataRoot.EnvironmentVariable] = Path.Combine(root, "data");
        start.Environment["OLLAMA_BASE_URL"] = "http://127.0.0.1:1";
        var result = await CliChildProcessRunner.RunAsync(start);
        Assert.True(result.ExitCode == 0, $"{string.Join(' ', args)} exited {result.ExitCode}: {result.StandardError}");
        return result.StandardOutput;
    }
}
