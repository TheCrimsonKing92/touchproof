using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceCohortWorkflowTestsAttributionProgress : AcceptanceCohortWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RedCohort_ReportsOrderedAttributionAndProgressDuringEachPartition(bool grouped)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First attribution member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second attribution member", repo);
            _ = CreateWorktreeCandidate(repo, firstGoal.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs", "first");
            _ = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var verifier = new GateProgressReportingVerifier(workspace.ConductEventsLogPath,
            [
                FailedVerification(repo, "combined-red-progress.trx", "combined"),
                FailedVerification(repo, "first-partition-red-progress.trx", "first partition"),
                new AcceptanceVerificationResult(true, false, 0, null,
                    Checks: [new AcceptanceCheckResult("second partition", true, 0, null)],
                    TestResultPaths: [WritePassingTrx(repo, "second-partition-green-progress.trx")])
            ]);
            var driver = new ConductorDriver(kernel, workspace, verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                cleanupHooks: CreateIsolatedCleanupContext(workspace.ExecutionDirectory).Hooks);
            var selection = ProjectSelection(driver, firstGoal, secondGoal);
            ConductorGroupedGateAttempt? attempt = null;
            AcceptanceCohortReceipt receipt;
            if (grouped)
            {
                var bindings = selection.BindMembers(id => kernel.GetGoal(id).AuthoritativeBrief.Version);
                AcceptanceCohortIdentity groupedIdentity;
                using (var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(repo,
                           selection.Members[0].MainRevision, bindings, TrunkBranchName.Default,
                           CreateIsolatedCleanupContext(workspace.ExecutionDirectory).Hooks))
                {
                    var manifest = ((IGoalAcceptanceVerifier)verifier).ComputeEffectivePlanIdentity(integration.Path,
                        bindings.SelectMany(member => member.LandingPaths).ToArray());
                    groupedIdentity = AcceptanceCohortIdentity.Create(bindings, selection.Members[0].MainRevision,
                        integration.TreeRevision, manifest);
                }
                var root = Path.Combine(workspace.OrchestratorDirectory, "attribution-child");
                attempt = new("attribution-child", "cohort", selection.Members.Select(ConductorGroupedGateMember.From).ToArray(),
                    groupedIdentity.ObservedMainRevision, groupedIdentity.CombinedTreeRevision, groupedIdentity.ManifestIdentity,
                    groupedIdentity.Value, DateTimeOffset.UnixEpoch, 0, 91001,
                    Path.Combine(root, "attempt.json"), Path.Combine(root, "result.json"), Path.Combine(root, "exit"),
                    Path.Combine(root, "out.log"), Path.Combine(root, "err.log"), repo, ConductorAutonomyPolicy.Permissive.ToJson());
                Assert.NotNull(driver.RunGroupedGateAttemptBody(attempt));
                receipt = Assert.IsType<AcceptanceCohortReceipt>(new CohortAcceptanceStore(
                    Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db")).TryReadReceipt(groupedIdentity.Value));
            }
            else
                receipt = Assert.IsType<AcceptanceCohortReceipt>(driver.RunAcceptanceCohort(
                    selection, [firstGoal, secondGoal], ConductorAutonomyPolicy.Permissive).Receipt);

            Assert.Equal(3, verifier.EventsAfterProgress.Count);
            var identity = receipt.Identity;
            var cohortTag = $"cohort={identity.Value[..18]}";
            var firstId = firstGoal.Id.Value[..8];
            var secondId = secondGoal.Id.Value[..8];
            AssertPartitionProgress(verifier.EventsAfterProgress[1], firstId, cohortTag);
            AssertPartitionProgress(verifier.EventsAfterProgress[2], secondId, cohortTag);

            var events = GateProgressReportingVerifier.ReadEvents(workspace.ConductEventsLogPath);
            var start = Assert.Single(events.Where(record => record.EventKind == "cohort-attribution"));
            Assert.Null(start.GoalId);
            Assert.Equal($"ATTRIBUTION_START {cohortTag} members={firstId},{secondId} scope=attribution", start.Detail);
            var startIndex = Array.IndexOf(events, start);
            Assert.True(startIndex > 0);
            Assert.DoesNotContain(events[..startIndex], record => record.Detail.Contains("scope=attribution", StringComparison.Ordinal));
            Assert.True(Array.FindIndex(events, record => record.Detail.Contains("scope=attribution", StringComparison.Ordinal) &&
                record.EventKind == "gate-progress") > startIndex);

            var comparisonPath = Path.Combine(repo, "combined-progress-comparison.jsonl");
            var expectedProgress = verifier.Progresses[0] with
            {
                TreeRevision = identity.CombinedTreeRevision,
                EvidenceKind = "candidate"
            };
            ConductorDriver.AppendCohortGateProgressEvents(
                new ConductEventLogWriter(comparisonPath), identity, identity.Members, expectedProgress, attempt?.AttemptId);
            var expectedCombined = GateProgressReportingVerifier.ReadEvents(comparisonPath)
                .Select(record => (record.EventKind, record.GoalId, record.Detail));
            var actualCombined = events[..startIndex]
                .Where(record => record.EventKind == "gate-progress")
                .Select(record => (record.EventKind, record.GoalId, record.Detail));
            Assert.Equal(expectedCombined, actualCombined);
            AssertAuxiliaryProjection(verifier, events, firstGoal.Id, secondGoal.Id, attempt);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    private static void AssertAuxiliaryProjection(GateProgressReportingVerifier verifier,
        ConductEventRecord[] records, GoalId first, GoalId second, ConductorGroupedGateAttempt? attempt)
    {
        var parent = verifier.Progresses[0].ExecutionId;
        Assert.Equal(3, verifier.Progresses.Select(progress => progress.ExecutionId).Distinct().Count());
        var partitions = records.Where(record => record.EventKind == "gate-progress" &&
            record.Detail.Contains("scope=attribution", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(partitions);
        Assert.All(partitions, record =>
        {
            Assert.Contains(" parent_execution=" + parent, record.Detail);
            Assert.Contains("members=" + record.GoalId + " scope=attribution", record.Detail);
            if (attempt is not null) Assert.Contains(" outer_attempt=" + attempt.AttemptId, record.Detail);
        });
        OwnerConductEvent[] Convert(IEnumerable<ConductEventRecord> source) => source
            .Select(record => new OwnerConductEvent(record.Timestamp, record.EventKind, record.GoalId, record.Detail)).ToArray();
        var during = Convert(verifier.EventsAfterProgress[1]);
        var running = OwnerConsoleGateProgress.Build(during.Max(item => item.Timestamp), during, [], []);
        var execution = Assert.Single(running.Executions!);
        Assert.Equal(parent, execution.ExecutionId);
        Assert.True(execution.Live);
        Assert.Empty(running.OtherLiveGates!);
        Assert.Equal(parent, running.ForMember(first.Value)!.ExecutionId);
        Assert.Equal(parent, running.ForMember(second.Value)!.ExecutionId);
        var auxiliary = Assert.Single(execution.Lanes, row => row.IsAttribution);
        Assert.Equal(first.Value, auxiliary.ReproductionMemberId);
        Assert.Equal(OwnerConsoleGateLaneState.Running, auxiliary.State);
        Assert.False(auxiliary.IsScheduled);
        Assert.Equal(0, OwnerConsoleGateCount.Of(execution).Total);

        var events = Convert(records);
        var at = events.Max(item => item.Timestamp);
        var finished = OwnerConsoleGateProgress.Build(at, events, [], []);
        var rows = Assert.Single(finished.Executions!).Lanes.Where(row => row.IsAttribution).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row => Assert.Equal(OwnerConsoleGateLaneState.Done, row.State));
        Assert.Equal("failed", Assert.Single(rows, row => row.ReproductionMemberId == first.Value).Outcome);
        Assert.Equal("passed", Assert.Single(rows, row => row.ReproductionMemberId == second.Value).Outcome);

        if (attempt is null) return;
        ConductorGroupedGateAttemptCoordinator.Save(attempt);
        File.WriteAllText(attempt.ResultPath, JsonSerializer.Serialize(new
        {
            attempt.IdentityValue, Status = "completed", Verdict = "failed"
        }));
        var completions = new List<string>();
        var coordinator = new ConductorGroupedGateAttemptCoordinator(Path.GetDirectoryName(attempt.MetadataPath)!,
            isProcessAlive: _ => false, utcNow: () => at.AddSeconds(1), eventSink: completions.Add);
        coordinator.TryReconcileDead(attempt, "child-result-published");
        var completion = Assert.Single(completions);
        Assert.StartsWith("ACCEPTANCE_COHORT_CHILD_COMPLETED ", completion);
        var closed = OwnerConsoleGateProgress.Build(at.AddSeconds(2), events.Append(
            new OwnerConductEvent(at.AddSeconds(1), "acceptance-cohort", null, completion)), [], []);
        Assert.False(closed.Live);
        Assert.Empty(closed.OtherLiveGates!);
        var historical = Assert.Single(closed.Executions!);
        Assert.False(historical.Live);
        Assert.Equal(parent, historical.ExecutionId);
        Assert.Equal(attempt.AttemptId, historical.OuterAttemptId);
        Assert.Equal(2, historical.Lanes.Count(row => row.IsAttribution));
    }

    private static void AssertPartitionProgress(
        IReadOnlyList<ConductEventRecord> events,
        string goalId,
        string cohortTag) =>
        Assert.Contains(events, record => record.EventKind == "gate-progress" &&
            record.GoalId == goalId &&
            record.Detail.Contains(cohortTag, StringComparison.Ordinal) &&
            record.Detail.Contains($"member={goalId}", StringComparison.Ordinal) &&
            record.Detail.Contains("scope=attribution", StringComparison.Ordinal));
}

internal sealed class GateProgressReportingVerifier(
    string logPath,
    IReadOnlyList<AcceptanceVerificationResult> results) : IGoalAcceptanceVerifier
{
    internal List<ConductEventRecord[]> EventsAfterProgress { get; } = [];
    internal List<AcceptanceGateProgress> Progresses { get; } = [];

    public Task<AcceptanceVerificationResult> RunOwnedAsync(
        string worktreePath,
        GoalId? goalId,
        IReadOnlyList<string>? changedFiles,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        IAcceptanceAttemptExecutionOwner executionOwner)
    {
        var index = EventsAfterProgress.Count;
        Assert.True(index < results.Count, "The gate ran more attempts than the fixture supplies.");
        var now = DateTimeOffset.UtcNow;
        var progress = new AcceptanceGateProgress(
            GoalId: goalId?.Value,
            Phase: "controlled-progress",
            CurrentTarget: "gate-progress-fixture",
            SlotIndex: stableSlotIndex,
            ProcessId: Environment.ProcessId,
            ChildProcessId: null,
            StartedAt: now,
            LastObservedAt: now,
            LastProgressAt: now,
            Elapsed: TimeSpan.Zero,
            OutputBytes: 0,
            HeartbeatPath: "fixture-heartbeat.json");
        var context = (IAcceptanceRunExecutionContext)executionOwner;
        Assert.False(string.IsNullOrWhiteSpace(context.RunId));
        Progresses.Add(progress with { ExecutionId = context.RunId });
        context.ReportProgress(progress);
        EventsAfterProgress.Add(ReadEvents(logPath));
        return Task.FromResult(results[index]);
    }

    public Task<FocusedEvidenceRunResult> RunFocusedEvidenceOwnedAsync(
        string worktreePath,
        GoalId? goalId,
        string request,
        IAcceptanceFocusedVerificationOwner executionOwner,
        int? stableSlotIndex = null,
        DotnetBuildEnvironmentLease? stableSlotLease = null,
        bool runBaselineArm = false) =>
        throw new NotSupportedException("Focused evidence is not used by the progress fixture.");

    internal static ConductEventRecord[] ReadEvents(string path) =>
        (File.Exists(path) ? File.ReadAllLines(path) : [])
        .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
            line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
        .ToArray();
}
