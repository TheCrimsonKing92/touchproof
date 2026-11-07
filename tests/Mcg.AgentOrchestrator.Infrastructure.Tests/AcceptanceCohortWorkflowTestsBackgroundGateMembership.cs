using System.Reflection;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: every scenario owns its repository, storage roots and coordinator.
public sealed class AcceptanceCohortWorkflowTestsBackgroundGateMembership : AcceptanceCohortWorkflowBackgroundSupport
{
    [Fact]
    public void BackgroundCohortGate_MainAdvanceDoesNotLaunchDuplicateForSameMembers()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var trx = Path.Combine(Path.GetTempPath(), $"cohort-member-pair-{Guid.NewGuid():N}.trx");
        using var gateStarted = new ManualResetEventSlim();
        using var gateRelease = new ManualResetEventSlim();
        var cleanupContext = CreateIsolatedCleanupContext(repo);
        DrainedTeardown teardown;
        try
        {
            AddAcceptanceManifest(repo);
            File.WriteAllText(trx, ValidPassingTrx());
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First stable member-pair member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second stable member-pair member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs",
                "first");
            _ = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var verifier = new BlockingAcceptanceVerifier(
                gateStarted,
                gateRelease,
                new AcceptanceVerificationResult(
                    Passed: true,
                    Skipped: false,
                    ExitCode: 0,
                    OutputTail: null,
                    Checks: [new AcceptanceCheckResult("shared-member-pair-gate", true, 0, null)],
                    TestResultPaths: [trx]));
            var driver = new ConductorDriver(
                kernel,
                OrchestratorWorkspace.ForDirectory(repo),
                verifier,
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                cleanupHooks: cleanupContext.Hooks);
            var selection = ProjectSelection(driver, firstGoal, secondGoal);
            var originalPairFingerprint = ConductorAcceptanceCohortSelector.PairFingerprint(
                selection.Members[0],
                selection.Members[1]);

            var first = driver.RunAcceptanceCohort(
                selection,
                [firstGoal, secondGoal],
                ConductorAutonomyPolicy.Permissive,
                runGateInBackground: true);

            Assert.Contains("outcome=inflight", first.Detail, StringComparison.Ordinal);
            Assert.True(gateStarted.Wait(TimeSpan.FromSeconds(10)), "The controlled cohort verifier did not start.");
            Assert.Equal(1, verifier.RunCount);
            Assert.Single(Directory.EnumerateDirectories(
                Path.Combine(repo, GoalWorktrees.DirectoryName),
                "c-*"));

            File.WriteAllText(Path.Combine(repo, "main-advanced.txt"), "main advanced while cohort gate remained held");
            RunGit(repo, "add", "main-advanced.txt");
            RunGit(repo, "commit", "-m", "Advance main during cohort gate");
            var revisedSelection = ProjectSelection(driver, firstGoal, secondGoal);
            Assert.NotEqual(selection.Members[0].MainRevision, revisedSelection.Members[0].MainRevision);
            Assert.NotEqual(
                originalPairFingerprint,
                ConductorAcceptanceCohortSelector.PairFingerprint(
                    revisedSelection.Members[0],
                    revisedSelection.Members[1]));

            var held = driver.RunAcceptanceCohort(
                revisedSelection,
                [firstGoal, secondGoal],
                ConductorAutonomyPolicy.Permissive,
                runGateInBackground: true);

