using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: each scenario owns its kernel, temporary tree and inline coordinator.
public sealed class PreTesterTypedEntryWritePointTests
{
    private const string Candidate = "bbb2222";

    [Fact]
    public void StartedAndAbandoned_StoreTypedFieldsAndRenderedMessage()
    {
        var kernel = new AgentOrchestratorKernel();
        var tester = new TaskSpec(TaskId.New(), "Verify", AgentRole.Tester);
        var goal = kernel.CreateGoal("Typed write points", [tester]);
        string[] selections = ["Core.Tests:FirstTests", "Core.Tests:SecondTests"];
        string[] notRun = ["MissingTests"];
        PreTesterStartedEvidence.RecordStarted(Candidate, "receipt", selections, notRun,
            (entry, message) => kernel.RecordFindingEvidenceRequest(goal.Id, tester.Id, entry, message));

        var started = Assert.Single(goal.Timeline.Where(evt => evt.Kind == ProgressKind.FindingEvidenceRequestRecorded));
        var expected = new PreTesterEvidenceEntry("started", Candidate, "receipt", selections, notRun, null, []);
        AssertEntry(started, expected);
        Assert.True(PreTesterStartedEvidence.TryAbandon(started.PreTesterEvidence,
            PreTesterStartedEvidence.MalformedSelectionName,
            (entry, message) => kernel.RecordFindingEvidenceRun(goal.Id, tester.Id, entry, message)));

        var abandoned = Assert.Single(goal.Timeline.Where(evt => evt.Kind == ProgressKind.FindingEvidenceRunRecorded));
        AssertEntry(abandoned, expected with { Outcome = "abandoned", AbandonmentReason = "malformed-selection-name" });
        Assert.EndsWith("; abandoned_reason=malformed-selection-name", abandoned.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeferredRun_ReplacesMalformedStartAndStoresTypedRun()
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var (kernel, goal) = SoftwareGoal("Typed pre-Tester driver writes");
            var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            foreach (var task in goal.Tasks.TakeWhile(task => task.Id != developer.Id))
                PassVerification(kernel, goal, task);
            DispatchTask(kernel, goal, developer, baseCommit: "aaa1111");
            var output = string.Join('\n', "WORKER_RESULT:", "files: tests/DeclaredTests.cs", "commands: none",
                "tests: deferred - DeclaredTests", "commit: none", "blockers: none", "assigned_scope_complete: true",
                "model_fit: test/model - adequate - fixture", "skills: none", "confidence: high", "END_WORKER_RESULT");
            kernel.RecordTaskVerification(goal.Id, developer.Id, new("fixture", root, 0, output, "",
                developer.LastDispatch!.DispatchedAt, WorkerResultPresent: true, HasCommittedChanges: true));
            Assert.Equal(WorkTaskStatus.Completed, developer.Status);
            var worktree = GoalWorktrees.WorktreePath(root, goal.Id);
            Directory.CreateDirectory(worktree);
            File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: fixture");
            var project = Path.Combine(worktree, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(project, "DeclaredTests.cs"),
                "public class DeclaredTests { [Xunit.Fact] public void Runs() {} }");
            var malformed = new PreTesterEvidenceEntry("started", Candidate, "old-receipt", ["colonless"],
                ["OldMissingTests"], null, []);
            kernel.RecordFindingEvidenceRequest(goal.Id, tester.Id, malformed, PreTesterEvidenceIndexLines.FormatMarker(malformed));
            var requests = new List<string>();
            var dispatches = 0;
            var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => NoPreReviewContext(Candidate), getLandingFileScopes: _ => [],
                executionDirectory: root,
                focusedEvidenceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    Path.Combine(root, "attempts"), TrunkBranchName.Default, runInline: true, acquireStableSlotLease: (_, _) => null),
                runFocusedEvidence: (_, request) =>
                {
                    requests.Add(request);
                    return ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(root, request, Candidate);
                },
                recordTaskNote: (gid, tid, message) => kernel.RecordTaskNote(gid, tid, message),
                recordFindingEvidenceRequest: (gid, tid, message) => kernel.RecordFindingEvidenceRequest(gid, tid, message),
                recordFindingEvidenceRun: (gid, tid, message) => kernel.RecordFindingEvidenceRun(gid, tid, message),
                dispatchAndStart: _ => { dispatches++; return DispatchStartOutcome.Started(); });
            driver.PreTesterRecorder = new(
                (gid, tid, entry, message) => kernel.RecordFindingEvidenceRequest(gid, tid, entry, message),
                (gid, tid, entry, message) => kernel.RecordFindingEvidenceRun(gid, tid, entry, message));
            driver.OverrideDeveloperCompletionStructuralPreflightForTests(
                _ => new DeveloperCompletionStructuralFindings(false, "clean"), TimeSpan.FromSeconds(5));

            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            Assert.Equal("Infrastructure.Tests:DeclaredTests", Assert.Single(requests));
            Assert.Equal(1, dispatches);
            var entries = goal.Timeline.Where(evt => evt.PreTesterEvidence is not null).ToArray();
            Assert.Equal(4, entries.Length);
            AssertEntry(entries[1], malformed with { Outcome = "abandoned", AbandonmentReason = "malformed-selection-name" });
            var started = Assert.IsType<PreTesterEvidenceEntry>(entries[2].PreTesterEvidence);
            Assert.NotEqual("old-receipt", started.ReceiptId);
            AssertEntry(entries[2], new("started", Candidate, started.ReceiptId,
                ["Infrastructure.Tests:DeclaredTests"], [], null, []));
            var run = Assert.IsType<PreTesterEvidenceEntry>(entries[3].PreTesterEvidence);
            Assert.False(string.IsNullOrWhiteSpace(run.ResultPath));
            AssertEntry(entries[3], new("green", Candidate, started.ReceiptId,
                ["Infrastructure.Tests:DeclaredTests"], [], run.ResultPath, []));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void AssertEntry(ProgressEvent evt, PreTesterEvidenceEntry expected)
    {
        var entry = Assert.IsType<PreTesterEvidenceEntry>(evt.PreTesterEvidence);
        Assert.Equal(expected.Outcome, entry.Outcome);
        Assert.Equal(expected.CandidateSha, entry.CandidateSha);
        Assert.Equal(expected.ReceiptId, entry.ReceiptId);
        Assert.Equal(expected.Selections, entry.Selections);
        Assert.Equal(expected.NotRun, entry.NotRun);
        Assert.Equal(expected.ResultPath, entry.ResultPath);
        Assert.Equal(expected.FailingTests, entry.FailingTests);
        Assert.Equal(expected.AbandonmentReason, entry.AbandonmentReason);
        Assert.Equal(PreTesterEvidenceIndexLines.FormatMarker(expected), evt.Message);
    }
}
