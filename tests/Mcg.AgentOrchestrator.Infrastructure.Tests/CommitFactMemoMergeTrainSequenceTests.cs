using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: owns every repository, ref and worktree; observation is thread-local.
public sealed class CommitFactMemoMergeTrainSequenceTests(ITestOutputHelper output) : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void CreateThenRecheck_ReportsRepeatAndLaunchCounts()
    {
        var root = CreateAcceptanceCohortRepository();
        try
        {
            var first = GoalId.New();
            _ = CreateWorktreeCandidate(root, first, "candidate.txt", "candidate\n");
            var memberWorktree = GoalWorktrees.Ensure(root, first);
            var landed = GoalId.New();
            _ = CreateWorktreeCandidate(root, landed, "landed.txt", "landed\n");
            RunGit(root, "merge", "--ff-only", GoalWorktrees.BranchName(landed));
            RunGit(memberWorktree, "merge", "--no-ff", "--no-edit", "main");
            var second = GoalId.New();
            var secondId = CreateWorktreeCandidate(root, second, "other.txt", "other\n");
            var main = RunGitOutput(root, "rev-parse", "main");
            MergeTrainMemberBinding[] members =
            [
                TrainBind(first, RunGitOutput(memberWorktree, "rev-parse", "HEAD"), "candidate.txt", "first"),
                TrainBind(second, secondId, "other.txt", "second")
            ];
            var seen = new HashSet<string>(GoalWorktrees.CommitFactKeyComparer);
            var repeatCount = 0;
            var launchCount = 0;
            string? priorTree = null;
            for (var pass = 0; pass < 2; pass++)
            {
                string? tree = null;
                var launches = CommitFactMemoRepository.Capture(() =>
                {
                    _ = LandingExecutor.LoadLandingDenylist(root, main);
                    using var train = GoalWorktrees.CreateMergeTrainWorkspace(
                        root, main, members, TrunkBranchName.Default);
                    Assert.Empty(train.Ejections);
                    Assert.Equal(members.Select(member => member.GoalId), train.Members.Select(member => member.GoalId));
                    tree = train.TreeRevision;
                });
                launchCount += launches.Count;
                if (priorTree is not null) Assert.Equal(priorTree, tree);
                priorTree = tree;

                Record("denylist", main);
                // A cache hit has no launch to observe. Each executed rebase proves the two
                // preceding fact lookups ran; its --onto arguments give the actual merge base.
                // Binding order is asserted above, so keys include hits without estimating them
                // from the missing git launches or adding product telemetry.
                var rebases = launches.Where(args => args.Contains("rebase") && args.Contains("--onto")).ToArray();
                Assert.Equal(members.Length, rebases.Length);
                for (var index = 0; index < members.Length; index++)
                {
                    var mergeBase = rebases[index][^2];
                    Assert.True(GoalWorktrees.IsFullObjectId(mergeBase));
                    Record("merge-base", main, members[index].CandidateRevision);
                    Record("merge-commits", mergeBase, members[index].CandidateRevision);
                }
                output.WriteLine($"pass={pass + 1}; git_launches={launches.Count}");
            }
            output.WriteLine("fixture=CommitFactMemoMergeTrainSequenceTests.CreateThenRecheck_ReportsRepeatAndLaunchCounts; " +
                $"eligible_repeat_count={repeatCount}; total_git_launch_count={launchCount}");

            void Record(string family, string firstId, string? secondId = null)
            {
                Assert.True(GoalWorktrees.IsFullObjectId(firstId));
                Assert.True(secondId is null || GoalWorktrees.IsFullObjectId(secondId));
                if (!seen.Add(family + '\0' + GoalWorktrees.CommitFactKey(root, firstId, secondId))) repeatCount++;
            }
        }
        finally { DeleteDirectory(root); }
    }
}
