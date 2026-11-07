using System.Reflection;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: every scenario owns its repository, storage roots and coordinator.
public sealed class AcceptanceCohortWorkflowTestsCapacityIsolation : AcceptanceCohortWorkflowBackgroundSupport
{
    [Fact]
    public void IndependentWorkspaceDrivers_OverlapWithoutSharingStorageRoots()
    {
        var repositories = new List<string>();
        var drivers = new List<ConductorDriver>();
        var roots = new List<DotnetBuildStorageRoot>();
        var verifiers = new List<BlockingAcceptanceVerifier>();
        using var firstStarted = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var starts = new[] { firstStarted, secondStarted };
        var drained = true;
        try
        {
            for (var index = 0; index < starts.Length; index++)
            {
                var repo = CreateReducedAcceptanceCohortRepository();
                repositories.Add(repo);
                AddAcceptanceManifest(repo);
                var kernel = new AgentOrchestratorKernel();
                var first = CreateCompletedGoal(kernel, $"Workspace {index} first member", repo);
                var second = CreateCompletedGoal(kernel, $"Workspace {index} second member", repo);
                _ = CreateWorktreeCandidate(repo, first.Id,
                    "src/Mcg.AgentOrchestrator.Core/IndependentFirst.cs", "first");
                _ = CreateWorktreeCandidate(repo, second.Id,
                    "tests/IndependentSecond.cs", "second");
                var context = CreateIsolatedCleanupContext(repo);
                roots.Add(context.Hooks.BuildStorageRoot!);
                var verifier = new BlockingAcceptanceVerifier(starts[index], release,
                    new AcceptanceVerificationResult(
                        Passed: true,
                        Skipped: false,
                        ExitCode: 0,
                        OutputTail: null,
                        Checks: [new AcceptanceCheckResult("independent-root-gate", true, 0, null)],
                        TestResultPaths: [WritePassingTrx(repo, "independent-root.trx")]));
                verifiers.Add(verifier);
                var driver = new ConductorDriver(kernel, OrchestratorWorkspace.ForDirectory(repo),
                    verifier, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                    cleanupHooks: context.Hooks);
                drivers.Add(driver);
                _ = driver.RunAcceptanceCohort(ProjectSelection(driver, first, second),
                    [first, second], ConductorAutonomyPolicy.Permissive, runGateInBackground: true);
            }

            Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(30)), "First workspace gate did not enter.");
            Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(30)), "Second workspace gate did not enter.");
            Assert.False(release.IsSet);
            Assert.NotEqual(roots[0].RootPath, roots[1].RootPath);
            for (var index = 0; index < drivers.Count; index++)
            {
                Assert.Equal(1, drivers[index].GetActiveAcceptanceCohortCapacity().ActiveRootCount);
                Assert.True(verifiers[index].StableSlotLeaseObserved);
                Assert.True(roots[index].ContainsPath(verifiers[index].StableSlotRootPath),
                    $"Workspace {index} acquired lease root '{verifiers[index].StableSlotRootPath}' " +
                    $"outside configured root '{roots[index].RootPath}'.");
                Assert.False(roots[1 - index].ContainsPath(verifiers[index].StableSlotRootPath));
            }
        }
        finally
        {
            release.Set();
            foreach (var driver in drivers)
            {
                drained &= SpinWait.SpinUntil(
                    () => driver.GetActiveAcceptanceCohortCapacity().ActiveRootCount == 0,
                    TimeSpan.FromSeconds(30));
            }
            if (drained)
            {
                foreach (var repo in repositories)
                    DeleteDirectory(repo);
            }
        }
        Assert.True(drained, "Independent workspace gate did not drain after release.");
    }

    [Fact]
    public void ProductionBatch_TwoLiveCohortRootsFillSharedAcceptanceCapacity()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var trx = Path.Combine(Path.GetTempPath(), $"cohort-shared-capacity-{Guid.NewGuid():N}.trx");
        using var gateStarted = new ManualResetEventSlim();
        using var gateRelease = new ManualResetEventSlim();
        var cleanupContext = CreateIsolatedCleanupContext(repo);
        ConductorDriver? driver = null;
        DrainedTeardown teardown;
        try
        {
            AddAcceptanceManifest(repo);
            File.WriteAllText(trx, ValidPassingTrx());
            var kernel = new AgentOrchestratorKernel();
            var goals = Enumerable.Range(0, 5)
                .Select(index => CreateCompletedGoal(kernel, $"Shared capacity member {index}", repo))
                .ToArray();
            var paths = new[]
            {
                "src/Mcg.AgentOrchestrator.Infrastructure/CapacityFirst.cs",
                "tests/CapacitySecond.cs",
                "src/Mcg.AgentOrchestrator.Core/CapacityThird.cs",
                "tests/CapacityFourth.cs",
                "src/Mcg.AgentOrchestrator.App/CapacityWaiter.cs"
            };
            for (var index = 0; index < goals.Length; index++)
            {
                _ = CreateWorktreeCandidate(repo, goals[index].Id, paths[index], $"capacity-{index}");
            }

            var verifier = new BlockingAcceptanceVerifier(
                gateStarted,
                gateRelease,
                new AcceptanceVerificationResult(
                    Passed: true,
                    Skipped: false,
                    ExitCode: 0,
                    OutputTail: null,
                    Checks: [new AcceptanceCheckResult("shared-capacity-gate", true, 0, null)],
                    TestResultPaths: [trx]));
            driver = new ConductorDriver(
                kernel,
                OrchestratorWorkspace.ForDirectory(repo),
                verifier,
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                cleanupHooks: cleanupContext.Hooks);
            var first = ProjectSelection(driver, goals[0], goals[1]);
            var second = ProjectSelection(driver, goals[2], goals[3]);

            _ = driver.RunAcceptanceCohort(
                first,
                [goals[0], goals[1]],
                ConductorAutonomyPolicy.Permissive,
                runGateInBackground: true);
            _ = driver.RunAcceptanceCohort(
                second,
                [goals[2], goals[3]],
                ConductorAutonomyPolicy.Permissive,
                runGateInBackground: true);
            Assert.True(
                SpinWait.SpinUntil(() => verifier.RunCount == 2, TimeSpan.FromSeconds(10)),
                "Both controlled cohort roots did not enter the verifier.");

            var capacity = driver.GetActiveAcceptanceCohortCapacity();
            Assert.Equal(2, capacity.ActiveRootCount);
            BatchTickSummary? tick = null;
            AdmissionFactsTestData.Warm(driver, kernel);
            new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Permissive,
                Path.Combine(repo, "stop-does-not-exist"),
                maxIterations: 1,
                onTick: current => tick = current);

            Assert.Empty(driver.ParallelAcceptanceAttemptCoordinator.GetUnreconciledAttempts([goals[4].Id.Value]));
            Assert.Equal(GoalStatus.Verified, goals[4].Status);
            Assert.Contains(tick!.ProgressLines!, line =>
                line.Contains("reason=parallel-acceptance-slot-cap", StringComparison.Ordinal));
        }
        finally
        {
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

    [Fact]
    public void CohortStableSlotLease_RefusesToExceedTrustedHostGateCap()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cohort-slot-cap-{Guid.NewGuid():N}");
        var storageRoot = new DotnetBuildStorageRoot(root);
        try
        {
            using var first = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
                DotnetBuildEnvironmentManager.TryAcquireStableSlotExecutionLock(0, TimeSpan.Zero, storageRoot: storageRoot)).Lease;
            using var second = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
                DotnetBuildEnvironmentManager.TryAcquireStableSlotExecutionLock(1, TimeSpan.Zero, storageRoot: storageRoot)).Lease;
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(root, "attempts"), Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
                runInline: true, buildStorageRoot: storageRoot);

            Assert.Throws<DotnetBuildSlotsBusyException>(() =>
                coordinator.AcquireCohortStableSlotLease("cohort-v2-slot-cap", timeout: TimeSpan.Zero));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }
}
