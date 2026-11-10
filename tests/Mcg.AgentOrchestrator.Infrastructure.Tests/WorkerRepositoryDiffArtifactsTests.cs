using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every case owns its repositories and context; no shared environment or git observers.
public sealed class WorkerRepositoryDiffArtifactsTests
{
    private const int PatchBudget = 40000;
    private static readonly string[] SupplementalNames = ["diff-summary.crosswalk.md", "diff.crosswalk.patch"];

    [Fact]
    public void DeclaredCrosswalkRepository_WritesSummaryAndPatchArtifacts()
    {
        using var fixture = new RepositoryFixture();
        fixture.DeclareRepositories();
        var context = fixture.Write();

        Assert.Equal(new WorkerGitContext().BuildDiffSummary(fixture.HomeWorktree), Read(context, "diff-summary.md"));
        Assert.Equal(new WorkerGitContext().BuildDiffSummary(fixture.CrosswalkWorktree), Read(context, SupplementalNames[0]));
        Assert.Contains(fixture.CrosswalkWorktree, Read(context, SupplementalNames[0]));
        Assert.Contains("+crosswalk candidate", Read(context, SupplementalNames[1]));
        Assert.DoesNotContain("home candidate", Read(context, SupplementalNames[1]));
        Assert.DoesNotContain(Directory.GetFiles(context), path => Path.GetFileName(path).Contains("r-63726f737377616c6b"));

        foreach (var name in SupplementalNames)
        {
            foreach (var index in new[] { "digest.md", "manifest.md", "workflow-brokers.md", "context-budget.md" })
                Assert.Contains(name, Read(context, index));
            Assert.Equal(File.ReadAllBytes(Path.Combine(context, name)),
                File.ReadAllBytes(Path.Combine(context, "packages", fixture.Task.Id.Value, name)));
        }
        using var registry = JsonDocument.Parse(Read(context, "artifact-registry.json"));
        foreach (var name in SupplementalNames)
        {
            var entry = Assert.Single(registry.RootElement.GetProperty("artifacts").EnumerateArray(),
                entry => entry.GetProperty("path").GetString() == name);
            var bytes = File.ReadAllBytes(Path.Combine(context, name));
            Assert.True(entry.GetProperty("exists").GetBoolean());
            Assert.True(entry.GetProperty("hashVerified").GetBoolean());
            Assert.Equal((long)bytes.Length, entry.GetProperty("byteCount").GetInt64());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                entry.GetProperty("sha256").GetString());
            Assert.Contains(entry.GetProperty("roleVisibility").EnumerateArray(), role => role.GetString() == "Reviewer");
        }
    }

    [Fact]
    public void CrosswalkPatchOverBudget_IsTruncatedWithOmittedSize()
    {
        using var fixture = new RepositoryFixture();
        fixture.DeclareRepositories();
        fixture.CommitLargePatch();
        var full = ReadFullCandidatePatch(fixture.CrosswalkWorktree);
        Assert.True(full.Length > PatchBudget);

        var patch = Read(fixture.Write(), SupplementalNames[1]);

        Assert.Equal(full[..PatchBudget] + Environment.NewLine +
            $"...[truncated {full.Length - PatchBudget} chars for context artifact budget]...", patch);
    }

    [Fact]
    public void TypedPackage_LargePatch_PreservesCompleteArtifact()
    {
        using var fixture = new RepositoryFixture();
        fixture.DeclareRepositories();
        fixture.CommitLargePatch();
        var full = ReadFullCandidatePatch(fixture.CrosswalkWorktree);
        Assert.True(full.Length > PatchBudget);

        var context = fixture.Write(typed: true);

        Assert.Equal(full, Read(context, SupplementalNames[1]).Trim());
        Assert.DoesNotContain("...[truncated", Read(context, SupplementalNames[1]));
    }

    [Fact]
    public void UndeclaredGoal_KeepsTodaysContextFileSet()
    {
        using var fixture = new RepositoryFixture();
        var context = fixture.Write();
        var originalNames = FileNames(context);
        var originalIndexes = new[] { "digest.md", "manifest.md", "workflow-brokers.md", "context-budget.md", "diff-summary.md" }
            .ToDictionary(name => name, name => Read(context, name));
        Assert.All(SupplementalNames, name => Assert.DoesNotContain(name, originalNames));
        Assert.DoesNotContain(".patch", Read(context, "artifact-registry.json"));
        Assert.All(originalIndexes.Values, text => Assert.DoesNotContain(".patch", text));

        fixture.DeclareRepositories();
        fixture.Write();
        Assert.Equal(originalNames, FileNames(context).Except(SupplementalNames).Order(StringComparer.Ordinal));

        // Reuse the same context and task snapshot after the declaration disappears.
        var undeclared = new AgentOrchestratorKernel().CreateGoal(fixture.Goal.Id, fixture.Goal.Objective, [fixture.Task]);
        WorkerContextArtifacts.Write(undeclared, fixture.Task, fixture.HomeWorktree);

        Assert.Equal(originalNames, FileNames(context));
        foreach (var (name, text) in originalIndexes) Assert.Equal(text, Read(context, name));
        foreach (var name in SupplementalNames)
            Assert.False(File.Exists(Path.Combine(context, "packages", fixture.Task.Id.Value, name)));
    }

    [Fact]
    public void CandidatePatch_UsesOwnMergeBaseAndCommittedBinaryChangesOnly()
    {
        using var fixture = new RepositoryFixture();
        fixture.DeclareRepositories();
        var worktree = fixture.CrosswalkWorktree;
        File.Delete(Path.Combine(worktree, "seed.txt"));
        File.WriteAllBytes(Path.Combine(worktree, "binary.dat"), [0, 1, 2, 0, 255, 128]);
        RunGit(worktree, "add", "--all");
        RunGit(worktree, "commit", "-m", "Binary addition and deletion");
        File.WriteAllText(Path.Combine(fixture.CrosswalkRoot, "integration-only.txt"), "integration branch advance\n");
        RunGit(fixture.CrosswalkRoot, "add", "integration-only.txt");
        RunGit(fixture.CrosswalkRoot, "commit", "-m", "Advance integration independently");
        File.WriteAllText(Path.Combine(worktree, "staged.txt"), "staged-only\n");
        RunGit(worktree, "add", "staged.txt");
        File.AppendAllText(Path.Combine(worktree, "candidate.txt"), "unstaged-only\n");
        File.WriteAllText(Path.Combine(worktree, "untracked.txt"), "untracked-only\n");

        var context = fixture.Write();
        var patch = Read(context, SupplementalNames[1]);

        Assert.Contains("+crosswalk candidate", patch);
        Assert.Contains("GIT binary patch", patch);
        Assert.Contains("deleted file mode", patch);
        Assert.DoesNotContain("integration-only", patch);
        Assert.DoesNotContain("staged-only", patch);
        Assert.DoesNotContain("untracked-only", patch);
        Assert.DoesNotContain("staged.txt", patch);
        Assert.Contains("staged.txt", Read(context, SupplementalNames[0]));
        Assert.Contains("untracked.txt", Read(context, SupplementalNames[0]));
    }

    [Theory]
    [InlineData(AgentRole.Planner)]
    [InlineData(AgentRole.Researcher)]
    [InlineData(AgentRole.Developer)]
    [InlineData(AgentRole.Tester)]
    public void SharedContext_AllWorkerRoles_IndexRepositoryArtifacts(AgentRole role)
    {
        using var fixture = new RepositoryFixture(role);
        fixture.DeclareRepositories();

        var context = fixture.Write();

        foreach (var name in SupplementalNames)
            Assert.Contains(name, Read(context, "workflow-brokers.md"));
    }

    [Theory]
    [InlineData("worktree")]
    [InlineData("detached")]
    [InlineData("reference")]
    public void MissingRepositoryPrerequisite_ContextGeneration_FailsWithKey(string prerequisite)
    {
        using var fixture = new RepositoryFixture();
        fixture.DeclareRepositories();
        if (prerequisite == "detached") RunGit(fixture.CrosswalkRoot, "switch", "--detach");
        else if (prerequisite == "reference") RunGit(fixture.CrosswalkRoot, "update-ref", "-d", "refs/heads/integration");
        else RunGit(fixture.CrosswalkRoot, "worktree", "remove", fixture.CrosswalkWorktree);

        var error = Assert.Throws<InvalidOperationException>(() => fixture.Write());

        Assert.Contains("repository=crosswalk;", error.Message);
        Assert.Contains(prerequisite switch
        {
            "detached" => "integration branch",
            "reference" => "candidate diff against integration",
            _ => fixture.CrosswalkWorktree
        }, error.Message);
    }

    [Fact]
    public void EmptyCandidate_Context_IndexesEmptyPatch()
    {
        using var fixture = new RepositoryFixture();
        fixture.DeclareRepositories();
        RunGit(fixture.CrosswalkWorktree, "reset", "--hard", "integration");

        var context = fixture.Write();

        Assert.Equal(string.Empty, Read(context, SupplementalNames[1]));
        Assert.Contains(SupplementalNames[1], Read(context, "manifest.md"));
        Assert.Contains(SupplementalNames[1], Read(context, "artifact-registry.json"));
    }

    [Theory]
    [InlineData(AgentRole.Planner)]
    [InlineData(AgentRole.Developer)]
    [InlineData(AgentRole.Reviewer)]
    public void UnprovisionedRepository_Context_IndexesExplicitNoticeThenRefreshes(AgentRole role)
    {
        using var fixture = new RepositoryFixture(role);
        fixture.DeclareRepositories();
        RunGit(fixture.CrosswalkRoot, "worktree", "remove", fixture.CrosswalkWorktree);
        RunGit(fixture.CrosswalkRoot, "branch", "-D", GoalRepositoryWorktreeLayout.BranchName(fixture.Goal.Id, "crosswalk"));

        var context = fixture.Write();

        foreach (var name in SupplementalNames)
        {
            Assert.Contains("repository=crosswalk; not yet provisioned; no candidate diff is available.", Read(context, name));
            Assert.Contains(fixture.CrosswalkWorktree, Read(context, name));
            Assert.Contains(name, Read(context, "manifest.md"));
            Assert.Contains(name, Read(context, "artifact-registry.json"));
        }
        RunGit(fixture.CrosswalkRoot, "worktree", "add", "-b",
            GoalRepositoryWorktreeLayout.BranchName(fixture.Goal.Id, "crosswalk"), fixture.CrosswalkWorktree, "candidate");
        fixture.Write();

        Assert.Contains("+crosswalk candidate", Read(context, SupplementalNames[1]));
        Assert.DoesNotContain("not yet provisioned", Read(context, SupplementalNames[0]));
    }

    [Fact]
    public void CandidatePatch_IgnoresDiffPresentationConfigAndSameNamedTag()
    {
        using var fixture = new RepositoryFixture();
        fixture.DeclareRepositories();
        RunGit(fixture.CrosswalkWorktree, "tag", "integration", "HEAD");
        RunGit(fixture.CrosswalkRoot, "config", "color.diff", "always");
        RunGit(fixture.CrosswalkRoot, "config", "diff.external", "nonexistent-diff-for-context-test");

        var patch = Read(fixture.Write(), SupplementalNames[1]);

        Assert.Contains("+crosswalk candidate", patch);
        Assert.DoesNotContain("\u001b[", patch);
    }

    [Fact]
    public void Regeneration_NewCandidate_RefreshesPatchAndRemovesObsoleteArtifacts()
    {
        using var fixture = new RepositoryFixture();
        fixture.DeclareRepositories();
        var context = fixture.Write();
        var package = Path.Combine(context, "packages", fixture.Task.Id.Value);
        foreach (var directory in new[] { context, package })
        {
            File.WriteAllText(Path.Combine(directory, "diff.obsolete.patch"), "stale");
            File.WriteAllText(Path.Combine(directory, "diff-summary.obsolete.md"), "stale");
        }
        File.AppendAllText(Path.Combine(fixture.CrosswalkWorktree, "candidate.txt"), "next candidate\n");
        RunGit(fixture.CrosswalkWorktree, "add", "candidate.txt");
        RunGit(fixture.CrosswalkWorktree, "commit", "-m", "Next candidate");

        fixture.Write();

        foreach (var directory in new[] { context, package })
        {
            Assert.Contains("+next candidate", Read(directory, SupplementalNames[1]));
            Assert.False(File.Exists(Path.Combine(directory, "diff.obsolete.patch")));
            Assert.False(File.Exists(Path.Combine(directory, "diff-summary.obsolete.md")));
        }
        Assert.DoesNotContain("obsolete", Read(context, "artifact-registry.json"));
    }

    private static string Read(string directory, string name) => File.ReadAllText(Path.Combine(directory, name));

    private static string[] FileNames(string directory) => Directory.GetFiles(directory)
        .Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal).ToArray();

    private static string ReadFullCandidatePatch(string worktree)
    {
        // RunGitProbe intentionally retains only 16 KB, below the artifact budget exercised here.
        var result = GitCli.Run(worktree, "diff", "--binary", "integration...HEAD", "--");
        Assert.True(result.ProcessStarted && result.Succeeded && !result.DrainTimedOut, result.Error);
        return result.Output.Trim();
    }

    private static string RunGit(string root, params string[] args)
    {
        var result = InfrastructureTestSupport.RunGitProbe(root, args);
        Assert.True(result.Succeeded, $"{result.Command}: {result.StandardError}");
        return result.StandardOutput.Trim();
    }

    private sealed class RepositoryFixture : IDisposable
    {
        private readonly AgentOrchestratorKernel kernel = new();
        private readonly List<string> roots = [];
        internal string HomeRoot { get; }
        internal string CrosswalkRoot { get; }
        internal string HomeWorktree { get; }
        internal string CrosswalkWorktree { get; }
        internal Goal Goal { get; }
        internal TaskSpec Task { get; }

        internal RepositoryFixture(AgentRole role = AgentRole.Reviewer)
        {
            try
            {
                HomeRoot = Seed("trunk");
                CrosswalkRoot = Seed("integration");
                Task = new TaskSpec(TaskId.New(), "Review candidate changes.", role);
                Goal = kernel.CreateGoal("Review all repositories", [Task]);
                HomeWorktree = Path.Combine(HomeRoot, "home-goal");
                CrosswalkWorktree = GoalRepositoryWorktreeLayout.WorktreePath(CrosswalkRoot, Goal.Id, "crosswalk");
                AddCandidate(HomeRoot, HomeWorktree, "trunk", "home candidate\n");
                AddCandidate(CrosswalkRoot, CrosswalkWorktree, "integration", "crosswalk candidate\n");
                RunGit(CrosswalkWorktree, "branch", "-m", GoalRepositoryWorktreeLayout.BranchName(Goal.Id, "crosswalk"));
                RunGit(CrosswalkRoot, "branch", "candidate", "refs/heads/" + GoalRepositoryWorktreeLayout.BranchName(Goal.Id, "crosswalk"));
            }
            catch { Dispose(); throw; }
        }

        private string Seed(string branch)
        {
            var root = PastDatedSeedRepository.Create();
            roots.Add(root);
            RunGit(root, "branch", "-M", branch);
            return root;
        }

        private static void AddCandidate(string root, string worktree, string branch, string content)
        {
            RunGit(root, "worktree", "add", "-b", "candidate", worktree, branch);
            File.WriteAllText(Path.Combine(worktree, "candidate.txt"), content);
            RunGit(worktree, "add", "candidate.txt");
            RunGit(worktree, "commit", "-m", "Candidate change");
            Assert.NotEqual(RunGit(root, "rev-parse", branch), RunGit(worktree, "rev-parse", "HEAD"));
        }

        internal void DeclareRepositories() => kernel.DeclareGoalRepositories(Goal.Id,
            new GoalRepositoryDeclaration(["default", "crosswalk"], new Dictionary<string, string>
            {
                ["default"] = HomeRoot,
                ["crosswalk"] = CrosswalkRoot
            }));

        internal string Write(bool typed = false) => WorkerContextArtifacts.Write(Goal, Task, HomeWorktree,
            providerName: typed ? "OpenAI" : null, modelName: typed ? AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias : null);

        internal void CommitLargePatch()
        {
            File.WriteAllText(Path.Combine(CrosswalkWorktree, "large.txt"), new string('x', PatchBudget + 1000) + "\n");
            RunGit(CrosswalkWorktree, "add", "large.txt");
            RunGit(CrosswalkWorktree, "commit", "-m", "Large candidate");
        }

        public void Dispose()
        {
            foreach (var root in roots) PastDatedSeedRepository.Delete(root);
        }
    }
}
