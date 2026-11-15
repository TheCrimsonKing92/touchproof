using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: formatter inputs and each sweep's repository, kernel and stores are private.
public sealed class StartupSweepStepsLineTests : CliCommandTestBase
{
    [Fact]
    public void Format_AllFieldsAppearInContractOrder()
    {
        var id = new GoalId("12345678abcdef");
        var result = new TerminalGoalSweepResult([
            new(id, "12345678", [new("completed-branch-landed-retired", "proof", "retired")], [])])
        {
            OwnedRootDurationMs = 1, GitIndexDurationMs = 2, EvidenceDurationMs = 3,
            EphemeralDurationMs = 4, AttentionDurationMs = 5, MergeEvidenceDurationMs = 6,
            GoalsDurationMs = 7, GitSpawnCount = 8, GoalsSweptCount = 9, TerminalGoalsSweptCount = 10,
            GoalDurations = [(id, 11, 4)]
        };

        Assert.Equal("STARTUP_SWEEP_STEPS total_ms=12 owned_root_ms=1 git_index_ms=2 evidence_ms=3 ephemeral_ms=4 attention_ms=5 merge_evidence_ms=6 goals_ms=7 git_spawns=8 goals_swept=9 terminal_goals_swept=10 landed_branches_retired=1 slowest=12345678:11:4",
            StartupSweepStepsLine.Format(result, 12));
    }

    [Fact]
    public void Format_SevenDurations_SelectsFiveByDescendingTimeThenOrdinalPrefix()
    {
        var result = new TerminalGoalSweepResult([])
        {
            GoalDurations = [
                (new("bbbbbbbb1"), 40, 2), (new("aaaaaaaa1"), 40, 1), (new("eeeeeeee1"), 10, 8),
                (new("dddddddd1"), 20, 0), (new("cccccccc1"), 30, 3), (new("ffffffff1"), 5, 9), (new("gggggggg1"), 1, 7)]
        };

        Assert.Equal("STARTUP_SWEEP_STEPS total_ms=0 owned_root_ms=0 git_index_ms=0 evidence_ms=0 ephemeral_ms=0 attention_ms=0 merge_evidence_ms=0 goals_ms=0 git_spawns=0 goals_swept=0 terminal_goals_swept=0 landed_branches_retired=0 slowest=aaaaaaaa:40:1,bbbbbbbb:40:2,cccccccc:30:3,dddddddd:20:0,eeeeeeee:10:8",
            StartupSweepStepsLine.Format(result, 0));
    }

    [Fact]
    public void Format_NoSweptGoals_UsesNone()
    {
        Assert.Equal("STARTUP_SWEEP_STEPS total_ms=0 owned_root_ms=0 git_index_ms=0 evidence_ms=0 ephemeral_ms=0 attention_ms=0 merge_evidence_ms=0 goals_ms=0 git_spawns=0 goals_swept=0 terminal_goals_swept=0 landed_branches_retired=0 slowest=none",
            StartupSweepStepsLine.Format(new TerminalGoalSweepResult([]), 0));
    }

    [Fact]
    public void Format_RetirementCount_UsesOnlyExactRepairKind()
    {
        var result = new TerminalGoalSweepResult([
            new(new("12345678"), "12345678", [
                new("completed-branch-landed-retired", "proof", "retired"),
                new("completed-branch-superseded", "equivalent", "retired"),
                new("completed-branch-landed-retired-extra", "other", "other")],
                [new("completed-branch-landed-retired", "blocked", "retry")]),
            new(new("23456789"), "23456789", [new("completed-branch-landed-retired", "proof", "retired")], [])]);

        Assert.Equal("STARTUP_SWEEP_STEPS total_ms=0 owned_root_ms=0 git_index_ms=0 evidence_ms=0 ephemeral_ms=0 attention_ms=0 merge_evidence_ms=0 goals_ms=0 git_spawns=0 goals_swept=0 terminal_goals_swept=0 landed_branches_retired=2 slowest=none",
            StartupSweepStepsLine.Format(result, 0));
    }

