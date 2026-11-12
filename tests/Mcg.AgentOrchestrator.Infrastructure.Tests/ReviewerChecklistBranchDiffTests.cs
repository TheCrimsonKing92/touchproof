using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = ReviewerBuildEvidencePackageLifecycleTests.Fixture;

// Parallel-safe: each case owns a real temporary repository/worktree and changes no global state.
public sealed class ReviewerChecklistBranchDiffTests
{
    [Fact]
    public void CleanCommittedCandidate_Checklist_UsesBranchFilesAndTestImpact()
    {
        using var fixture = new Fixture();
        fixture.CommitCandidate();
        Assert.Empty(Fixture.Git(fixture.Worktree, "status", "--porcelain").Output.Trim());
        Assert.Single(new WorkerGitContext().ReadReviewerChangedFileScope(fixture.Worktree, "master").ChangedFiles);

        var checklist = fixture.Checklist();

        Assert.Contains("## Reviewed Branch Diff", checklist);
        Assert.Contains("Committed branch-diff files: 1", checklist);
        Assert.Contains($"- `{Fixture.CandidatePath}`", checklist);
        Assert.Contains("ChangedCandidateTests", checklist[(checklist.IndexOf("## Test Impact Plan", StringComparison.Ordinal))..]);
        Assert.Contains("Requires tests: yes", checklist);
        AssertNoEmptyScopeClaims(checklist);
    }

    [Fact]
    public void DirtyFiles_Checklist_StillUsesCommittedScope()
    {
        using var fixture = new Fixture();
        fixture.CommitCandidate();
        var dirtyPath = "tests/Mcg.AgentOrchestrator.Core.Tests/DirtyOnlyTests.cs";
        File.WriteAllText(Path.Combine(fixture.Worktree, dirtyPath), "public class DirtyOnlyTests { }");
        Assert.Contains(dirtyPath, new WorkerGitContext().ReadChangedFilesForTestImpact(fixture.Worktree));

        var checklist = fixture.Checklist();

        Assert.Contains($"- `{Fixture.CandidatePath}`", checklist);
        Assert.DoesNotContain("DirtyOnlyTests", checklist);
    }

    [Fact]
    public void MissingBranch_Checklist_ReportsUnavailableWithoutDirtyFallback()
    {
        using var fixture = new Fixture();
        fixture.CommitCandidate();
        File.WriteAllText(Path.Combine(fixture.Worktree, "dirty.txt"), "dirty");

        var checklist = fixture.Checklist(integrationBranch: "missing-trunk");

        Assert.Contains("Committed branch diff unavailable:", checklist);
        Assert.Contains("Test-impact assessment unavailable", checklist);
        Assert.Contains("Requires tests: unknown", checklist);
        Assert.DoesNotContain("dirty.txt", checklist);
        Assert.DoesNotContain("Committed branch-diff files: 0", checklist);
        AssertNoEmptyScopeClaims(checklist);
    }

    [Fact]
    public void NonGitWorkspace_Checklist_ReportsUnavailable()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var reviewer = new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer);
            var goal = new AgentOrchestratorKernel().CreateGoal("Review", [reviewer]);
            var directory = WorkerContextArtifacts.Write(goal, reviewer, root);
            var checklist = File.ReadAllText(Path.Combine(directory, "deterministic-verification.md"));
            Assert.Contains("Committed branch diff unavailable:", checklist);
            Assert.Contains("tree digest: unavailable", checklist);
            AssertNoEmptyScopeClaims(checklist);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyOrDocumentationDiff_Checklist_AvoidsNoBuildConclusion(bool documentation)
    {
        using var fixture = new Fixture();
        if (documentation)
        {
            File.WriteAllText(Path.Combine(fixture.Worktree, "README.md"), "Documentation\n");
            Fixture.Git(fixture.Worktree, "add", "README.md");
            Fixture.Git(fixture.Worktree, "commit", "-m", "Documentation");
        }

        var checklist = fixture.Checklist();

        Assert.Contains($"Committed branch-diff files: {(documentation ? 1 : 0)}", checklist);
        Assert.Contains("No test command selected for the listed files", checklist);
        AssertNoEmptyScopeClaims(checklist);
    }

    [Theory]
    [InlineData(AgentRole.Developer)]
    [InlineData(AgentRole.Tester)]
    [InlineData(AgentRole.Planner)]
    public void OtherRoles_CleanCandidate_RetainDirtyTreeChecklist(AgentRole role)
    {
        using var fixture = new Fixture();
        fixture.CommitCandidate();
        var task = fixture.Goal.Tasks.Single(task => task.RequiredRole == role);

        var checklist = fixture.Checklist(task);

        Assert.DoesNotContain("## Reviewed Branch Diff", checklist);
        Assert.DoesNotContain("## Host Completion Build Evidence", checklist);
        Assert.Contains("No changed files detected; inspect the branch diff before running tests.", checklist);
        Assert.Contains("test impact: no build required", checklist);
    }

    private static void AssertNoEmptyScopeClaims(string checklist)
    {
        Assert.DoesNotContain("No changed files detected", checklist);
        Assert.DoesNotContain("no build required", checklist);
        Assert.DoesNotContain("No deterministic build/test command required", checklist);
        Assert.DoesNotContain("no build or test command is required", checklist);
    }
}
