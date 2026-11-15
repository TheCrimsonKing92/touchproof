using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel safe: each integration test owns and deletes its GUID-named SQLite store.
public sealed class GoalSnapshotObligationMergeTests
{
    private const string CandidateSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherCandidateSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ObligationId = "criterion-v1-0";
    private const string VersionThreeCandidateSha = "cccccccccccccccccccccccccccccccccccccccc";
    private const string VersionThreeObligationId = "criterion-v3-4";
    private const string OperatorCriterion = "Observe the acknowledged candidate behavior";
    private const string OperatorScope = "operator observation of acknowledged candidate";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V3Index4OperatorMap_StaleTick_PreservesSavedBinding(bool conflictingTick)
    {
        using var store = new TempStore();
        var repository = new SqliteOrchestratorStateRepository(store.Path);
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVersionThreeGoal(kernel);
        await repository.SaveAsync(kernel);
        var tick = await repository.LoadAsync();
        var baseline = tick.ExportSnapshot();
        Assert.DoesNotContain(baseline.Goals.Single().CriterionEvidenceObligations!,
            item => item.CriterionVersion == 3 && item.CriterionIndex == 4);

        var second = await repository.LoadAsync();
        var mapped = MapVersionThreeOperator(second, goal.Id);
        await repository.SaveAsync(second);
        var afterSave = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(goal.Id));
        AssertVersionThreeBinding(mapped, afterSave.CriterionEvidenceObligations!);

        if (conflictingTick)
            tick.MapCriterionEvidenceOwner(goal.Id, 4, 3, CriterionEvidenceOwner.Acceptance,
                "stale tick", expectedCandidateSha: OtherCandidateSha);
        else
            Assert.DoesNotContain(tick.GetGoal(goal.Id).CriterionEvidenceObligations,
                item => item.Id == VersionThreeObligationId);
        tick.SetGoalPriority(goal.Id, GoalPriority.Critical, "test", "tick");
        var current = tick.ExportSnapshot();

        var result = Assert.Single(await repository.SaveGoalSnapshotsWithMergeAsync([
            new GoalSnapshotSaveRequest(baseline.Goals.Single(), current.Goals.Single(), current.HumanInputRequests)]));