    [Fact]
    public void GoalDurations_TwoGoalsDifferentGitLaunches_ReportOwnCounts()
    {
        var root = CreateAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var merged = kernel.CreateGoal("Merged timing goal",
                [new TaskSpec(TaskId.New(), "Merged work", AgentRole.Developer)]).Id;
            var noBranch = kernel.CreateGoal("No-branch timing goal",
                [new TaskSpec(TaskId.New(), "No branch work", AgentRole.Developer)]).Id;
            cleanupGoalId = merged;
            kernel = WithGoalStatus(WithGoalStatus(kernel, merged, GoalStatus.Completed), noBranch, GoalStatus.Completed);
            _ = CreateGoalBranchScenario(root, merged, "src/timing.txt", "timed goal work");
            var mergedBranch = GoalWorktrees.BranchName(merged);
            var noBranchName = GoalWorktrees.BranchName(noBranch);
            var branchTip = RunGitOutput(root, "rev-parse", mergedBranch).Trim();
            RunGit(root, "merge", "--no-ff", mergedBranch, "-m", "Timing merge");
            var mergeCommit = RunGitOutput(root, "rev-parse", "HEAD").Trim();
            Assert.NotEqual(branchTip, mergeCommit);
            Assert.Null(GoalOperationJournal.TryGetLatestLandingIntent(GoalOperationJournal.Read(root, merged)));
            Assert.Empty(RunGitOutput(root, "branch", "--list", noBranchName).Trim());
            var commands = new List<string[]>();
            GitCli.GitResult RecordingGitRunner(string directory, IReadOnlyList<string> arguments)
            {
                commands.Add(arguments.ToArray());
                return GoalGitFactIndex.RunGitCli(directory, arguments);
            }

            var result = TerminalGoalSweep.Run(kernel, root, "main", gitRunner: RecordingGitRunner,
                cleanupHooks: CreateIsolatedCleanupContext(OrchestratorWorkspace.ForDirectory(root)).Hooks);

            // The shared history scan also names main's SHA. Only the goal's recovery commands own it.
            var mergedLaunches = commands.Count(args =>
                (args[0] == "rev-parse" && args.Contains($"refs/heads/{mergedBranch}")) ||
                (args[0] == "log" && (args.Contains($"{branchTip}..main") || args.Contains(branchTip))) ||
                (args[0] == "show" && args.Contains(mergeCommit)));
            var noBranchLaunches = commands.Count(args => args.Any(arg => arg.Contains(noBranchName, StringComparison.Ordinal)));
            Assert.Contains(result.Goals, goal => goal.GoalId == merged &&
                goal.Repairs.Any(repair => repair.Kind == "landing-intent-auto-repair"));
            Assert.Equal(2, result.GoalDurations.Count);
            var mergedDuration = Assert.Single(result.GoalDurations, item => item.GoalId == merged);
            var noBranchDuration = Assert.Single(result.GoalDurations, item => item.GoalId == noBranch);
            Assert.True(mergedLaunches > 0);
            Assert.Equal(0, noBranchLaunches);
            Assert.Equal(mergedLaunches, mergedDuration.GitSpawns);
            Assert.Equal(noBranchLaunches, noBranchDuration.GitSpawns);
            Assert.NotEqual(mergedDuration.GitSpawns, noBranchDuration.GitSpawns);
            Assert.True(mergedDuration.GitSpawns < result.GitSpawnCount);
            Assert.True(noBranchDuration.GitSpawns < result.GitSpawnCount);
            Assert.Equal(commands.Count, result.GitSpawnCount);
            var line = StartupSweepStepsLine.Format(result, 0);
            Assert.Contains($"{merged.Value[..8]}:{mergedDuration.ElapsedMs}:{mergedLaunches}", line, StringComparison.Ordinal);
            Assert.Contains($"{noBranch.Value[..8]}:{noBranchDuration.ElapsedMs}:{noBranchLaunches}", line, StringComparison.Ordinal);
        }
        finally { CleanupAcceptanceRepository(root, cleanupGoalId); }
    }

    [Fact]
    public void GoalDurationsHasOneEntryPerSweptGoal()
    {
        var root = CreateAcceptanceRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var ids = Enumerable.Range(0, 3).Select(index => kernel.CreateGoal($"Timing {index}",
                [new TaskSpec(TaskId.New(), "Original task", AgentRole.Developer)]).Id).ToArray();
            foreach (var id in ids)
                kernel = WithGoalStatus(kernel, id, GoalStatus.Completed);
            GoalOperationJournal.RecordLandingIntent(root, kernel.GetGoal(ids[0]), GoalWorktrees.BranchName(ids[0]),
                "main", RunGitOutput(root, "rev-parse", "HEAD").Trim(), "test");
            var snapshot = kernel.ExportSnapshot();
            kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Select(goal => goal.Id == ids[0].Value
                    ? goal with { Tasks = goal.Tasks.Select(task => task with { Description = "Changed task graph" }).ToArray() }
                    : goal).ToArray()
            });

            var result = TerminalGoalSweep.Run(kernel, root, "main",
                cleanupHooks: CreateIsolatedCleanupContext(OrchestratorWorkspace.ForDirectory(root)).Hooks);

            Assert.Contains(result.Goals, goal => goal.GoalId == ids[0] && goal.Blockers.Any(blocker => blocker.Kind == "landing-graph-changed"));
            Assert.Equal(ids.OrderBy(id => id.Value), result.ExplicitlySweptGoalIds.OrderBy(id => id.Value));
            Assert.Equal(result.ExplicitlySweptGoalIds.OrderBy(id => id.Value), result.GoalDurations.Select(item => item.GoalId).OrderBy(id => id.Value));
            Assert.Equal(3, result.GoalDurations.Select(item => item.GoalId).Distinct().Count());
            Assert.Equal(3, result.TerminalGoalsSweptCount);
        }
        finally { CleanupAcceptanceRepository(root, null); }
    }
}