            Assert.Contains("outcome=inflight", held.Detail, StringComparison.Ordinal);
            Assert.Contains($"fingerprint={originalPairFingerprint}", held.Detail, StringComparison.Ordinal);
            Assert.Equal(1, verifier.RunCount);
            Assert.Single(Directory.EnumerateDirectories(
                Path.Combine(repo, GoalWorktrees.DirectoryName),
                "c-*"));
        }
        finally
        {
            gateRelease.Set();
            teardown = RemoveAfterObservedDrain(
                () => !Directory.Exists(repo) ||
                      !Directory.EnumerateDirectories(
                          Path.Combine(repo, GoalWorktrees.DirectoryName),
                          "c-*").Any(),
                "no c-* cohort worktree remains under repo",
                TimeSpan.FromSeconds(10),
                () => { if (File.Exists(trx)) File.Delete(trx); },
                () => DeleteDirectory(repo));
        }
        teardown.AssertDrained();
    }

    [Fact]
    public void BackgroundCohortGate_OverlappingMembersAreHeldByActualOwner()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var trx = Path.Combine(Path.GetTempPath(), $"cohort-overlap-{Guid.NewGuid():N}.trx");
        using var gateStarted = new ManualResetEventSlim();
        using var gateRelease = new ManualResetEventSlim();
        var cleanupContext = CreateIsolatedCleanupContext(repo);
        ConductorDriver? driver = null;
        TaskCompletionSource? registrationProbe = null;
        DrainedTeardown teardown;
        try
        {
            AddAcceptanceManifest(repo);
            File.WriteAllText(trx, ValidPassingTrx());
            var kernel = new AgentOrchestratorKernel();
            var goals = Enumerable.Range(0, 3)
                .Select(index => CreateCompletedGoal(kernel, $"Overlapping cohort member {index}", repo))
                .ToArray();
            var paths = new[]
            {
                "src/Mcg.AgentOrchestrator.Infrastructure/OverlapFirst.cs",
                "tests/OverlapSecond.cs",
                "src/Mcg.AgentOrchestrator.Core/OverlapThird.cs"
            };
            for (var index = 0; index < goals.Length; index++)
            {
                _ = CreateWorktreeCandidate(repo, goals[index].Id, paths[index], $"overlap-{index}");
            }

            var verifier = new BlockingAcceptanceVerifier(
                gateStarted,
                gateRelease,
                new AcceptanceVerificationResult(
                    Passed: true,
                    Skipped: false,
                    ExitCode: 0,
                    OutputTail: null,
                    Checks: [new AcceptanceCheckResult("overlap-owner-gate", true, 0, null)],
                    TestResultPaths: [trx]));
            driver = new ConductorDriver(
                kernel,
                OrchestratorWorkspace.ForDirectory(repo),
                verifier,
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                cleanupHooks: cleanupContext.Hooks);
            var policy = ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 2 };
            var ownerSelection = ProjectSelection(driver, goals[0], goals[1]);
            var overlappingSelection = ProjectSelection(driver, goals[1], goals[2]);

            _ = driver.RunAcceptanceCohort(
                ownerSelection,
                goals,
                policy,
                runGateInBackground: true);
            registrationProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.False(driver.TryRegisterCohortGateRunForTests(overlappingSelection, registrationProbe));
            var held = driver.RunAcceptanceCohort(
                overlappingSelection,
                goals,
                policy,
                runGateInBackground: true);

            var owner = string.Join(
                "+",
                ownerSelection.Members.Select(member => member.GoalId.Value).OrderBy(id => id, StringComparer.Ordinal));
            var capacity = driver.GetActiveAcceptanceCohortCapacity();
            Assert.Equal(1, capacity.ActiveRootCount);
            Assert.Single(capacity.ActiveRoots.Where(root => root.MemberGoalIds.Contains(goals[1].Id.Value)));
            Assert.Contains("outcome=inflight", held.Detail, StringComparison.Ordinal);
            Assert.Contains($"owner={owner}", held.Detail, StringComparison.Ordinal);
            Assert.True(driver.TryGetCohortGateHold(goals[1].Id, out var hold));
            Assert.Contains($"owner={owner}", hold, StringComparison.Ordinal);
            Assert.False(driver.TryGetCohortGateHold(goals[2].Id, out _));
        }
        finally
        {
            registrationProbe?.TrySetResult();
            gateRelease.Set();
            teardown = RemoveAfterObservedDrain(
                () => driver is null || driver.GetActiveAcceptanceCohortCapacity().ActiveRootCount == 0,
                "ActiveRootCount == 0",
                TimeSpan.FromSeconds(10),
                () => { if (File.Exists(trx)) File.Delete(trx); },
                () => DeleteDirectory(repo));
        }
        teardown.AssertDrained();
    }

    [Theory]
    [InlineData("succeeded")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task BackgroundCohortGate_TerminalRunReleasesMembersWhileDisjointRunRemainsActive(
        string terminalState)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var cleanupContext = CreateIsolatedCleanupContext(repo);
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goals = Enumerable.Range(0, 5)
                .Select(index => CreateCompletedGoal(kernel, $"Terminal cohort member {index}", repo))
                .ToArray();
            using var unusedStarted = new ManualResetEventSlim();
            using var unusedRelease = new ManualResetEventSlim();
            var driver = new ConductorDriver(
                kernel,
                OrchestratorWorkspace.ForDirectory(repo),
                new BlockingAcceptanceVerifier(
                    unusedStarted,
                    unusedRelease,
                    new AcceptanceVerificationResult(
                        Passed: true,
                        Skipped: false,
                        ExitCode: 0,
                        OutputTail: null,
                        Checks: [],
                        TestResultPaths: [])),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                cleanupHooks: cleanupContext.Hooks);
            var ownerSelection = TestSelection(goals[0], goals[1]);
            var disjointSelection = TestSelection(goals[2], goals[3]);
            var releasedSelection = TestSelection(goals[0], goals[4]);
            var ownerCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var disjointCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releasedCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            Assert.True(driver.TryRegisterCohortGateRunForTests(ownerSelection, ownerCompletion));
            Assert.True(driver.TryRegisterCohortGateRunForTests(disjointSelection, disjointCompletion));
            Assert.Equal(2, driver.GetActiveAcceptanceCohortCapacity().ActiveRootCount);

            switch (terminalState)
            {
                case "succeeded":
                    ownerCompletion.SetResult();
                    await ownerCompletion.Task;
                    break;
                case "failed":
                    ownerCompletion.SetException(new InvalidOperationException("controlled cohort failure"));
                    await Assert.ThrowsAsync<InvalidOperationException>(async () => await ownerCompletion.Task);
                    break;
                case "cancelled":
                    ownerCompletion.SetCanceled();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await ownerCompletion.Task);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown terminal state '{terminalState}'.");
            }

            var activeMembers = driver.GetActiveCohortGateMemberGoalIds();
            Assert.DoesNotContain(goals[0].Id.Value, activeMembers);
            Assert.DoesNotContain(goals[1].Id.Value, activeMembers);
            Assert.Contains(goals[2].Id.Value, activeMembers);
            Assert.Contains(goals[3].Id.Value, activeMembers);
            Assert.True(driver.TryRegisterCohortGateRunForTests(releasedSelection, releasedCompletion));

            disjointCompletion.SetResult();
            releasedCompletion.SetResult();
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void BackgroundCohortGate_SamePairCanRegisterWhenIncumbentCompletesAfterSweep()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var cleanupContext = CreateIsolatedCleanupContext(repo);
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goals = Enumerable.Range(0, 2)
                .Select(index => CreateCompletedGoal(kernel, $"Same-pair cohort member {index}", repo))
                .ToArray();
            using var unusedStarted = new ManualResetEventSlim();
            using var unusedRelease = new ManualResetEventSlim();
            var driver = new ConductorDriver(
                kernel,
                OrchestratorWorkspace.ForDirectory(repo),
                new BlockingAcceptanceVerifier(
                    unusedStarted,
                    unusedRelease,
                    new AcceptanceVerificationResult(
                        Passed: true,
                        Skipped: false,
                        ExitCode: 0,
                        OutputTail: null,
                        Checks: [],
                        TestResultPaths: [])),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                cleanupHooks: cleanupContext.Hooks);
            var selection = TestSelection(goals[0], goals[1]);
            var incumbentCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var replacementCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            Assert.True(driver.TryRegisterCohortGateRunForTests(selection, incumbentCompletion));
            var registered = false;
            var exception = Record.Exception(() =>
                registered = driver.TryRegisterCohortGateRunForTests(
                    selection,
                    replacementCompletion,
                    incumbentCompletion.SetResult));

            Assert.Null(exception);
            Assert.True(registered);
            replacementCompletion.SetResult();
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    private static ConductorAcceptanceCohortSelection TestSelection(Goal first, Goal second)
    {
        var mainRevision = new string('c', 40);
        return new ConductorAcceptanceCohortSelection(
            [
                TestProjection(first.Id, new string('a', 40), mainRevision, $"src/{first.Id.Value[..8]}.cs"),
                TestProjection(second.Id, new string('b', 40), mainRevision, $"tests/{second.Id.Value[..8]}.cs")
            ],
            []);
    }

    private static GateReadyCandidateProjection TestProjection(
        GoalId goalId,
        string branchRevision,
        string mainRevision,
        string landingPath) =>
        new(
            goalId,
            GoalLifecycleState.Verified,
            GateReadyVerificationState.Satisfied,
            ChangeRiskTier.DocsOnly,
            ConductorTransitionDecision.Auto,
            [landingPath],
            [$"production:{goalId.Value[..8]}"],
            new GateReadyMergeEvidence(
                branchRevision,
                mainRevision,
                GateReadyMergeStatus.Clean,
                GateReadyMergeReason.NoConflictsDetected));
}
