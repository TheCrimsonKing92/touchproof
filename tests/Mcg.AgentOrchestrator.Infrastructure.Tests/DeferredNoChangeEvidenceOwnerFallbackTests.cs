using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

// Parallel-safe: each scenario owns its kernel, temporary directory, and fake evidence runner.
public sealed class DeferredNoChangeEvidenceOwnerFallbackTests
{
    private static readonly string Candidate = new('b', 40);

    [Xunit.Fact]
    public void ReviewerOwnsEvidenceRunAndGreenAdvancesToReviewWhenNoTester()
    {
        using var scenario = new Scenario(ReviewGoalWithoutTester());
        Xunit.Assert.Equal("deferred-no-change-round", scenario.Developer.LastVerification?.CompletionVerdictRule);
        Xunit.Assert.DoesNotContain(scenario.Goal.Tasks, task => task.RequiredRole == AgentRole.Tester);
        var reviewer = scenario.Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        var driver = scenario.Driver();

        var result = driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive);

        Xunit.Assert.False(result.WasEscalated);
        scenario.AssertGreenEvidenceOwnedBy(reviewer);
        Xunit.Assert.Equal(AgentRole.Reviewer, Xunit.Assert.Single(scenario.Dispatches));
    }

    [Xunit.Fact]
    public void DeveloperOnlyGoalEscalatesOwnerAbsentWithoutThrowing()
    {
        using var scenario = new Scenario(SimpleGoal());
        Xunit.Assert.Equal("deferred-no-change-round", scenario.Developer.LastVerification?.CompletionVerdictRule);
        Xunit.Assert.Single(scenario.Goal.Tasks);
        var driver = scenario.Driver();

        var result = driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive);

        Xunit.Assert.True(result.WasEscalated);
        Xunit.Assert.Contains("DEFERRED_NO_CHANGE_EVIDENCE_OWNER_ABSENT", result.Outcome.ToString(), StringComparison.Ordinal);
        Xunit.Assert.Contains("task=" + scenario.Developer.Id.Value, result.Outcome.ToString(), StringComparison.Ordinal);
        Xunit.Assert.Contains("candidate_sha=" + Candidate, result.Outcome.ToString(), StringComparison.Ordinal);
        Xunit.Assert.Empty(scenario.Requests);
        Xunit.Assert.Empty(scenario.RequestOwners);
        Xunit.Assert.Empty(scenario.RunOwners);
        Xunit.Assert.Empty(scenario.Dispatches);
        Xunit.Assert.DoesNotContain(scenario.Goal.Timeline, evt =>
            evt.Kind is ProgressKind.FindingEvidenceRequestRecorded or ProgressKind.FindingEvidenceRunRecorded);
    }

    [Xunit.Fact]
    public void TesterOwnsEvidenceRunWhenTesterExists()
    {
        using var scenario = new Scenario(SoftwareGoal());
        Xunit.Assert.Equal("deferred-no-change-round", scenario.Developer.LastVerification?.CompletionVerdictRule);
        var tester = scenario.Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = scenario.Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        var driver = scenario.Driver();

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive);

        scenario.AssertGreenEvidenceOwnedBy(tester);
        Xunit.Assert.DoesNotContain(reviewer.Id, scenario.RequestOwners);
        Xunit.Assert.DoesNotContain(reviewer.Id, scenario.RunOwners);
        Xunit.Assert.Equal(AgentRole.Reviewer, Xunit.Assert.Single(scenario.Dispatches));
    }

    private sealed class Scenario : IDisposable
    {
        public string Root { get; } = ConductorDriverTests.CreateTempDirectory();
        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public TaskSpec Developer { get; }
        public List<string> Requests { get; } = [];
        public List<TaskId> RequestOwners { get; } = [];
        public List<TaskId> RunOwners { get; } = [];
        public List<AgentRole> Dispatches { get; } = [];
        private CandidateIdentity Identity { get; } = new("deferred-candidate", "base", "manifest");

        public Scenario((AgentOrchestratorKernel Kernel, Goal Goal) setup)
        {
            (Kernel, Goal) = setup;
            Developer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            foreach (var task in Goal.Tasks.TakeWhile(task => task.Id != Developer.Id))
                PassVerification(Kernel, Goal, task);
            Kernel.RetryTask(Goal.Id, Developer.Id, "Retry unchanged candidate after evidence.");
            DispatchTask(Kernel, Goal, Developer, baseCommit: Candidate);
            var output = "NO_CHANGE: the candidate already contains the revision.\n" +
                "WORKER_RESULT:\nfiles: none\ncommands: none\n" +
                "tests: deferred - DeferredAlphaTests\ncommit: none\nblockers: none\n" +
                "assigned_scope_complete: true\nmodel_fit: test/model - adequate - fixture\n" +
                "skills: none\nconfidence: high\nEND_WORKER_RESULT";
            Kernel.RecordDispatchExecutionResult(Goal.Id, Developer.Id, new TaskVerificationRecord(
                "test.exe", "C:\\tmp", 0, output,
                new DeferredNoChangeOutcome(Candidate, ["DeferredAlphaTests"],
                    "NO_CHANGE: the candidate already contains the revision.").FormatMarker(),
                DateTimeOffset.UtcNow, WorkerResultPresent: true, HasCommittedChanges: false));
            Xunit.Assert.Equal(WorkTaskStatus.Completed, Developer.Status);
            if (Goal.Tasks.FirstOrDefault(task => task.RequiredRole == AgentRole.Tester) is { } tester)
            {
                DispatchTask(Kernel, Goal, tester, baseCommit: Candidate);
                Kernel.RecordTaskVerification(Goal.Id, tester.Id, new TaskVerificationRecord(
                    "test.exe", "C:\\tmp", 0, "pass", "", DateTimeOffset.UtcNow,
                    WorkerResultPresent: true, HasCommittedChanges: false,
                    ReviewedCommit: Candidate, CandidateIdentity: Identity));
            }
            var worktree = GoalWorktrees.WorktreePath(Root, Goal.Id);
            Directory.CreateDirectory(worktree);
            File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: fixture");
            var project = Path.Combine(worktree, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(project, "DeferredAlphaTests.cs"), "public class DeferredAlphaTests {}");
        }

        public ConductorDriver Driver()
        {
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => NoPreReviewContext(Candidate),
                focusedEvidenceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    Path.Combine(Root, "attempts"), TrunkBranchName.Default, runInline: true,
                    acquireStableSlotLease: (_, _) => null),
                runFocusedEvidence: (_, request) =>
                {
                    Requests.Add(request);
                    return ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(Root, request, Candidate);
                },
                dispatchAndStart: goal =>
                {
                    Dispatches.Add(goal.Tasks.First(task => task.Status == WorkTaskStatus.Assigned).RequiredRole);
                    return DispatchStartOutcome.Started();
                },
                recordFindingEvidenceRequest: (goalId, taskId, message) =>
                {
                    RequestOwners.Add(taskId);
                    Kernel.RecordFindingEvidenceRequest(goalId, taskId, message);
                },
                recordFindingEvidenceRun: (goalId, taskId, message) =>
                {
                    RunOwners.Add(taskId);
                    Kernel.RecordFindingEvidenceRun(goalId, taskId, message);
                },
                executionDirectory: Root);
            driver.OverrideCandidateIdentityResolverForTests(_ => Identity);
            driver.OverrideDeveloperCompletionStructuralPreflightForTests(
                _ => new DeveloperCompletionStructuralFindings(false, "clean"), TimeSpan.FromSeconds(5));
            return driver;
        }

        public void AssertGreenEvidenceOwnedBy(TaskSpec owner)
        {
            Xunit.Assert.Equal("Infrastructure.Tests:DeferredAlphaTests", Xunit.Assert.Single(Requests));
            Xunit.Assert.Equal(owner.Id, Xunit.Assert.Single(RequestOwners));
            Xunit.Assert.Equal(owner.Id, Xunit.Assert.Single(RunOwners));
            Xunit.Assert.Contains(Goal.Timeline, evt =>
                evt.Kind == ProgressKind.FindingEvidenceRunRecorded &&
                evt.Message.StartsWith("finding-evidence deferred-no-change outcome=green;", StringComparison.Ordinal) &&
                evt.Message.Contains("candidate_sha=" + Candidate, StringComparison.Ordinal));
            Xunit.Assert.False(ConductorDriver.HasPendingDeferredNoChangeEvidence(Goal));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
