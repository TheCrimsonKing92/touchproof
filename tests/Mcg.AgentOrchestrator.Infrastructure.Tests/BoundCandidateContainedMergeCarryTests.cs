using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;
using static LandingExecutorTests;

// Parallel-safe: every case owns its repository and kernel and changes no process-global state.
public sealed class BoundCandidateContainedMergeCarryTests : HostCapacityBoundTestBase
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void ContainedConflictMerge_BoundThenCleanIntegration_CarriesForward(bool bindAtMerge)
    {
        using var fixture = CreateFixture(bindAtMerge: bindAtMerge);
        var integrationBase = ReadGit(fixture.Repository, "merge-base", "main", fixture.Head);
        Assert.False(GoalWorktrees.TryVerifyIntegrationMergesClean(
            fixture.Repository, integrationBase, fixture.Head, out var refusal));
        Assert.Equal($"integration-merge-not-clean; merge={fixture.ConflictMerge}", refusal);
        Assert.Equal("", ReadGit(fixture.Repository, "diff", fixture.Bound, fixture.Head,
            "--", "src/Help.cs", "src/bound.txt"));

        AssertCarriedByRangeDiff(fixture);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void ConflictMergeAfterBound_Refuses_NamingMergeAndEvaluatedHead(bool landingRebind)
    {
        using var fixture = CreateFixture(conflictAfterBound: true);
        Assert.NotEqual(fixture.ConflictMerge, fixture.Head);
        AssertRefused(fixture, landingRebind);
    }

    [Xunit.Fact]
    public void NonAncestorBound_ContainedConflictMerge_StillRefuses()
    {
        using var fixture = CreateFixture(nonAncestorBound: true);
        var ancestry = GitCli.Run(fixture.Repository, "merge-base", "--is-ancestor", fixture.Bound, fixture.Head);
        Assert.True(ancestry.ProcessStarted);
        Assert.False(ancestry.DrainTimedOut);
        Assert.Equal(1, ancestry.ExitCode);
        AssertRefused(fixture);
    }

    [Xunit.Fact]
    public void MainSideConflictMerge_ArrivingByCleanIntegration_CarriesForward()
    {
        var repository = CreateGitRepository();
        try
        {
            ReadGit(repository, "config", "core.autocrlf", "false");
            AppendCommit(repository, "src/conflict.txt", "a\nb\nc\nd");
            ReadGit(repository, "checkout", "-b", "candidate");
            AppendCommit(repository, "src/goal.txt", "goal owned line");
            var bound = ReadGit(repository, "rev-parse", "HEAD");
            ReadGit(repository, "checkout", "-b", "main-topic", "main");
            AppendCommit(repository, "src/conflict.txt", "a\nb-topic\nc\nd");
            ReadGit(repository, "checkout", "main");
            AppendCommit(repository, "src/conflict.txt", "a\nb\nc-main\nd");
            var merging = GitCli.Run(repository, "merge", "--no-ff", "main-topic");
            Assert.True(merging.ProcessStarted);
            Assert.False(merging.DrainTimedOut);
            Assert.Equal(1, merging.ExitCode);
            Assert.Contains("<<<<<<<", File.ReadAllText(Path.Combine(repository, "src/conflict.txt")),
                StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(repository, "src/conflict.txt"), "a\nb-topic\nc-main\nd\n");
            ReadGit(repository, "add", "src/conflict.txt");
            ReadGit(repository, "commit", "-m", "Resolve main-side conflict");
            var conflictMerge = ReadGit(repository, "rev-parse", "HEAD");
            var (kernel, goal) = Bind(bound);
            var head = IntegrateMainCleanly(repository, "candidate");

            // Literal bound..head includes this conflict; the integration base must remain excluded.
            Assert.Contains(conflictMerge, ReadGit(repository, "rev-list", "--merges", $"{bound}..{head}"),
                StringComparison.Ordinal);
            var fixture = new Fixture(repository, kernel, goal, bound, head, conflictMerge);
            AssertCarriedByRangeDiff(fixture);
        }
        finally { TryDeleteDirectory(repository); }
    }

    private static void AssertCarriedByRangeDiff(Fixture fixture)
    {
        var diagnostic = AcceptanceCriterionEvidence.RecordAndDescribeOutstanding(
            fixture.Goal, fixture.Head, fixture.Kernel, "main", fixture.Repository);
        Assert.Null(diagnostic);
        var obligation = Assert.Single(fixture.Goal.CriterionEvidenceObligations);
        Assert.Equal(CriterionEvidenceState.Satisfied, obligation.State);
        Assert.Equal(fixture.Head, obligation.ExpectedCandidateSha);
        Assert.Equal(fixture.Head, obligation.CandidateSha);
        Assert.Equal($"full-acceptance:{fixture.Head}", obligation.ReceiptId);
        var entry = Assert.Single(GoalOperationJournal.Read(fixture.Repository, fixture.Goal.Id).Entries,
            item => item.Operation == "conductor:criterion-evidence-patch-carry");
        // Success alone also passes through the pre-existing goal-owned-lines fallback.
        Assert.Contains("range-diff", entry.Detail, StringComparison.Ordinal);
    }

    private static void AssertRefused(Fixture fixture, bool landingRebind = false)
    {
        var diagnostic = landingRebind
            ? AcceptanceCriterionEvidence.RebindRecordAndDescribeOutstanding(
                fixture.Goal, fixture.Head, fixture.Kernel, "test landing gate", "main", fixture.Repository)
            : AcceptanceCriterionEvidence.RecordAndDescribeOutstanding(
                fixture.Goal, fixture.Head, fixture.Kernel, "main", fixture.Repository);
        Assert.Contains($"reason=integration-merge-not-clean; merge={fixture.ConflictMerge}; " +
            $"head={fixture.Head}; bound={fixture.Bound}", diagnostic, StringComparison.Ordinal);
        var obligation = Assert.Single(fixture.Goal.CriterionEvidenceObligations);
        Assert.Equal(CriterionEvidenceState.Pending, obligation.State);
        Assert.Equal(fixture.Bound, obligation.ExpectedCandidateSha);
        Assert.Null(obligation.ReceiptId);
        Assert.DoesNotContain(GoalOperationJournal.Read(fixture.Repository, fixture.Goal.Id).Entries,
            item => item.Operation == "conductor:criterion-evidence-patch-carry");
    }

    private static Fixture CreateFixture(bool conflictAfterBound = false,
        bool nonAncestorBound = false, bool bindAtMerge = false)
    {
        var conflict = GoalOwnedLinesPatchEquivalenceTests.CreateConflictFixture(conflictAfterBound);
        var repository = conflict.Repository;
        try
        {
            // Pin the real conflict precondition before asking the carry path to classify it.
            Assert.False(GoalWorktrees.TryVerifyIntegrationMergesClean(repository,
                ReadGit(repository, "merge-base", "main", conflict.NewHead), conflict.NewHead, out var reason));
            Assert.Equal($"integration-merge-not-clean; merge={conflict.NewHead}", reason);
            var bound = conflict.OldHead;
            if (nonAncestorBound)
            {
                ReadGit(repository, "checkout", "-b", "sibling-bound", conflict.OldHead);
                AppendCommit(repository, "src/bound-only.txt", "not in evaluated head");
                bound = ReadGit(repository, "rev-parse", "HEAD");
                ReadGit(repository, "checkout", "candidate-old");
            }
            else if (!conflictAfterBound)
            {
                if (!bindAtMerge) AppendCommit(repository, "src/bound.txt", "bound after conflict resolution");
                bound = ReadGit(repository, "rev-parse", "HEAD");
            }
            var (kernel, goal) = Bind(bound);
            ReadGit(repository, "checkout", "main");
            AppendCommit(repository, "src/main-advance.txt", "unrelated main advance");
            var head = IntegrateMainCleanly(repository, "candidate-old");
            return new Fixture(repository, kernel, goal, bound, head, conflict.NewHead);
        }
        catch { TryDeleteDirectory(repository); throw; }
    }

    private static string IntegrateMainCleanly(string repository, string branch)
    {
        ReadGit(repository, "checkout", branch);
        // Avoid the separate integration-only proof's prescribed subject.
        ReadGit(repository, "merge", "--no-ff", "main", "-m", "Integrate main cleanly");
        var head = ReadGit(repository, "rev-parse", "HEAD");
        var parents = ReadGit(repository, "rev-list", "--parents", "-n", "1", head).Split(' ');
        Assert.Equal(3, parents.Length);
        var automatic = GitCli.Run(repository, "merge-tree", "--write-tree", parents[1], parents[2]);
        Assert.True(automatic.ProcessStarted);
        Assert.False(automatic.DrainTimedOut);
        Assert.Equal(0, automatic.ExitCode);
        Assert.Equal(ReadGit(repository, "rev-parse", $"{head}^{{tree}}"), automatic.Output.Trim());
        return head;
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) Bind(string bound)
    {
        var (kernel, goal) = SimpleGoal("Carry evidence past a contained conflict merge");
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
            ["Full acceptance passes"], VerificationClass.TestVerifiable, [], []));
        kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
            "reviewer", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: bound);
        return (kernel, goal);
    }

    private sealed class Fixture(string repository, AgentOrchestratorKernel kernel, Goal goal,
        string bound, string head, string conflictMerge) : IDisposable
    {
        public string Repository { get; } = repository;
        public AgentOrchestratorKernel Kernel { get; } = kernel;
        public Goal Goal { get; } = goal;
        public string Bound { get; } = bound;
        public string Head { get; } = head;
        public string ConflictMerge { get; } = conflictMerge;
        public void Dispose() => TryDeleteDirectory(Repository);
    }
}
