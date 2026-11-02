using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;
using static LandingExecutorTests;

// Each case owns its temporary Git repository and kernel; no process-global state is changed.
public sealed class IntegrationMergeRatchetOnlyCleanTests : HostCapacityBoundTestBase
{
    private const string GoalPath = "src/goal.txt";

    [Xunit.Theory]
    [Xunit.InlineData(false, 22)]
    [Xunit.InlineData(true, 22)]
    [Xunit.InlineData(false, 24)]
    [Xunit.InlineData(true, 24)]
    public void RatchetConflict_NonIncreasingResolution_CarriesForward(bool mainFirst, int ceiling)
    {
        using var fixture = CreateFixture(ceiling, mainFirst: mainFirst);
        AssertAllowed(fixture);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void RatchetConflict_RaisedResolution_Refuses(bool mainFirst)
    {
        using var fixture = CreateFixture(25, mainFirst: mainFirst);
        AssertRefused(fixture);
    }

    [Xunit.Fact]
    public void RatchetConflict_ResolutionAlsoEditsGoalSource_Refuses()
    {
        using var fixture = CreateFixture(22, editGoalSource: true);
        AssertRefused(fixture);
    }

    [Xunit.Theory]
    [Xunit.InlineData("comment")]
    [Xunit.InlineData("delete")]
    public void RatchetConflict_NonRowResolution_Refuses(string change)
    {
        using var fixture = CreateFixture(22, nonRowChange: change);
        AssertRefused(fixture);
    }

    [Xunit.Fact]
    public void CleanMerge_UnchangedAutomaticTree_CarriesForward()
    {
        using var fixture = CreateFixture(24, ratchetConflict: false);
        AssertAllowed(fixture);
    }

    [Xunit.Fact]
    public void CleanAutomaticMerge_RatchetOnlyRecordedDifference_CarriesForward()
    {
        using var fixture = CreateFixture(22, ratchetConflict: false);
        AssertAllowed(fixture);
    }

    private static void AssertAllowed(Fixture fixture)
    {
        Assert.True(GoalWorktrees.TryComputePatchEquivalence(fixture.Repository, fixture.Bound,
            fixture.Head, "main", out var evidence, out var refusal), refusal);
        // This must exercise the cleanliness check, rather than another carry-forward proof.
        Assert.StartsWith("range-diff", evidence, StringComparison.Ordinal);
        Assert.Equal(string.Empty, refusal);

        var diagnostic = AcceptanceCriterionEvidence.RecordAndDescribeOutstanding(
            fixture.Goal, fixture.Head, fixture.Kernel, "main", fixture.Repository);
        Assert.Null(diagnostic);
        var obligation = Assert.Single(fixture.Goal.CriterionEvidenceObligations);
        Assert.Equal(CriterionEvidenceState.Satisfied, obligation.State);
        Assert.Equal(fixture.Head, obligation.CandidateSha);
        Assert.Equal($"full-acceptance:{fixture.Head}", obligation.ReceiptId);
    }

    private static void AssertRefused(Fixture fixture)
    {
        var reason = $"integration-merge-not-clean; merge={fixture.Head}";
        Assert.False(GoalWorktrees.TryComputePatchEquivalence(fixture.Repository, fixture.Bound,
            fixture.Head, "main", out _, out var refusal));
        Assert.Equal(reason, refusal);

        var diagnostic = AcceptanceCriterionEvidence.RecordAndDescribeOutstanding(
            fixture.Goal, fixture.Head, fixture.Kernel, "main", fixture.Repository);
        Assert.Contains($"reason={reason}", diagnostic, StringComparison.Ordinal);
        var obligation = Assert.Single(fixture.Goal.CriterionEvidenceObligations);
        Assert.Equal(CriterionEvidenceState.Pending, obligation.State);
        Assert.Equal(fixture.Bound, obligation.ExpectedCandidateSha);
        Assert.Null(obligation.ReceiptId);
    }

    private static Fixture CreateFixture(int ceiling, bool mainFirst = false,
        bool editGoalSource = false, bool ratchetConflict = true, string? nonRowChange = null)
    {
        var repository = CreateGitRepository();
        try
        {
            ReadGit(repository, "config", "core.autocrlf", "false");
            WriteAuthority(repository, 20);
            ReadGit(repository, "add", SourceSizeRatchet.SourcePath);
            ReadGit(repository, "commit", "-m", "Seed ratchet authority");

            var (kernel, goal) = SimpleGoal("Preserve acceptance across ratchet-only integration");
            var branch = GoalWorktrees.BranchName(goal.Id);
            ReadGit(repository, "checkout", "-b", branch);
            AppendCommit(repository, GoalPath, "goal owned line");
            WriteAuthority(repository, 24);
            ReadGit(repository, "add", SourceSizeRatchet.SourcePath);
            ReadGit(repository, "commit", "--amend", "--no-edit");
            var bound = ReadGit(repository, "rev-parse", "HEAD");
            kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                ["Full acceptance passes"], VerificationClass.TestVerifiable, [], []));
            kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
                "reviewer", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: bound);

