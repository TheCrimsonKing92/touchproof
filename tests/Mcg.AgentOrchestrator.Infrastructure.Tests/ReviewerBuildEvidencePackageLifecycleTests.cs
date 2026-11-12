using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its repositories, worktree and controlled records; no host build is launched.
public sealed class ReviewerBuildEvidencePackageLifecycleTests
{
    [Fact]
    public void Correction_NewCandidateReceipt_MatchesAndEarlierReceiptsAreStale()
    {
        using var fixture = new Fixture();
        fixture.CommitCandidate();
        var digestA = fixture.Digest();
        var developerReceipt = fixture.Receipt(fixture.Developer, digestA);
        var testerReceipt = fixture.Receipt(fixture.Tester, digestA);
        fixture.SetEvidence(fixture.Developer, developerReceipt);
        fixture.SetEvidence(fixture.Tester, testerReceipt);
        var first = fixture.Checklist();
        Assert.Contains($"Reviewed worktree tree digest: {digestA}", first);
        Assert.Equal(2, first.Split("matches reviewed candidate").Length - 1);
        Assert.Equal(digestA, fixture.Digest()); // Package artifacts must not change candidate identity.

        fixture.CommitCandidate("public sealed class ChangedCandidateTests { public int Revision => 2; }");
        var digestB = fixture.Digest();
        Assert.NotEqual(digestA, digestB);
        var beforeNewReceipt = fixture.Checklist();
        Assert.Contains($"Reviewed worktree tree digest: {digestB}", beforeNewReceipt);
        Assert.Equal(2, beforeNewReceipt.Split("stale: does not describe the reviewed candidate").Length - 1);
        Assert.DoesNotContain("matches reviewed candidate", beforeNewReceipt);

        var newDispatch = Fixture.DispatchAt.AddMinutes(10);
        var correctedReceipt = fixture.Receipt(fixture.Developer, digestB) with
        {
            Id = "corrected-operation", DispatchId = "corrected-dispatch", DispatchAt = newDispatch
        };
        fixture.SetEvidence(fixture.Developer, correctedReceipt);
        var corrected = fixture.Checklist();
        Assert.Contains($"Reviewed worktree tree digest: {digestB}", corrected);
        Assert.Contains("Operation: corrected-operation", corrected);
        Assert.Contains($"Tree digest: {digestB}", corrected);
        Assert.Equal(1, corrected.Split("matches reviewed candidate").Length - 1);
        Assert.Equal(1, corrected.Split("stale: does not describe the reviewed candidate").Length - 1);
        Assert.DoesNotContain($"Operation: {developerReceipt.Id};", corrected);
        Assert.Contains($"Operation: {testerReceipt.Id};", corrected);
        var testerSection = corrected[corrected.IndexOf($"### Prior Tester task {fixture.Tester.Id.Value}", StringComparison.Ordinal)..];
        Assert.Contains($"Tree digest: {digestA}", testerSection);
        Assert.Contains("stale: does not describe the reviewed candidate", testerSection);
        Assert.Equal(digestB, fixture.Digest());
    }

    internal sealed class Fixture : IDisposable
    {
        internal static readonly DateTimeOffset DispatchAt = DateTimeOffset.Parse("2026-11-12T12:00:00Z");
        internal const string CandidatePath = "tests/Mcg.AgentOrchestrator.Core.Tests/ChangedCandidateTests.cs";
        internal string Root { get; } = SharedTestSupport.CreateTempDirectory();
        internal string Repository => Path.Combine(Root, "repository");
        internal string Worktree => Path.Combine(Root, "worktree");
        internal AgentOrchestratorKernel Kernel { get; private set; } = new();
        private readonly GoalId _goalId;
        internal Goal Goal => Kernel.GetGoal(_goalId);
        internal TaskSpec Developer => Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        internal TaskSpec Tester => Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        internal TaskSpec Reviewer => Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);