        Assert.Equal(GoalSnapshotSaveDisposition.Merged, result.Disposition);
        if (conflictingTick)
            Assert.Contains($"criterion evidence obligation conflict kept stored: {VersionThreeObligationId}",
                result.Message, StringComparison.Ordinal);
        var afterMerge = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(goal.Id));
        AssertVersionThreeBinding(mapped, afterMerge.CriterionEvidenceObligations!);
        var restored = (await repository.LoadAsync()).GetGoal(goal.Id);
        AssertVersionThreeBinding(mapped, restored.CriterionEvidenceObligations);
        Assert.Equal(GoalPriority.Critical, restored.Priority);
    }

    [Fact]
    public async Task V3Index4OperatorMap_Restore_RetainsBindingAndRetiredRows()
    {
        using var store = new TempStore();
        var repository = new SqliteOrchestratorStateRepository(store.Path);
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVersionThreeGoal(kernel);
        var mapped = MapVersionThreeOperator(kernel, goal.Id);
        var retired = goal.CriterionEvidenceObligations.Where(item => item.CriterionVersion < 3).ToArray();
        Assert.Equal(new[] { 1, 2 }, retired.Select(item => item.CriterionVersion));
        Assert.All(retired, item => Assert.StartsWith("retired:", item.Detail));
        var rawRetired = kernel.ExportSnapshot().Goals.Single().CriterionEvidenceObligations!
            .Where(item => item.CriterionVersion < 3).ToArray();
        Assert.All(rawRetired, item => Assert.Null(item.Detail));
        await repository.SaveAsync(kernel);
        var afterSave = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(goal.Id));
        AssertVersionThreeBinding(mapped, afterSave.CriterionEvidenceObligations!);
        Assert.Equal(rawRetired, afterSave.CriterionEvidenceObligations!.Where(item => item.CriterionVersion < 3));

        var tick = await repository.LoadAsync();
        var baseline = tick.ExportSnapshot();
        var second = await repository.LoadAsync();
        second.SetGoalSourceBacklogItemId(goal.Id, "store-write-after-owner-map");
        await repository.SaveAsync(second);
        tick.SetGoalPriority(goal.Id, GoalPriority.Critical, "test", "tick");
        var current = tick.ExportSnapshot();
        var result = Assert.Single(await repository.SaveGoalSnapshotsWithMergeAsync([
            new GoalSnapshotSaveRequest(baseline.Goals.Single(), current.Goals.Single(), current.HumanInputRequests)]));

        Assert.Equal(GoalSnapshotSaveDisposition.Merged, result.Disposition);
        var afterMerge = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(goal.Id));
        AssertVersionThreeBinding(mapped, afterMerge.CriterionEvidenceObligations!);
        Assert.Equal(rawRetired, afterMerge.CriterionEvidenceObligations!.Where(item => item.CriterionVersion < 3));
        var restoredKernel = await repository.LoadAsync();
        var restored = restoredKernel.GetGoal(goal.Id);
        Assert.Equal(3, restored.AuthoritativeRefinedSpecVersion!.Version);
        AssertVersionThreeBinding(mapped, restored.CriterionEvidenceObligations);
        Assert.Equal(retired, restored.CriterionEvidenceObligations.Where(item => item.CriterionVersion < 3));
        Assert.Equal(rawRetired, restoredKernel.ExportSnapshot().Goals.Single().CriterionEvidenceObligations!
            .Where(item => item.CriterionVersion < 3));
        Assert.DoesNotContain(restored.OutstandingCriterionEvidenceObligations, item => item.CriterionVersion < 3);
    }

    [Fact]
    public async Task TickAppliedV3Index4OperatorMap_MergedCheckpoint_PreservesBinding()
    {
        using var store = new TempStore();
        var repository = new SqliteOrchestratorStateRepository(store.Path);
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVersionThreeGoal(kernel);
        await repository.SaveAsync(kernel);
        var tick = await repository.LoadAsync();
        var baseline = tick.ExportSnapshot();
        var mapped = MapVersionThreeOperator(tick, goal.Id);
        AssertVersionThreeBinding(mapped, tick.GetGoal(goal.Id).CriterionEvidenceObligations);

        var second = await repository.LoadAsync();
        second.SetGoalSourceBacklogItemId(goal.Id, "store-write-before-mapped-checkpoint");
        await repository.SaveAsync(second);
        var beforeMerge = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(goal.Id));
        Assert.DoesNotContain(beforeMerge.CriterionEvidenceObligations!, item => item.Id == VersionThreeObligationId);
        var current = tick.ExportSnapshot();
        AssertVersionThreeBinding(mapped, current.Goals.Single().CriterionEvidenceObligations!);

        // Historical writer: before 7e8895d16, SqliteOrchestratorStateRepository.TryMergeGoalSnapshots
        // copied the stored obligation list and lost tick-added mappings at this boundary.
        // This guards that mechanism; the original incident's failing boundary was not observed.
        var result = Assert.Single(await repository.SaveGoalSnapshotsWithMergeAsync([
            new GoalSnapshotSaveRequest(baseline.Goals.Single(), current.Goals.Single(), current.HumanInputRequests)]));

        Assert.Equal(GoalSnapshotSaveDisposition.Merged, result.Disposition);
        var afterMerge = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(goal.Id));
        AssertVersionThreeBinding(mapped, afterMerge.CriterionEvidenceObligations!);
        var restored = (await repository.LoadAsync()).GetGoal(goal.Id);
        AssertVersionThreeBinding(mapped, restored.CriterionEvidenceObligations);
        Assert.Equal("store-write-before-mapped-checkpoint", restored.SourceBacklogItemId);
    }

    [Fact]
    public async Task V3Index4OperatorMap_AfterRoundTrip_ReviewerDefersWithOwnerNote()
    {
        using var store = new TempStore();
        var repository = new SqliteOrchestratorStateRepository(store.Path);
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVersionThreeGoal(kernel);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        await repository.SaveAsync(kernel);
        var tick = await repository.LoadAsync();
        var baseline = tick.ExportSnapshot();
        var second = await repository.LoadAsync();
        var mapped = MapVersionThreeOperator(second, goal.Id);
        await repository.SaveAsync(second);
        var afterSave = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(goal.Id));
        AssertVersionThreeBinding(mapped, afterSave.CriterionEvidenceObligations!);
        tick.SetGoalPriority(goal.Id, GoalPriority.Critical, "test", "tick");
        var current = tick.ExportSnapshot();
        var result = Assert.Single(await repository.SaveGoalSnapshotsWithMergeAsync([
            new GoalSnapshotSaveRequest(baseline.Goals.Single(), current.Goals.Single(), current.HumanInputRequests)]));
        Assert.Equal(GoalSnapshotSaveDisposition.Merged, result.Disposition);
        var afterMerge = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(goal.Id));
        AssertVersionThreeBinding(mapped, afterMerge.CriterionEvidenceObligations!);

        var restoredKernel = await repository.LoadAsync();
        var restored = restoredKernel.GetGoal(goal.Id);
        AssertVersionThreeBinding(mapped, restored.CriterionEvidenceObligations);
        var reviewer = Assert.Single(restored.Tasks);
        var startedAt = DateTimeOffset.UtcNow;
        restoredKernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review", "C:\\fixture", startedAt, BaseCommit: VersionThreeCandidateSha));
        var verdicts = JsonSerializer.Serialize(Enumerable.Range(0, 5).Select(index => new
        {
            criterion_index = index,
            verdict = index == 4 ? "not-verifiable" : "met",
            evidence = index == 4 ? "Operator observation remains pending" : "Source inspected"
        }));
        var output = string.Join(Environment.NewLine,
            "WORKER_RESULT:", "files: none", "commands: inspect", "tests: deferred - OperatorObservationTests",
            "commit: none", "blockers: none", "findings: []", "touched_anchors: []",
            $"criteria_verdicts: {verdicts}", "verdict: pass",
            "model_fit: fixture/model - adequate - deterministic review", "skills: none", "confidence: high",
            "END_WORKER_RESULT");
        restoredKernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review", "C:\\fixture", 0, output, string.Empty, DateTimeOffset.UtcNow,
            WorkerResultPresent: true, ReviewedCommit: VersionThreeCandidateSha,
            DispatchStartedAt: startedAt, DispatchAttemptAt: startedAt));

        Assert.Equal(VerificationTerminalContractStatus.Valid,
            WorkerResultBlockers.AssessStoredSubscriptionVerification(reviewer, reviewer.LastVerification!).Status);
        Assert.Equal(WorkTaskStatus.Completed, reviewer.Status);
        Assert.Equal(VersionThreeCandidateSha, reviewer.LastVerification!.ReviewedCommit);
        AssertVersionThreeBinding(mapped, restored.CriterionEvidenceObligations);
        Assert.Contains(restored.Timeline, item => item.Kind == ProgressKind.TaskNote && item.TaskId == reviewer.Id &&
            item.Message.Contains($"Deferred criterion evidence remains pending: obligation={mapped.DisplayLabel}; owner=Operator; state=Pending;",
                StringComparison.Ordinal) &&
            item.Message.Contains("reviewer_verdict=not-verifiable", StringComparison.Ordinal) &&
            item.Message.Contains($"expected_candidate={VersionThreeCandidateSha}", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("unmapped")]
    [InlineData("duplicate")]
    [InlineData("invalid")]
    [InlineData("unknown")]
    public async Task IneligibleOwner_AfterRoundTrip_RejectsDeferredAttestation(string ownership)
    {
        using var store = new TempStore();
        var repository = new SqliteOrchestratorStateRepository(store.Path);
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVersionThreeGoal(kernel);
        var mapped = MapVersionThreeOperator(kernel, goal.Id);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var snapshot = kernel.ExportSnapshot();
        var original = snapshot.Goals.Single();
        var rows = original.CriterionEvidenceObligations!.Where(item => item.Id != mapped.Id).ToList();
        switch (ownership)
        {
            case "duplicate": rows.AddRange([mapped, mapped]); break;
            case "invalid": rows.Add(mapped with { RequiredScope = string.Empty }); break;
            case "unknown": rows.Add(mapped with { Owner = CriterionEvidenceOwner.Unknown }); break;
        }
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = [original with { CriterionEvidenceObligations = rows }]
        });
        var expected = kernel.GetGoal(goal.Id).CriterionEvidenceObligations
            .Where(item => item.CriterionVersion == 3 && item.CriterionIndex == 4).ToArray();
        Assert.Equal(ownership == "unmapped" ? 0 : ownership == "duplicate" ? 2 : 1, expected.Length);
        if (ownership != "unmapped")
            Assert.Contains(expected, item => item.Owner == CriterionEvidenceOwner.Unknown);
        await repository.SaveAsync(kernel);
        var tick = await repository.LoadAsync();
        var baseline = tick.ExportSnapshot();
        var second = await repository.LoadAsync();
        second.SetGoalSourceBacklogItemId(goal.Id, "unrelated-store-write");
        await repository.SaveAsync(second);
        tick.SetGoalPriority(goal.Id, GoalPriority.Critical, "test", "tick");
        var current = tick.ExportSnapshot();
        var result = Assert.Single(await repository.SaveGoalSnapshotsWithMergeAsync([
            new GoalSnapshotSaveRequest(baseline.Goals.Single(), current.Goals.Single(), current.HumanInputRequests)]));
        Assert.Equal(GoalSnapshotSaveDisposition.Merged, result.Disposition);

        var restoredKernel = await repository.LoadAsync();
        var restored = restoredKernel.GetGoal(goal.Id);
        Assert.Equal(expected, restored.CriterionEvidenceObligations
            .Where(item => item.CriterionVersion == 3 && item.CriterionIndex == 4));
        RecordReviewerVerdicts(restoredKernel, restored);

        Assert.Equal(WorkTaskStatus.Failed, Assert.Single(restored.Tasks).Status);
        Assert.Contains(restored.Timeline, item => item.Message.Contains(
            "criteria attestation rejected: non-waived criteria are not passing: criterion_index=4", StringComparison.Ordinal));
        Assert.DoesNotContain(restored.Timeline, item => item.Message.Contains(
            "Deferred criterion evidence remains pending: obligation=criterion 5 (v3)", StringComparison.Ordinal));
        Assert.Equal(expected, restored.CriterionEvidenceObligations
            .Where(item => item.CriterionVersion == 3 && item.CriterionIndex == 4));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoredRemapOrRetirement_StaleTick_CannotRestoreOldBinding(bool retire)
    {
        using var store = new TempStore();
        var repository = new SqliteOrchestratorStateRepository(store.Path);
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVersionThreeGoal(kernel);
        MapVersionThreeOperator(kernel, goal.Id);
        await repository.SaveAsync(kernel);
        var tick = await repository.LoadAsync();
        var baseline = tick.ExportSnapshot();
        var second = await repository.LoadAsync();
        if (retire)
        {
            var spec = second.GetGoal(goal.Id).RefinedSpec!;
            second.RecordGoalRefinement(goal.Id, spec with
            {
                AcceptanceCriteria = spec.AcceptanceCriteria.Select((criterion, index) =>
                    index == 4 ? "Observe replacement candidate behavior" : criterion).ToArray()
            });
            second.MapCriterionEvidenceOwner(goal.Id, 4, 4, CriterionEvidenceOwner.Operator,
                "operator", requiredScope: OperatorScope, expectedCandidateSha: OtherCandidateSha);
        }
        else
        {
            second.MapCriterionEvidenceOwner(goal.Id, 4, 3, CriterionEvidenceOwner.Acceptance,
                "operator", expectedCandidateSha: OtherCandidateSha);
        }
        var expected = second.GetGoal(goal.Id).CriterionEvidenceObligations;
        var replacement = Assert.Single(expected.Where(item => item.CriterionIndex == 4 &&
            item.CriterionVersion == (retire ? 4 : 3)));
        Assert.Equal(retire ? CriterionEvidenceOwner.Operator : CriterionEvidenceOwner.Acceptance, replacement.Owner);
        Assert.Equal(OtherCandidateSha, replacement.ExpectedCandidateSha);
        if (retire)
            Assert.StartsWith("retired:", Assert.Single(expected.Where(item => item.Id == VersionThreeObligationId)).Detail);
        await repository.SaveAsync(second);

        // Give the stale tick a conflicting owner delta as well as an unrelated goal change.
        tick.MapCriterionEvidenceOwner(goal.Id, 4, 3, CriterionEvidenceOwner.Operator,
            "stale tick", requiredScope: "stale observation", expectedCandidateSha: VersionThreeCandidateSha);
        tick.SetGoalPriority(goal.Id, GoalPriority.Critical, "test", "tick");
        var current = tick.ExportSnapshot();
        var result = Assert.Single(await repository.SaveGoalSnapshotsWithMergeAsync([
            new GoalSnapshotSaveRequest(baseline.Goals.Single(), current.Goals.Single(), current.HumanInputRequests)]));
        Assert.Equal(GoalSnapshotSaveDisposition.Merged, result.Disposition);
        Assert.Contains($"criterion evidence obligation conflict kept stored: {VersionThreeObligationId}",
            result.Message, StringComparison.Ordinal);
        var saved = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(goal.Id));
        Assert.Equal(second.ExportSnapshot().Goals.Single().CriterionEvidenceObligations, saved.CriterionEvidenceObligations);
        var restored = (await repository.LoadAsync()).GetGoal(goal.Id);
        Assert.Equal(expected, restored.CriterionEvidenceObligations);
        Assert.Equal(retire ? 4 : 3, restored.AuthoritativeRefinedSpecVersion!.Version);
        Assert.Equal(GoalPriority.Critical, restored.Priority);
    }

    [Theory]
    [InlineData("not-verifiable")]
    [InlineData("not-met")]
    public async Task SourceReview_AfterRestore_RemainsWorkerOwned(string sourceVerdict)
    {
        using var store = new TempStore();
        var repository = new SqliteOrchestratorStateRepository(store.Path);
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVersionThreeGoal(kernel);
        var mapped = MapVersionThreeOperator(kernel, goal.Id);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        await repository.SaveAsync(kernel);
        var restoredKernel = await repository.LoadAsync();
        var restored = restoredKernel.GetGoal(goal.Id);
        Assert.DoesNotContain(restored.CriterionEvidenceObligations, item => item.CriterionIndex == 1);

        RecordReviewerVerdicts(restoredKernel, restored, sourceVerdict);

        Assert.Equal(WorkTaskStatus.Failed, Assert.Single(restored.Tasks).Status);
        Assert.Contains(restored.Timeline, item => item.Message.Contains(
            $"criteria attestation rejected: non-waived criteria are not passing: criterion_index=1 (criterion 2) verdict={sourceVerdict}",
            StringComparison.Ordinal));
        Assert.DoesNotContain(restored.CriterionEvidenceObligations, item => item.CriterionIndex == 1);
        AssertVersionThreeBinding(mapped, restored.CriterionEvidenceObligations);
        Assert.Contains(restored.Timeline, item => item.Message.Contains(
            $"Deferred criterion evidence remains pending: obligation={mapped.DisplayLabel}; owner=Operator;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TickEvidenceSurvivesConcurrentStoreWrite()
    {
        using var store = new TempStore();
        var repository = new SqliteOrchestratorStateRepository(store.Path);
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateBoundGoal(kernel);
        await repository.SaveAsync(kernel);
        var tick = await repository.LoadAsync();
        var baseline = tick.ExportSnapshot();
        Assert.Equal(CriterionEvidenceState.Pending,
            Assert.Single(tick.GetGoal(goal.Id).CriterionEvidenceObligations).State);

        var second = await repository.LoadAsync();
        second.SetGoalSourceBacklogItemId(goal.Id, "second-kernel-backlog");
        await repository.SaveAsync(second);
        tick.RecordCriterionEvidence(goal.Id, ObligationId, CriterionEvidenceOwner.Acceptance,
            CandidateSha, $"full-acceptance:{CandidateSha}", CriterionEvidenceScopes.FullAcceptanceGate,
            passed: true, detail: "Full acceptance passed on the tick candidate");

        var current = tick.ExportSnapshot();
        var result = Assert.Single(await repository.SaveGoalSnapshotsWithMergeAsync([
            new GoalSnapshotSaveRequest(baseline.Goals.Single(), current.Goals.Single(), current.HumanInputRequests)]));

        Assert.Equal(GoalSnapshotSaveDisposition.Merged, result.Disposition);
        var restored = (await repository.LoadAsync()).GetGoal(goal.Id);
        var obligation = Assert.Single(restored.CriterionEvidenceObligations);
        Assert.Equal(CriterionEvidenceState.Satisfied, obligation.State);
        Assert.Equal($"full-acceptance:{CandidateSha}", obligation.ReceiptId);
        Assert.Equal(CandidateSha, obligation.CandidateSha);
        Assert.Equal("second-kernel-backlog", restored.SourceBacklogItemId);
    }

    [Fact]
    public async Task StoreOnlyObligationChangeSurvivesUnrelatedTickChange()
    {
        using var store = new TempStore();
        var repository = new SqliteOrchestratorStateRepository(store.Path);
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateBoundGoal(kernel, includeSecondCriterion: true);
        await repository.SaveAsync(kernel);
        var tick = await repository.LoadAsync();
        var baseline = tick.ExportSnapshot();
        Assert.Single(tick.GetGoal(goal.Id).CriterionEvidenceObligations);

        var second = await repository.LoadAsync();
        var mapped = second.MapCriterionEvidenceOwner(goal.Id, 1, 1, CriterionEvidenceOwner.Acceptance,
            "operator", expectedCandidateSha: CandidateSha);
        await repository.SaveAsync(second);
        tick.SetGoalPriority(goal.Id, GoalPriority.Critical, "test", "tick");

        var current = tick.ExportSnapshot();
        var result = Assert.Single(await repository.SaveGoalSnapshotsWithMergeAsync([
            new GoalSnapshotSaveRequest(baseline.Goals.Single(), current.Goals.Single(), current.HumanInputRequests)]));

        Assert.Equal(GoalSnapshotSaveDisposition.Merged, result.Disposition);
        var restored = (await repository.LoadAsync()).GetGoal(goal.Id);
        Assert.Equal(GoalPriority.Critical, restored.Priority);
        Assert.Equal(new[] { ObligationId, mapped.Id }, restored.CriterionEvidenceObligations.Select(item => item.Id));
        var obligation = restored.CriterionEvidenceObligations.Single(item => item.Id == mapped.Id);
        Assert.Equal(CriterionEvidenceOwner.Acceptance, obligation.Owner);
        Assert.Equal(CandidateSha, obligation.ExpectedCandidateSha);
        Assert.Equal(CriterionEvidenceState.Pending, obligation.State);
    }

    [Fact]
    public async Task ConflictingObligationChangeKeepsStoredAndNamesTheId()
    {
        using var store = new TempStore();
        var repository = new SqliteOrchestratorStateRepository(store.Path);
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateBoundGoal(kernel);
        await repository.SaveAsync(kernel);
        var tick = await repository.LoadAsync();
        var baseline = tick.ExportSnapshot();

        var second = await repository.LoadAsync();
        second.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
            "operator", expectedCandidateSha: OtherCandidateSha);
        await repository.SaveAsync(second);
        tick.RecordCriterionEvidence(goal.Id, ObligationId, CriterionEvidenceOwner.Acceptance,
            CandidateSha, $"full-acceptance:{CandidateSha}", CriterionEvidenceScopes.FullAcceptanceGate,
            passed: true, detail: "Tick still used the original binding");

        var current = tick.ExportSnapshot();
        var result = Assert.Single(await repository.SaveGoalSnapshotsWithMergeAsync([
            new GoalSnapshotSaveRequest(baseline.Goals.Single(), current.Goals.Single(), current.HumanInputRequests)]));

        Assert.Equal(GoalSnapshotSaveDisposition.Merged, result.Disposition);
        Assert.Contains($"criterion evidence obligation conflict kept stored: {ObligationId}", result.Message,
            StringComparison.Ordinal);
        var obligation = Assert.Single((await repository.LoadAsync()).GetGoal(goal.Id).CriterionEvidenceObligations);
        Assert.Equal(OtherCandidateSha, obligation.ExpectedCandidateSha);
        Assert.Equal(CriterionEvidenceState.Pending, obligation.State);
        Assert.Null(obligation.CandidateSha);
        Assert.Null(obligation.ReceiptId);
    }

    [Fact]
    public void MergeHelperRules()
    {
        var before = Enumerable.Range(0, 7).Select(Obligation).ToArray();
        var tickOnly = before[0] with { Provenance = "tick" };
        var storeOnly = before[1] with { Provenance = "store" };
        var shared = before[2] with { Provenance = "both", PriorReceipts = new List<CriterionEvidenceReceipt>() };
        var sharedCopy = shared with { PriorReceipts = new List<CriterionEvidenceReceipt>() };
        var conflictingStore = before[3] with { Provenance = "store" };
        var conflictingTick = before[3] with { Provenance = "tick" };
        var added = Obligation(7);

        // Deliberately reorder stored entries; baseline-only entries 5 and 6 are removed by both sides.
        var result = GoalSnapshotObligationMerge.Merge(before,
            [storeOnly, before[0], shared, conflictingStore, before[4]],
            [tickOnly, before[1], sharedCopy, conflictingTick, added]);

        Assert.Equal(new[] { storeOnly, tickOnly, shared, conflictingStore, added }, result.Obligations);
        Assert.Equal(new[] { before[3].Id }, result.ConflictIds);
        Assert.DoesNotContain(result.Obligations!, item => item.Id == before[4].Id);

        IReadOnlyList<CriterionEvidenceObligation> unchanged = [before[0]];
        var noChanges = GoalSnapshotObligationMerge.Merge(unchanged, unchanged, [before[0] with { }]);
        Assert.Same(unchanged, noChanges.Obligations);
        Assert.Empty(noChanges.ConflictIds);
        var storeChanged = GoalSnapshotObligationMerge.Merge(unchanged, [tickOnly], unchanged);
        Assert.Equal(new[] { tickOnly }, storeChanged.Obligations);
        Assert.Empty(storeChanged.ConflictIds);

        var bothNull = GoalSnapshotObligationMerge.Merge(null, null, null);
        Assert.Null(bothNull.Obligations);
        Assert.Empty(bothNull.ConflictIds);
        var removed = GoalSnapshotObligationMerge.Merge(unchanged, unchanged, null);
        Assert.Null(removed.Obligations);
        Assert.Empty(removed.ConflictIds);
        var inserted = GoalSnapshotObligationMerge.Merge(null, null, [added]);
        Assert.Equal(new[] { added }, inserted.Obligations);
        Assert.Empty(inserted.ConflictIds);

        var deletedInStore = GoalSnapshotObligationMerge.Merge(unchanged, null, [tickOnly]);
        Assert.Null(deletedInStore.Obligations);
        Assert.Equal(new[] { before[0].Id }, deletedInStore.ConflictIds);
        var deletedInTick = GoalSnapshotObligationMerge.Merge(unchanged, [tickOnly], null);
        Assert.Equal(new[] { tickOnly }, deletedInTick.Obligations);
        Assert.Equal(new[] { before[0].Id }, deletedInTick.ConflictIds);
    }

    private static void RecordReviewerVerdicts(AgentOrchestratorKernel kernel, Goal goal, string sourceVerdict = "met")
    {
        var reviewer = Assert.Single(goal.Tasks);
        var startedAt = DateTimeOffset.UtcNow;
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review", "C:\\fixture", startedAt, BaseCommit: VersionThreeCandidateSha));
        var verdicts = JsonSerializer.Serialize(Enumerable.Range(0, 5).Select(index => new
        {
            criterion_index = index,
            verdict = index == 4 ? "not-verifiable" : index == 1 ? sourceVerdict : "met",
            evidence = index == 4 ? "Operator observation remains pending" : "Source inspected"
        }));
        var output = string.Join(Environment.NewLine,
            "WORKER_RESULT:", "files: none", "commands: inspect", "tests: deferred - OperatorObservationTests",
            "commit: none", "blockers: none", "findings: []", "touched_anchors: []",
            $"criteria_verdicts: {verdicts}", "verdict: pass",
            "model_fit: fixture/model - adequate - deterministic review", "skills: none", "confidence: high",
            "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review", "C:\\fixture", 0, output, string.Empty, DateTimeOffset.UtcNow,
            WorkerResultPresent: true, ReviewedCommit: VersionThreeCandidateSha,
            DispatchStartedAt: startedAt, DispatchAttemptAt: startedAt));

        // Prove these controls reach attestation rather than an incomplete-contract retry.
        Assert.Equal(VerificationTerminalContractStatus.Valid,
            WorkerResultBlockers.AssessStoredSubscriptionVerification(reviewer, reviewer.LastVerification!).Status);
    }

    private static Goal CreateVersionThreeGoal(AgentOrchestratorKernel kernel)
    {
        var reviewer = new TaskSpec(TaskId.New(), "Review persisted owner binding", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Preserve the Applied Operator binding for v3 criterion 5", [reviewer]);
        for (var version = 1; version <= 3; version++)
        {
            var gateCriterion = $"Acceptance verifies version {version}";
            kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                [gateCriterion, "Inspect source behavior", "Inspect error handling", "Inspect persistence", OperatorCriterion],
                VerificationClass.TestVerifiable, [], [])
            {
                AcceptanceGateOwnedAcceptanceCriteria = [gateCriterion]
            });
            if (version < 3)
                kernel.MapCriterionEvidenceOwner(goal.Id, 0, version, CriterionEvidenceOwner.Acceptance,
                    "operator", expectedCandidateSha: VersionThreeCandidateSha);
        }
        Assert.Equal(3, goal.AuthoritativeRefinedSpecVersion!.Version);
        Assert.DoesNotContain(OperatorCriterion, goal.RefinedSpec!.OperatorOwnedAcceptanceCriteria);
        Assert.DoesNotContain(OperatorCriterion, goal.RefinedSpec.AcceptanceGateOwnedAcceptanceCriteria);
        Assert.DoesNotContain(goal.CriterionEvidenceObligations, item => item.CriterionIndex == 4);
        return goal;
    }

    private static CriterionEvidenceObligation MapVersionThreeOperator(AgentOrchestratorKernel kernel, GoalId goalId) =>
        kernel.MapCriterionEvidenceOwner(goalId, 4, 3, CriterionEvidenceOwner.Operator,
            "operator", requiredScope: OperatorScope, expectedCandidateSha: VersionThreeCandidateSha);

    private static void AssertVersionThreeBinding(CriterionEvidenceObligation expected,
        IReadOnlyList<CriterionEvidenceObligation> obligations)
    {
        var actual = Assert.Single(obligations.Where(item => item.CriterionVersion == 3 && item.CriterionIndex == 4));
        Assert.Equal(VersionThreeObligationId, actual.Id);
        Assert.Equal(CriterionEvidenceOwner.Operator, actual.Owner);
        Assert.Equal(CriterionEvidenceState.Pending, actual.State);
        Assert.Equal(VersionThreeCandidateSha, actual.ExpectedCandidateSha);
        Assert.Equal(OperatorScope, actual.RequiredScope);
        Assert.Equal("operator mapping by operator", actual.Provenance);
        Assert.Equal(expected, actual);
    }

    private static Goal CreateBoundGoal(AgentOrchestratorKernel kernel, bool includeSecondCriterion = false)
    {
        var goal = kernel.CreateGoal("Merge criterion evidence");
        const string criterion = "Acceptance verifies the candidate";
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
            includeSecondCriterion ? [criterion, "Second criterion"] : [criterion],
            VerificationClass.TestVerifiable, [], [])
        {
            AcceptanceGateOwnedAcceptanceCriteria = [criterion]
        });
        kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
            "operator", expectedCandidateSha: CandidateSha);
        return goal;
    }

    private static CriterionEvidenceObligation Obligation(int index) => new(
        CriterionEvidenceObligation.BuildId(1, index), index, 1, $"Criterion {index}",
        CriterionEvidenceOwner.Acceptance, CriterionEvidenceState.Pending,
        CriterionEvidenceScopes.FullAcceptanceGate, "baseline", DateTimeOffset.UnixEpoch,
        ExpectedCandidateSha: CandidateSha);

    private sealed class TempStore : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"mcg-obligation-merge-{Guid.NewGuid():N}");

        public TempStore()
        {
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "state.db");
            StateDbMigrations.EnsureUpToDate(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
