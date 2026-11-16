using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalGitFactIndexTests
{
    [Xunit.Fact]
    public void BuildBranchQueryFailureMarksEvidenceUnavailable()
    {
        var index = GoalGitFactIndex.Build(
            Environment.CurrentDirectory, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
            (_, _) => new GitCli.GitResult(1, string.Empty, "git unavailable"));

        Assert.False(index.IsAvailable);
    }

    [Xunit.Theory]
    [Xunit.InlineData(0, "- 1111111111111111111111111111111111111111\n- 2222222222222222222222222222222222222222\n", "EquivalentToMain")]
    [Xunit.InlineData(0, "- 1111111111111111111111111111111111111111\n+ 2222222222222222222222222222222222222222\n", "AbsentFromMain")]
    [Xunit.InlineData(1, "", "Inconclusive")]
    [Xunit.InlineData(0, "", "NoCommitsAhead")]
    [Xunit.InlineData(0, "unexpected output", "Inconclusive")]
    [Xunit.InlineData(0, "- not-an-object-id", "Inconclusive")]
    public void ClassifyCherryResult_Output_ReturnsExpectedState(
        int exitCode,
        string output,
        string expected)
    {
        var result = GoalGitFactIndex.ClassifyCherryResult(new GitCli.GitResult(exitCode, output, string.Empty));

        Assert.Equal(expected, result.ToString());
    }

    [Xunit.Fact]
    public void BuildGoalBranchFacts_WithoutMainSha_IsInconclusiveWithoutRunningCherry()
    {
        var goal = CreateGoalWithStatus("Main ref is unavailable", GoalStatus.Verified);
        var branch = GoalWorktrees.BranchName(goal.Id);
        var index = new GoalGitFactIndex(
            Environment.CurrentDirectory,
            isGitWorkTree: true,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [branch] = "1111111111111111111111111111111111111111"
            },
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            mainSha: null,
            gitRunner: (_, _) => throw new Xunit.Sdk.XunitException("git cherry must not run without a main SHA"));

        var facts = index.BuildGoalBranchFacts(goal);

        Assert.Equal(GoalBranchContentState.Inconclusive, facts.ContentState);
    }

    [Xunit.Fact]
    public void BuildReplacementFacts_BranchAtMainTipIsNotLanded()
    {
        var goal = CreateGoalWithStatus("Zero-commit replacement branch", GoalStatus.Cancelled);
        var branch = GoalWorktrees.BranchName(goal.Id);
        var mainSha = "1111111111111111111111111111111111111111";
        var index = new GoalGitFactIndex(
            Environment.CurrentDirectory,
            isGitWorkTree: true,
            new Dictionary<string, string>(StringComparer.Ordinal) { [branch] = mainSha },
            new HashSet<string>(StringComparer.Ordinal) { branch },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            mainSha,
            gitRunner: (_, args) =>
                string.Join(" ", args).StartsWith("cherry ", StringComparison.Ordinal)
                    ? new GitCli.GitResult(0, string.Empty, string.Empty)
                    : new GitCli.GitResult(1, string.Empty, "unexpected git command"));

        var facts = index.BuildReplacementFacts(goal);

        Assert.False(facts.BranchAlreadyLanded);
        Assert.Equal(GoalBranchContentState.NoCommitsAhead, facts.ContentState);
    }

    [Xunit.Fact]
    public void BuildReplacementFacts_ZeroCommitBranchAtOlderMainAncestorIsNotLanded()
    {
        var goal = CreateGoalWithStatus("Historical zero-commit replacement branch", GoalStatus.Failed);
        var branch = GoalWorktrees.BranchName(goal.Id);
        var branchSha = "1111111111111111111111111111111111111111";
        var mainSha = "2222222222222222222222222222222222222222";
        var index = new GoalGitFactIndex(
            Environment.CurrentDirectory,
            isGitWorkTree: true,
            new Dictionary<string, string>(StringComparer.Ordinal) { [branch] = branchSha },
            new HashSet<string>(StringComparer.Ordinal) { branch },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            mainSha,
            gitRunner: (_, args) =>
                string.Join(" ", args).StartsWith("cherry ", StringComparison.Ordinal)
                    ? new GitCli.GitResult(0, string.Empty, string.Empty)
                    : new GitCli.GitResult(1, string.Empty, "unexpected git command"));

        var facts = index.BuildReplacementFacts(goal);

        Assert.False(facts.BranchAlreadyLanded);
        Assert.Equal(GoalBranchContentState.NoCommitsAhead, facts.ContentState);
    }

    [Xunit.Fact]
    public void ReplacementEvidence_InconclusiveBranchContent_FailsClosedWithoutInventingDelta()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-goal-replacement-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var goal = CreateGoalWithStatus("Inconclusive replacement evidence", GoalStatus.Cancelled);
        var branch = GoalWorktrees.BranchName(goal.Id);
        try
        {
            GitCli.GitResult GitRunner(string _, IReadOnlyList<string> args) => string.Join(" ", args) switch
            {
                "for-each-ref --format=%(refname:lstrip=2) %(objectname) refs/heads/goal/ refs/heads/main" =>
                    new GitCli.GitResult(0, $"{branch} 1111111111111111111111111111111111111111\nmain 2222222222222222222222222222222222222222\n", string.Empty),
                "for-each-ref --format=%(refname:short) --merged HEAD refs/heads/goal/" =>
                    new GitCli.GitResult(0, string.Empty, string.Empty),
                "worktree list --porcelain" => new GitCli.GitResult(0, string.Empty, string.Empty),
                "rev-parse --verify refs/heads/main" =>
                    new GitCli.GitResult(0, "2222222222222222222222222222222222222222\n", string.Empty),
                var command when command.StartsWith("cherry ", StringComparison.Ordinal) =>
                    new GitCli.GitResult(1, string.Empty, "classification unavailable"),
                var command => new GitCli.GitResult(1, string.Empty, $"unexpected git command: {command}")
            };

            var facts = GoalReplacementEvidence.Capture(workspace, goal, GitRunner);

            Assert.False(facts.IsGitEvidenceAvailable);
            Assert.False(facts.HasRepositoryDelta);
            Assert.Equal(
                GoalReplacementOutcome.ProtectedOwner,
                SourceBacklogClaimEligibility.Evaluate(GoalReplacementDisposition.ZeroWorkCorrection, facts));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void BuildGoalEvidenceKey_DoesNotProbeBranchContentThatCannotBeCached()
    {
        var goal = CreateGoalWithStatus("Evidence key stays cheap", GoalStatus.Completed);
        var branch = GoalWorktrees.BranchName(goal.Id);
        var index = new GoalGitFactIndex(
            Environment.CurrentDirectory,
            isGitWorkTree: true,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [branch] = "2222222222222222222222222222222222222222"
            },
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            mainSha: "3333333333333333333333333333333333333333",
            gitRunner: (_, _) => throw new Xunit.Sdk.XunitException("evidence-key construction must not run git cherry"));

        var key = index.BuildGoalEvidenceKey(goal);

        Assert.DoesNotContain("main=", key, StringComparison.Ordinal);
        Assert.DoesNotContain("content=", key, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "GoalGitFactIndex_parse_branch_tips_keeps_present_and_ignores_malformed_lines")]
    public void GoalGitFactIndexParseBranchTipsKeepsPresentAndIgnoresMalformedLines()
    {
        var tips = GoalGitFactIndex.ParseBranchTips(
            """
            goal/present 1111111111111111111111111111111111111111
            goal/missing-object
            goal/blank-object
            goal/extra-object 2222222222222222222222222222222222222222 trailing
            goal/also-present abcdefabcdefabcdefabcdefabcdefabcdefabcd
            """);

        Assert.Equal("1111111111111111111111111111111111111111", tips["goal/present"]);
        Assert.Equal("abcdefabcdefabcdefabcdefabcdefabcdefabcd", tips["goal/also-present"]);
        Assert.False(tips.ContainsKey("goal/missing-object"));
        Assert.False(tips.ContainsKey("goal/blank-object"));
        Assert.False(tips.ContainsKey("goal/extra-object"));
    }

    [Xunit.Fact(DisplayName = "GoalIntegrationEvidenceResolver_subject_without_main_ancestry_is_not_landed")]
    public void GoalIntegrationEvidenceResolverSubjectWithoutMainAncestryIsNotLanded()
    {
        var goalId = new GoalId("aaaaaaaa111111111111111111111111");
        GitCli.GitResult GitRunner(string _, IReadOnlyList<string> args)
        {
            var command = string.Join(" ", args);
            return command switch
            {
                "log main-sha --format=%H%x09%s --grep=^Integrate goal/" =>
                    new GitCli.GitResult(0, $"discarded-sha\tIntegrate goal/{goalId.Value[..8]}\n", string.Empty),
                "merge-base --is-ancestor discarded-sha main-sha" =>
                    new GitCli.GitResult(1, string.Empty, string.Empty),
                _ => new GitCli.GitResult(1, string.Empty, $"unexpected git command: {command}")
            };
        }

        var resolver = GoalIntegrationEvidenceResolver.Build(Environment.CurrentDirectory, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, "main-sha", GitRunner);

        Assert.False(resolver.TryResolve(goalId, out var evidence));
        Assert.Null(evidence);
    }

    [Xunit.Fact(DisplayName = "GoalGitFactIndex_build_reports_present_and_missing_goal_branches_from_batch_query")]
    public void GoalGitFactIndexBuildReportsPresentAndMissingGoalBranchesFromBatchQuery()
    {
        GitCli.GitResult GitRunner(string _, IReadOnlyList<string> args)
        {
            var command = string.Join(" ", args);
            return command switch
            {
                "for-each-ref --format=%(refname:lstrip=2) %(objectname) refs/heads/goal/ refs/heads/main" =>
                    new GitCli.GitResult(
                        0,
                        "goal/present 1111111111111111111111111111111111111111\ngoal/malformed 222 trailing\n",
                        string.Empty),
                "for-each-ref --format=%(refname:short) --merged HEAD refs/heads/goal/" =>
                    new GitCli.GitResult(0, string.Empty, string.Empty),
                "worktree list --porcelain" =>
                    new GitCli.GitResult(0, string.Empty, string.Empty),
                _ => new GitCli.GitResult(1, string.Empty, "unexpected git command")
            };
        }

        var index = GoalGitFactIndex.Build(Environment.CurrentDirectory, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, GitRunner);

        Assert.True(index.HasGoalBranch("goal/present"));
        Assert.False(index.HasGoalBranch("goal/missing"));
        Assert.False(index.HasGoalBranch("goal/malformed"));
    }

    [Xunit.Theory]
    [Xunit.InlineData(0, "", "NoCommitsAhead")]
    [Xunit.InlineData(0, "- 1111111111111111111111111111111111111111\n", "EquivalentToMain")]
    [Xunit.InlineData(1, "", "Inconclusive")]
    [Xunit.InlineData(0, "malformed", "Inconclusive")]
    public void BuildGoalBranchFacts_OtherCherryStates_DoNotRunDetector(int exitCode, string output, string expected)
    {
        var goal = CreateGoalWithStatus("Cherry result is final", GoalStatus.Verified);
        var calls = 0;
        var index = CreateContentIndex(goal, (_, args) =>
        {
            calls++;
            Assert.Equal("cherry", args[0]);
            return new GitCli.GitResult(exitCode, output, "");
        });

        Assert.Equal(expected, index.BuildGoalBranchFacts(goal).ContentState.ToString());
        Assert.Equal(1, calls);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void BuildBranchFacts_CompletedOrReplacement_DoesNotRunDetector(bool replacement)
    {
        var goal = CreateGoalWithStatus("Cherry-only lookup", replacement ? GoalStatus.Verified : GoalStatus.Completed);
        var calls = 0;
        var index = CreateContentIndex(goal, (_, args) =>
        {
            calls++;
            Assert.Equal("cherry", args[0]);
            return new GitCli.GitResult(0, $"+ {new string('2', 40)}\n", "");
        });

        var facts = replacement ? index.BuildReplacementFacts(goal) : index.BuildGoalBranchFacts(goal);
        Assert.Equal(GoalBranchContentState.AbsentFromMain, facts.ContentState);
        Assert.Equal(1, calls);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void BuildGoalBranchFacts_TreePromotion_DoesNotLeakToCherryOnlyLookups(bool cherryOnlyFirst)
    {
        var goal = CreateGoalWithStatus("Verified tree promotion", GoalStatus.Verified);
        var completed = Goal.FromSnapshot(goal.ToSnapshot() with { Status = GoalStatus.Completed });
        var cache = new GoalBranchContentStateCache();
        var commands = new List<string>();
        var branch = GoalWorktrees.BranchName(goal.Id);
        var tip = new string('2', 40);
        var main = new string('1', 40);
        var baseSha = new string('3', 40);
        GitCli.GitResult Runner(string _, IReadOnlyList<string> args)
        {
            commands.Add(args[0]);
            if (args[0] == "cherry") return new(0, $"+ {tip}\n", "");
            if (args[0] == "merge-base")
            {
                Assert.Equal(new[] { "merge-base", "--all", main, tip }, args.ToArray());
                return new(0, baseSha + "\n", "");
            }
            Assert.Equal("diff-tree", args[0]);
            Assert.Equal(tip, args[^1]);
            return new(0, args[^2] == baseSha
                ? $":100644 100644 {main} {tip} M\0path\0" : "", "");
        }
        var index = CreateContentIndex(goal, Runner, cache);
        if (cherryOnlyFirst)
        {
            Assert.Equal(GoalBranchContentState.AbsentFromMain, index.BuildReplacementFacts(goal).ContentState);
            Assert.Equal(GoalBranchContentState.AbsentFromMain, index.BuildGoalBranchFacts(completed).ContentState);
        }

        Assert.Equal(GoalBranchContentState.EquivalentToMain, index.BuildGoalBranchFacts(goal).ContentState);
        Assert.Equal(GoalBranchContentState.EquivalentToMain, index.BuildGoalBranchFacts(goal).ContentState);
        Assert.Equal(1, commands.Count(command => command == "merge-base"));
        Assert.Equal(GoalBranchContentState.AbsentFromMain, index.BuildGoalBranchFacts(completed).ContentState);
        Assert.Equal(GoalBranchContentState.AbsentFromMain, index.BuildReplacementFacts(goal).ContentState);
        Assert.True(cache.TryGet(main, branch, tip, out var shared));
        Assert.Equal(GoalBranchContentState.AbsentFromMain, shared);

        var priorCalls = commands.Count;
        var next = CreateContentIndex(goal, (_, _) => throw new Xunit.Sdk.XunitException("Cherry cache should suffice"), cache);
        Assert.Equal(GoalBranchContentState.AbsentFromMain, next.BuildGoalBranchFacts(completed).ContentState);
        Assert.Equal(GoalBranchContentState.AbsentFromMain, next.BuildReplacementFacts(goal).ContentState);
        Assert.Equal(priorCalls, commands.Count);
    }

    [Xunit.Fact]
    public void BuildGoalBranchFacts_DetectorFailure_KeepsAbsentCherryResult()
    {
        var goal = CreateGoalWithStatus("Unavailable comparison", GoalStatus.Verified);
        var commands = new List<string>();
        var index = CreateContentIndex(goal, (_, args) =>
        {
            commands.Add(args[0]);
            return args[0] == "cherry"
                ? new(0, $"+ {new string('2', 40)}\n", "")
                : new(1, "", "comparison unavailable");
        });

        Assert.Equal(GoalBranchContentState.AbsentFromMain, index.BuildGoalBranchFacts(goal).ContentState);
        Assert.Equal(new[] { "cherry", "merge-base" }, commands.ToArray());
    }

    private static GoalGitFactIndex CreateContentIndex(Goal goal,
        Func<string, IReadOnlyList<string>, GitCli.GitResult> runner, GoalBranchContentStateCache? cache = null) =>
        new(Path.GetTempPath(), true,
            new Dictionary<string, string> { [GoalWorktrees.BranchName(goal.Id)] = new('2', 40) },
            new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new string('1', 40), gitRunner: runner, branchContentStateCache: cache);

    private static Goal CreateGoalWithStatus(string objective, GoalStatus status)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            objective,
            [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals
                .Select(item => item.Id == goal.Id.Value ? item with { Status = status } : item)
                .ToArray()
        });
        return kernel.GetGoal(goal.Id);
    }
}
