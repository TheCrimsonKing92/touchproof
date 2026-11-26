using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static AttributedFailureDeveloperRouteTests;
using static SoloRepairWorkflowTests;

public sealed class SoloRepairCheckAttemptTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);
    private static readonly SpawnProcessIdentity Owner = new(1, DateTimeOffset.UnixEpoch, "test-child");

    [Theory]
    [InlineData("inherited")]
    [InlineData("introduced")]
    [InlineData("passed")]
    [InlineData("failed")]
    public async Task Advance_WhileCheckIsBlockedHoldsOnce_ThenConsumesTheInlineEquivalent(string outcome)
    {
        var validation = outcome is "passed" or "failed";
        using var fixture = new SoloAttributedFailureRouteFixture();
        var goal = fixture.Kernel.GetGoal(fixture.Goal.Id);
        var unmodified = Goal.FromSnapshot(goal.ToSnapshot());
        await fixture.Produce(baselineFails: true, singleRow: true);
        if (!validation)
            fixture.ReplaceProof(proof => proof with { Checks = proof.Checks.Select(item => item with { Artifacts = [] }).ToArray() });
        var summary = fixture.Summary;
        fixture.Kernel.RecordAcceptanceFailure(goal.Id, summary.FailedChecks!, summary.BranchHeadSha, summary.MainHeadSha,
            summary.CheckAttributions, summary.BaselineAttestation, summary.AttemptId, summary.SoloFailureReceipt);
        var finding = Assert.Single(Assert.IsType<SoloRepairPartition>(
            SoloAcceptanceFailureReceiptReader.ReadRepairPartition(goal, fixture.Context, summary)).Findings);
        Assert.Equal(validation, finding.Inherited);

        var main = fixture.Context.MainSha;
        string? repairId = null;
        if (validation)
        {
            repairId = CompletedGoal(fixture.Kernel).Id.Value;
            fixture.Git("checkout", "main");
            fixture.Git("commit", "--allow-empty", "-m", $"Integrate goal/{repairId}: baseline repair");
            main = fixture.Git("rev-parse", "main");
        }
        var pair = new GateReadyCandidateRevisionPair(fixture.Context.CandidateSha, main);
        var workspace = Workspace(fixture);
        var store = Seed(workspace, unmodified, finding, repairId);
        var key = validation ? SoloBaselineRepairStore.ValidationKey(finding, main) :
            store.ResolveAttributionRequest(unmodified.Id.Value, finding, fixture.Context.CandidateSha, main).Key;

        var inlineWorkspace = OrchestratorWorkspace.ForProject(workspace.ProjectName, fixture.Repo, integrationBranch: "main",
            dataRootDirectory: Path.Combine(fixture.Root, "inline-data"));
        Directory.CreateDirectory(inlineWorkspace.OrchestratorDirectory);
        _ = StateDbMigrations.EnsureUpToDate(inlineWorkspace.SqliteStatePath);
        var inlineStore = Seed(inlineWorkspace, unmodified, finding, repairId);
        var inline = Coordinator(inlineWorkspace, NewVerifier((_, _, _) => CheckName()), (_, _) => Lease(inlineWorkspace));
        var expected = Observe(inline, unmodified);
        var expectedReceipt = Assert.IsType<SoloRepairCheckReceipt>(inlineStore.ReadCheck(key));
        Assert.Equal(outcome, expectedReceipt.Outcome);

        var enteredSlot = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verifierThread = 0;
        var verifier = NewVerifier((_, _, _) =>
        {
            verifierThread = Environment.CurrentManagedThreadId;
            return CheckName();
        });
        var launches = 0;
        Task<int>? child = null;
        SoloRepairCheckRequest? observed = null;
        var attempts = AttemptCoordinator(workspace, (path, id) =>
        {
            launches++;
            child = Task.Factory.StartNew(() => SoloRepairCheckAttemptCoordinator.RunAttempt(path, id, attempt =>
            {
                observed = attempt.Request;
                return SoloRepairCheckRunner.Run(workspace, store, verifier, (_, _) =>
                {
                    enteredSlot.SetResult(Environment.CurrentManagedThreadId);
                    releaseSlot.Task.GetAwaiter().GetResult();
                    return Lease(workspace);
                }, attempt.Request);
            }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            return Owner;
        });
        var background = Coordinator(workspace, verifier, (_, _) => throw new InvalidOperationException("Tick acquired the build slot."), attempts);
        var tick = Task.Factory.StartNew(() =>
        {
            var caller = Environment.CurrentManagedThreadId;
            AssertPending(background, unmodified, finding.Request!);
            return caller;
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        int tickThread;
        try
        {
            tickThread = await tick.WaitAsync(HangGuard);
            var slotThread = await enteredSlot.Task.WaitAsync(HangGuard);
            Assert.NotEqual(tickThread, slotThread);
            AssertPending(background, unmodified, finding.Request!);
            Assert.Equal(1, launches);
            Assert.Equal(1, attempts.CountLiveAttempts());
            Assert.Null(store.ReadCheck(key));
        }
        finally
        {
            releaseSlot.TrySetResult();
            if (child is not null)
                Assert.Equal(0, await child.WaitAsync(HangGuard));
        }

        Assert.NotEqual(0, verifierThread);
        Assert.NotEqual(tickThread, verifierThread);
        Assert.Equal(key, observed?.Key);
        Assert.Equal(!validation, observed?.RunBaseline);
        Assert.Equal(expected, Observe(background, unmodified));
        var actual = Assert.IsType<SoloRepairCheckReceipt>(store.ReadVerifiedCheck(key, observed!.ProofIdentity));
        Assert.Equal(expectedReceipt.Outcome, actual.Outcome);
        Assert.Equal(expectedReceipt.Detail, actual.Detail);
        Assert.Equal(expectedReceipt.LaneEnvironmentVerified, actual.LaneEnvironmentVerified);
        Assert.Equal(expectedReceipt.Artifacts.Select(TrxContent), actual.Artifacts.Select(TrxContent));
        Assert.Equal(expectedReceipt.CandidateArtifacts?.Select(TrxContent), actual.CandidateArtifacts?.Select(TrxContent));
        Assert.Equal(expectedReceipt.Feedback, actual.Feedback);
        Assert.NotNull(actual.Proof);
        Assert.Equal(1, launches);
        Assert.Single(verifier.FocusedRuns);
        Assert.Equal(0, attempts.CountLiveAttempts());

        string CheckName() => finding.BaselineCheck ?? finding.Check;

        ControlledVerifier NewVerifier(Func<string, string, bool, string>? selection = null) => new(fixture.Root)
        {
            Finding = finding, AttributionBaselinePass = outcome == "introduced", RefreshedBaselinePass = outcome == "passed",
            FocusedSelection = selection
        };

        SoloBaselineRepairCoordinator Coordinator(OrchestratorWorkspace state, ControlledVerifier runner,
            Func<string, CancellationToken, DotnetBuildEnvironmentLease> acquire, SoloRepairCheckAttemptCoordinator? owned = null) =>
            new(state, fixture.Kernel, runner, _ => pair, _ => null,
                () => throw new RouteObservedException(), _ => new(true, false, false, false, false, false),
                (_, _, _, _) => LandingEscalationWriteResult.CompletedWithoutExternalSinks, acquire, checkAttempts: owned);
    }

    [Fact]
    public async Task Restart_ObservesLiveOwnerAndHeldMutex_ThenCompletesStagedCheckWithoutExecuting()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        var workspace = Workspace(fixture);
        var store = new SoloBaselineRepairStore(Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName));
        var main = fixture.Context.MainSha;
        var finding = SoloRepairReplayTests.Finding() with { Baseline = main };
        var key = SoloBaselineRepairStore.ValidationKey(finding, main);
        var request = new SoloRepairCheckRequest(key, finding, main, main, false, false,
            SoloBaselineRepairStore.FocusedProofIdentity(key, finding, main, main, false, false));
        var launched = new List<(string Path, Guid Id)>();
        SpawnProcessIdentity Launch(string path, Guid id)
        {
            launched.Add((path, id));
            return Owner;
        }

        AttemptCoordinator(workspace, Launch).StartOrObserve(store, request);
        var first = Assert.Single(launched);
        AttemptCoordinator(workspace, Launch).StartOrObserve(store, request);
        Assert.Single(launched);

        var staged = RecordPassedValidation(store, finding, main);
        SoloRepairReplayTests.ExecuteSql(Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName),
            "UPDATE solo_repair_check SET result = NULL");
        store.RecordCheckExecution(key, staged);
        var recovered = AttemptCoordinator(workspace, Launch, _ => SpawnTrackedProcessStatus.DeadOrRecycled);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var legacy = Task.Factory.StartNew(() =>
        {
            using var mutex = SoloRepairCheckRunner.OpenOwnership(workspace.SqliteStatePath, key);
            Assert.True(SoloRepairCheckRunner.TryTakeOwnership(mutex));
            try
            {
                held.SetResult();
                release.Task.GetAwaiter().GetResult();
            }
            finally { mutex.ReleaseMutex(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await held.Task.WaitAsync(HangGuard);
            recovered.StartOrObserve(store, request);
            Assert.Single(launched);
            Assert.Null(store.ReadCheck(key));
        }
        finally
        {
            release.TrySetResult();
            await legacy.WaitAsync(HangGuard);
        }

        recovered.StartOrObserve(store, request);
        Assert.Equal(2, launched.Count);
        var second = launched[1];
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(0, SoloRepairCheckAttemptCoordinator.RunAttempt(first.Path, first.Id,
            _ => throw new InvalidOperationException("A superseded child must not execute.")));
        var verifier = new ControlledVerifier(fixture.Root);
        Assert.Equal(0, SoloRepairCheckAttemptCoordinator.RunAttempt(second.Path, second.Id, attempt =>
            SoloRepairCheckRunner.Run(workspace, store, verifier,
                (_, _) => throw new InvalidOperationException("Recovery must consume staged proof before acquiring a slot."),
                attempt.Request)));
        Assert.Equal(staged.Proof, store.ReadVerifiedCheck(key, request.ProofIdentity)?.Proof);
        Assert.Null(store.ReadCheckExecution(key));
        Assert.Empty(verifier.FocusedRuns);
    }

    private static SoloRepairCheckAttemptCoordinator AttemptCoordinator(OrchestratorWorkspace workspace,
        Func<string, Guid, SpawnProcessIdentity?> launch, Func<SpawnProcessIdentity, SpawnTrackedProcessStatus>? classify = null) =>
        new(workspace, new DotnetBuildStorageRoot(Path.Combine(workspace.OrchestratorDirectory, "build")),
            launch, classify ?? (_ => SpawnTrackedProcessStatus.LiveMatch));

    private static SoloBaselineRepairStore Seed(OrchestratorWorkspace workspace, Goal goal, SoloRepairFinding finding, string? repairId)
    {
        var store = new SoloBaselineRepairStore(Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName));
        store.Retain(goal.Id.Value, [finding]);
        if (repairId is not null)
        {
            store.ReserveGroup(finding.GroupKey, "{}");
            store.AcknowledgeRepair(finding.GroupKey, repairId);
        }
        return store;
    }

    private static DotnetBuildEnvironmentLease Lease(OrchestratorWorkspace workspace) =>
        AcceptanceStableSlotTestSupport.CreateFakeStableSlotLease(workspace.OrchestratorDirectory);

    private static string TrxContent(SoloAcceptanceArtifact artifact)
    {
        var root = XElement.Load(artifact.Path);
        root.Attribute("id")?.Remove();
        return root.ToString(SaveOptions.DisableFormatting);
    }

    private static ConductorAdvanceResult? Advance(SoloBaselineRepairCoordinator coordinator, Goal goal) =>
        coordinator.TryAdvance(goal, goal.Id.Value[..8], ConductorAutonomyPolicy.Permissive);

    private static void AssertPending(SoloBaselineRepairCoordinator coordinator, Goal goal, string request)
    {
        var outcome = Assert.IsType<ConductorAdvanceOutcome.Held>(Advance(coordinator, goal)?.Outcome);
        Assert.Equal(SoloHoldReasons.RepairPending, outcome.Decision?.DiscriminatingEvidence);
        Assert.Contains(request, outcome.Reason, StringComparison.Ordinal);
    }

    private static (string? Reason, string? Detail) Observe(SoloBaselineRepairCoordinator coordinator, Goal goal)
    {
        try
        {
            var outcome = Advance(coordinator, goal)?.Outcome;
            if (outcome is null) return (null, null);
            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(outcome);
            return (held.Decision?.DiscriminatingEvidence, held.Reason);
        }
        catch (RouteObservedException)
        {
            return ("route to Developer", null);
        }
    }

    private sealed class RouteObservedException : Exception;
}