            ReadGit(repository, "checkout", "main");
            if (ratchetConflict)
            {
                WriteAuthority(repository, 18);
                ReadGit(repository, "add", SourceSizeRatchet.SourcePath);
                ReadGit(repository, "commit", "-m", "Retighten authority on main");
            }
            else AppendCommit(repository, "src/unrelated.txt", "main advance");

            if (mainFirst) ReadGit(repository, "checkout", "-b", "merge-candidate");
            else ReadGit(repository, "checkout", branch);
            var merging = GitCli.Run(repository, "merge", "--no-ff", "--no-commit", mainFirst ? branch : "main");
            Assert.True(merging.ProcessStarted);
            Assert.False(merging.DrainTimedOut);
            Assert.Equal(ratchetConflict ? 1 : 0, merging.ExitCode);
            if (ratchetConflict)
            {
                Assert.Contains("<<<<<<<", File.ReadAllText(Path.Combine(repository, SourceSizeRatchet.SourcePath)),
                    StringComparison.Ordinal);
                var conflicts = ReadMergeTree(repository, mainFirst ? "main" : bound, mainFirst ? bound : "main");
                Assert.Equal(SourceSizeRatchet.SourcePath, Assert.Single(conflicts.Skip(1)));
            }

            WriteAuthority(repository, ceiling);
            if (editGoalSource) File.WriteAllText(Path.Combine(repository, GoalPath), "manual rewritten goal line\n");
            if (nonRowChange == "comment")
                File.AppendAllText(Path.Combine(repository, SourceSizeRatchet.SourcePath), "// manual comment\n");
            if (nonRowChange == "delete") File.Delete(Path.Combine(repository, SourceSizeRatchet.SourcePath));
            ReadGit(repository, "add", "-A");
            // Avoid the separate integration-only proof, whose subject ends in "before acceptance".
            ReadGit(repository, "commit", "-m", $"Integrate main into {branch} (additive conflict auto-merge)");
            return new Fixture(repository, kernel, goal, bound, ReadGit(repository, "rev-parse", "HEAD"));
        }
        catch { TryDeleteDirectory(repository); throw; }
    }

    private static string[] ReadMergeTree(string repository, string first, string second)
    {
        var result = GitCli.Run(repository, "merge-tree", "--write-tree", "--name-only", "--no-messages", first, second);
        Assert.True(result.ProcessStarted);
        Assert.False(result.DrainTimedOut);
        Assert.Equal(1, result.ExitCode);
        return result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    }

    private static void WriteAuthority(string repository, int ceiling)
    {
        var path = Path.Combine(repository, SourceSizeRatchet.SourcePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"// authority comment\nnew SourceSizeCeiling(\"src/guarded.cs\", {ceiling}), // row comment\n");
    }

    private sealed class Fixture(string repository, AgentOrchestratorKernel kernel, Goal goal,
        string bound, string head) : IDisposable
    {
        public string Repository { get; } = repository;
        public AgentOrchestratorKernel Kernel { get; } = kernel;
        public Goal Goal { get; } = goal;
        public string Bound { get; } = bound;
        public string Head { get; } = head;
        public void Dispose() => TryDeleteDirectory(Repository);
    }
}