        internal Fixture()
        {
            Directory.CreateDirectory(Repository);
            Git(Repository, "init", "-b", "master");
            Git(Repository, "config", "user.email", "reviewer-evidence@example.invalid");
            Git(Repository, "config", "user.name", "Reviewer Evidence Tests");
            Git(Repository, "config", "core.autocrlf", "false");
            File.WriteAllText(Path.Combine(Repository, "seed.txt"), "seed\n");
            Git(Repository, "add", "seed.txt");
            Git(Repository, "commit", "-m", "Seed");
            Git(Repository, "worktree", "add", Worktree, "-b", "goal/reviewer", "master");
            _goalId = Kernel.CreateGoal("Review the candidate", new[]
            {
                new TaskSpec(TaskId.New(), "Plan candidate", AgentRole.Planner),
                new TaskSpec(TaskId.New(), "Develop candidate", AgentRole.Developer),
                new TaskSpec(TaskId.New(), "Verify candidate", AgentRole.Tester),
                new TaskSpec(TaskId.New(), "Review candidate", AgentRole.Reviewer)
            }).Id;
        }

        internal void CommitCandidate(string source = "public sealed class ChangedCandidateTests { }")
        {
            var path = Path.Combine(Worktree, CandidatePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, source + "\n");
            Git(Worktree, "add", CandidatePath);
            Git(Worktree, "commit", "-m", "Candidate revision");
            Assert.Empty(Git(Worktree, "status", "--porcelain").Output.Trim());
        }

        internal string Digest()
        {
            Assert.True(WorktreeTreeDigest.TryCompute(Worktree, out var digest, out var reason), reason);
            return digest;
        }

        internal CompletionBuildEvidenceOperation Receipt(TaskSpec task, string digest) => new(
            "operation-" + task.Id.Value, Goal.Id.Value, task.Id.Value, DispatchAt,
            "dispatch-" + task.Id.Value, Worktree, digest,
            ["src/Example/Example.csproj", "tests/Example.Tests/Example.Tests.csproj"],
            new TaskProcessRecord(123, "controlled-worker", Worktree, "stdout", "stderr", "exit",
                DispatchAt, DispatchAt.AddMinutes(1), 0),
            DispatchAt.AddHours(1), CompletionBuildEvidencePhase.Success, "state", "execution", "result",
            456, DispatchAt, BuildExitCode: 0, Applied: true, CustodyReleased: true);

        internal void SetEvidence(TaskSpec task, CompletionBuildEvidenceOperation? receipt,
            string? report = null, DateTimeOffset? reportAttempt = null, bool withDispatch = true)
        {
            var dispatchAt = receipt?.DispatchAt ?? DispatchAt;
            var snapshot = Kernel.ExportSnapshot();
            Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Select(goal => goal with
                {
                    Tasks = goal.Tasks.Select(item => item.Id != task.Id.Value ? item : item with
                    {
                        CompletionBuildEvidence = receipt,
                        LastDispatch = withDispatch ? new TaskDispatchSnapshot("controlled-worker", "worker", Worktree, dispatchAt) : null,
                        LastVerification = report is null ? null : new TaskVerificationSnapshot(
                            "worker", Worktree, 0, WorkerReport(report), "", dispatchAt.AddMinutes(1),
                            DispatchAttemptAt: reportAttempt)
                    }).ToArray()
                }).ToArray()
            });
        }

        internal string Checklist(TaskSpec? task = null, string integrationBranch = "master")
        {
            var directory = WorkerContextArtifacts.Write(Goal, task ?? Reviewer, Worktree, integrationBranch: integrationBranch);
            return File.ReadAllText(Path.Combine(directory, "deterministic-verification.md"));
        }

        internal string Section(string? workingDirectory = null) => string.Join("\n",
            ReviewerCompletionBuildEvidenceSection.Build(Goal, Reviewer, workingDirectory ?? Worktree));

        private static string WorkerReport(string tests) => $"""
            WORKER_RESULT:
            files: none
            commands: build-request
            tests: {tests}
            commit: none
            blockers: none
            assigned_scope_complete: true
            model_fit: controlled
            skills: none
            confidence: high
            END_WORKER_RESULT
            """;

        internal static GitCli.GitResult Git(string root, params string[] arguments)
        {
            var result = GitCli.Run(root, 30_000, arguments);
            Assert.True(result.Succeeded && !result.DrainTimedOut, $"git {string.Join(' ', arguments)}: {result.Error}");
            return result;
        }

        public void Dispose()
        {
            if (Directory.Exists(Worktree)) Git(Repository, "worktree", "remove", "--force", Worktree);
            SharedTestSupport.RemoveTempDirectory(Root);
        }
    }
}
