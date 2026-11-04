using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json.Nodes;
using System.Text.Json;

// Parallel-safe: each case owns its Git repository, gate files and SQLite collaboration store.
public sealed class LandingBuildReportAttentionTests
{
    [Theory]
    [InlineData("conductor")]
    [InlineData("worker")]
    [InlineData("fixture-equality-only")]
    [InlineData("wrong-gate-candidate")]
    [InlineData("wrong-gate-brief")]
    public void LandingCaller_RequiresActualCandidateProofAndBoundGate(string change)
    {
        using var repo = new ConductorOnlyCandidateRangeTests.Repository();
        var fixturePaths = DeveloperBuildReportEvidenceClosureTests.Records.Classes.Take(3)
            .Select(name => $"tests/Mcg.AgentOrchestrator.Infrastructure.Tests/{name}.cs").ToArray();
        foreach (var path in fixturePaths) repo.Write(path, "// unchanged implementation fixture\n");
        var implementation = ConductorOnlyCandidateRangeTests.Commit(repo.Root, "worker", "worker@example.invalid");
        repo.Merge(change == "worker" ? "worker" : "mcg-orchestrator",
            change == "worker" ? "worker@example.invalid" : "mcg-orchestrator@localhost");
        if (change == "fixture-equality-only")
        {
            repo.Write("unrelated-implementation.txt", "unproved work\n");
            ConductorOnlyCandidateRangeTests.Commit(repo.Root);
        }
        Assert.Equal("", repo.Git(["diff", "--name-only", implementation, repo.Head, "--", .. fixturePaths]));
        var records = BoundRecords(repo, implementation);
        var workspace = OrchestratorWorkspace.ForDirectory(repo.Root);
        var attempt = ProspectiveEvidenceGateFixture.WriteAttempt(workspace.OrchestratorDirectory, records.Goal,
            change == "wrong-gate-candidate" ? repo.Reviewed : repo.Head, Rows());
        StampBrief(attempt.MetadataPath, change == "wrong-gate-brief" ? 2 : 1);

        var result = LandingExecutor.EvaluatePendingHumanInput(records.Kernel, records.Goal, workspace, repo.Head);

        if (change == "conductor")
        {
            Assert.True(records.Wait.IsCompleted);
            Assert.Null(result.HoldDescription);
            Assert.Contains(attempt.Id, records.Wait.Answer);
            Assert.Empty(records.Kernel.GetPendingHumanInput(records.Goal.Id));
        }
        else
        {
            Assert.False(records.Wait.IsCompleted);
            Assert.Contains(change is "wrong-gate-candidate" or "wrong-gate-brief" ? "class-not-in-passed-gate:" : "build-candidate-provenance-missing:", result.HoldDescription);
            Assert.Same(records.Wait, Assert.Single(records.Kernel.GetPendingHumanInput(records.Goal.Id)));
        }
    }

    [Fact]
    public void ResolvedWait_RetiresOnlyExactAttentionAndPreservesCurrentTrainHold()
    {
        using var repo = new ConductorOnlyCandidateRangeTests.Repository();
        repo.Merge();
        var records = BoundRecords(repo);
        var workspace = OrchestratorWorkspace.ForDirectory(repo.Root);
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var reason = $"acceptance passed at {repo.Head[..8]}; 1 pending human wait(s) (ProspectiveAcceptanceEvidence {records.Wait.Id.Value[..8]})";
        OperatorInbox.RecordLandingEscalation(workspace, records.Goal, reason, "conductor:Verified", collaborationStore: store);
        var waitItem = Assert.Single(store.ListAsync(records.Goal.Id.Value).GetAwaiter().GetResult());
        var unrelated = store.RaiseAsync(CollaborationItemType.Decision, records.Goal.Id.Value,
            "Unrelated approval", reason, "independent-approval").GetAwaiter().GetResult();
        const string trainReason = "Landing held at mutation boundary: merge train train:other is in flight and goal is not a member";
        var train = store.RaiseAsync(CollaborationItemType.Decision, records.Goal.Id.Value,
            "Train in flight", trainReason, "independent-train").GetAwaiter().GetResult();
        var before = store.ListAsync(records.Goal.Id.Value).GetAwaiter().GetResult();
        var hold = new GoalLandingBlocker("landing/mutation-boundary", trainReason, "train:other", DateTimeOffset.UnixEpoch, "conductor", "next-tick");
        records.Goal.ObserveLandingBlocker(hold);
        var attempt = ProspectiveEvidenceGateFixture.WriteAttempt(workspace.OrchestratorDirectory, records.Goal, repo.Head, Rows());
        StampBrief(attempt.MetadataPath, 1);

        var result = LandingExecutor.EvaluatePendingHumanInput(records.Kernel, records.Goal, workspace, repo.Head);

        Assert.True(records.Wait.IsCompleted);
        Assert.Null(result.HoldDescription);
        Assert.Equal(0, records.Kernel.BuildHumanInputWorklist(records.Goal.Id).OpenCount);
        Assert.Equal(hold, records.Goal.LandingBlocker);
        var changed = new HashSet<GoalId>();
        ConductorLandingBlockerRecorder.Observe(records.Kernel, records.Goal,
            new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, trainReason, "train:other"), DateTimeOffset.UnixEpoch, changed);
        Assert.Contains(trainReason, records.Goal.LandingBlocker!.Detail);
        Assert.DoesNotContain(records.Wait.Id.Value[..8], records.Goal.LandingBlocker.Detail);
        Assert.Contains(records.Goal.Id, changed);
        var rows = store.ListAsync(records.Goal.Id.Value).GetAwaiter().GetResult();
        Assert.Equal(CollaborationItemStatus.Resolved, rows.Single(item => item.Id == waitItem.Id).Status);
        Assert.Equal(JsonSerializer.Serialize(before.Single(item => item.Id == unrelated.Id)), JsonSerializer.Serialize(rows.Single(item => item.Id == unrelated.Id)));
        Assert.Equal(JsonSerializer.Serialize(before.Single(item => item.Id == train.Id)), JsonSerializer.Serialize(rows.Single(item => item.Id == train.Id)));
        OperatorInbox.ResolveAnsweredBuildReportEscalations(workspace, records.Goal, records.Kernel.HumanInputRequests.ToArray());
        Assert.Equal(JsonSerializer.Serialize(rows), JsonSerializer.Serialize(store.ListAsync(records.Goal.Id.Value).GetAwaiter().GetResult()));
    }

    [Theory]
    [InlineData("wrong-correlation")]
    [InlineData("unknown-request")]
    [InlineData("ambiguous-prefix")]
    [InlineData("mixed-waits")]
    [InlineData("extra-text")]
    public void Attention_InsufficientCorrelationLeavesOccurrenceUntouched(string change)
    {
        using var repo = new ConductorOnlyCandidateRangeTests.Repository();
        var records = BoundRecords(repo);
        records.Close();
        var workspace = OrchestratorWorkspace.ForDirectory(repo.Root);
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var prefix = records.Wait.Id.Value[..8];
        var reason = $"acceptance passed at {repo.Head[..8]}; 1 pending human wait(s) (ProspectiveAcceptanceEvidence {prefix})";
        var requests = records.Kernel.HumanInputRequests.ToList();
        if (change == "unknown-request") reason = reason.Replace(prefix, "00000000", StringComparison.Ordinal);
        if (change == "extra-text") reason += "; independent approval required";
        if (change == "mixed-waits") reason = $"acceptance passed at {repo.Head[..8]}; 2 pending human wait(s) (ProspectiveAcceptanceEvidence {prefix}, 00000000)";
        if (change == "ambiguous-prefix") requests.Add(new HumanInputRequest(new(prefix + new string('0', 24)), records.Goal.Id, null,
            "Distinct same-prefix wait", DateTimeOffset.UnixEpoch));
        OperatorInbox.RecordLandingEscalation(workspace, records.Goal, reason, "conductor:Verified", collaborationStore: store);
        if (change == "wrong-correlation")
        {
            // A byte-identical body with a different persisted key is not this occurrence.
            store.TryResolveAsync(Assert.Single(store.ListAsync(records.Goal.Id.Value).GetAwaiter().GetResult()).CorrelationKey!, "already handled").GetAwaiter().GetResult();
            store.RaiseAsync(CollaborationItemType.Decision, records.Goal.Id.Value, "Unrelated", reason, "different-key").GetAwaiter().GetResult();
        }
        var before = store.ListAsync(records.Goal.Id.Value).GetAwaiter().GetResult();
        OperatorInbox.ResolveAnsweredBuildReportEscalations(workspace, records.Goal, requests, store);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(store.ListAsync(records.Goal.Id.Value).GetAwaiter().GetResult()));
    }

    [Theory]
    [InlineData("blockers")]
    [InlineData("verification")]
    [InlineData("mixed-waits")]
    [InlineData("mixed-kinds")]
    [InlineData("combined")]
    public void CompoundReason_AnsweredPortionRetiresAndRemainingDemandSurvives(string change)
    {
        using var repo = new ConductorOnlyCandidateRangeTests.Repository();
        var records = BoundRecords(repo);
        records.Close();
        Assert.True(records.Wait.IsCompleted);
        var prefix = records.Wait.Id.Value[..8];
        var requests = records.Kernel.HumanInputRequests.ToList();
        var workspace = OrchestratorWorkspace.ForDirectory(repo.Root);
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var acceptance = $"acceptance passed at {repo.Head[..8]}";
        var verification = change is "verification" or "combined" ? "; landing held by 1 open verification(s)" : "";
        var blockers = change is "blockers" or "combined" ? "; 1 blocker(s) (AcceptanceFailed)" : "";
        var waitReferences = $"ProspectiveAcceptanceEvidence {prefix}";
        var remainingWait = "";
        HumanInputRequest? unresolved = null;
        if (change is "mixed-waits" or "mixed-kinds" or "combined")
        {
            var kind = change == "mixed-kinds" ? HumanWaitKind.OperatorApproval : HumanWaitKind.ProspectiveAcceptanceEvidence;
            unresolved = new HumanInputRequest(new("11111111" + new string('1', 24)), records.Goal.Id, null,
                "Independent unanswered request", DateTimeOffset.UnixEpoch, kind);
            requests.Add(unresolved);
            remainingWait = $"; 1 pending human wait(s) ({kind} 11111111)";
            waitReferences = kind == HumanWaitKind.OperatorApproval
                ? $"OperatorApproval 11111111; {waitReferences}"
                : $"{waitReferences}, 11111111";
        }
        var count = unresolved is null ? 1 : 2;
        var reason = $"{acceptance}{verification}; {count} pending human wait(s) ({waitReferences}){blockers}";
        var expected = acceptance + verification + remainingWait + blockers;
        OperatorInbox.RecordLandingEscalation(workspace, records.Goal, reason, "conductor:Verified", collaborationStore: store);
        var original = Assert.Single(store.ListAsync(records.Goal.Id.Value).GetAwaiter().GetResult());
        var unrelated = store.RaiseAsync(CollaborationItemType.Decision, records.Goal.Id.Value,
            "Independent approval", reason, "independent-approval").GetAwaiter().GetResult();
        var hold = new GoalLandingBlocker("landing/mutation-boundary", "Train still in flight", "train:other",
            DateTimeOffset.UnixEpoch, "conductor", "next-tick");
        records.Goal.ObserveLandingBlocker(hold);

        OperatorInbox.ResolveAnsweredBuildReportEscalations(workspace, records.Goal, requests, store);

        var rows = store.ListAsync(records.Goal.Id.Value).GetAwaiter().GetResult();
        Assert.Equal(CollaborationItemStatus.Resolved, rows.Single(row => row.Id == original.Id).Status);
        var replacement = Assert.Single(rows.Where(row => row.Id != unrelated.Id && !CollaborationItemLifecycle.IsTerminal(row.Status)));
        Assert.Equal(expected, replacement.Body);
        Assert.DoesNotContain(prefix, replacement.Body);
        Assert.NotEqual(original.CorrelationKey, replacement.CorrelationKey);
        Assert.Equal(JsonSerializer.Serialize(unrelated), JsonSerializer.Serialize(rows.Single(row => row.Id == unrelated.Id)));
        Assert.Equal(hold, records.Goal.LandingBlocker);
        if (unresolved is not null) Assert.False(unresolved.IsCompleted);
        var persisted = ReadEscalations(workspace);
        Assert.Equal(expected, Assert.Single(persisted.Where(IsOpen)).GetProperty("reason").GetString());
        Assert.Equal(reason, Assert.Single(persisted.Where(item => !IsOpen(item))).GetProperty("reason").GetString());
        var persistedBefore = File.ReadAllText(EscalationPath(workspace));
        OperatorInbox.ResolveAnsweredBuildReportEscalations(workspace, records.Goal, requests, store);
        Assert.Equal(JsonSerializer.Serialize(rows), JsonSerializer.Serialize(store.ListAsync(records.Goal.Id.Value).GetAwaiter().GetResult()));
        Assert.Equal(persistedBefore, File.ReadAllText(EscalationPath(workspace)));
    }

    [Fact]
    public void RecurringReason_ResolvesActiveOccurrenceAndPreservesTerminalHistory()
    {
        using var repo = new ConductorOnlyCandidateRangeTests.Repository();
        var records = BoundRecords(repo);
        var workspace = OrchestratorWorkspace.ForDirectory(repo.Root);
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var reason = $"acceptance passed at {repo.Head[..8]}; 1 pending human wait(s) (ProspectiveAcceptanceEvidence {records.Wait.Id.Value[..8]})";
        OperatorInbox.RecordLandingEscalation(workspace, records.Goal, reason, "conductor:Verified", collaborationStore: store);
        var first = Assert.Single(store.ListAsync(records.Goal.Id.Value).GetAwaiter().GetResult());
        Assert.True(store.TryResolveAsync(first.CorrelationKey!, "earlier occurrence handled").GetAwaiter().GetResult());
        var history = Assert.Single(store.ListAsync(records.Goal.Id.Value).GetAwaiter().GetResult());
        OperatorInbox.RecordLandingEscalation(workspace, records.Goal, reason, "conductor:Verified", collaborationStore: store);
        var before = store.ListAsync(records.Goal.Id.Value).GetAwaiter().GetResult();
        Assert.Equal(2, before.Count);
        var active = Assert.Single(before.Where(row => !CollaborationItemLifecycle.IsTerminal(row.Status)));
        Assert.NotEqual(first.Id, active.Id);
        Assert.Equal(first.CorrelationKey, active.CorrelationKey);
        Assert.Single(ReadEscalations(workspace).Where(IsOpen));
        records.Close();
        Assert.True(records.Wait.IsCompleted);

        OperatorInbox.ResolveAnsweredBuildReportEscalations(workspace, records.Goal, records.Kernel.HumanInputRequests.ToArray(), store);

        var after = store.ListAsync(records.Goal.Id.Value).GetAwaiter().GetResult();
        Assert.Equal(2, after.Count);
        Assert.Equal(JsonSerializer.Serialize(history), JsonSerializer.Serialize(after.Single(row => row.Id == history.Id)));
        Assert.Equal(CollaborationItemStatus.Resolved, after.Single(row => row.Id == active.Id).Status);
        Assert.Empty(ReadEscalations(workspace).Where(IsOpen));
        Assert.Equal("developer-build-report", Assert.Single(ReadEscalations(workspace)).GetProperty("resolvedBy").GetString());
        OperatorInbox.ResolveAnsweredBuildReportEscalations(workspace, records.Goal, records.Kernel.HumanInputRequests.ToArray(), store);
        Assert.Equal(JsonSerializer.Serialize(after), JsonSerializer.Serialize(store.ListAsync(records.Goal.Id.Value).GetAwaiter().GetResult()));
    }

    private static string EscalationPath(OrchestratorWorkspace workspace) =>
        Path.Combine(workspace.OrchestratorDirectory, "landing-escalations.json");

    private static bool IsOpen(JsonElement item) => item.GetProperty("resolvedAtUtc").ValueKind == JsonValueKind.Null;

    private static JsonElement[] ReadEscalations(OrchestratorWorkspace workspace)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(EscalationPath(workspace)));
        return document.RootElement.GetProperty("items").EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private static IEnumerable<(string Name, string Outcome)> Rows() => DeveloperBuildReportEvidenceClosureTests.Records.Classes
        .Select(name => ($"{name}.RoundTrip", "Passed"));

    private static DeveloperBuildReportEvidenceClosureTests.Records BoundRecords(ConductorOnlyCandidateRangeTests.Repository repo, string? implementation = null)
    {
        implementation ??= repo.Reviewed;
        var tree = repo.Git("rev-parse", implementation + "^{tree}");
        var records = new DeveloperBuildReportEvidenceClosureTests.Records(implementation: implementation,
            candidate: repo.Head, tree: tree, root: repo.Root);
        var snapshot = records.Kernel.ExportGoalSnapshot(records.Goal.Id);
        var task = records.Developer;
        var dispatch = task.LastDispatch!;
        var id = BackgroundDispatchRunner.BuildDispatchId(records.Goal.Id, task.Id, dispatch with { ProviderSessionId = null });
        records.Kernel.ReplaceGoalWithSnapshot(snapshot with
        {
            Tasks = snapshot.Tasks.Select(item => item.Id == task.Id.Value
                ? item with { WorkerBuildInvocations = [records.Invocation with { DispatchId = id }] } : item).ToArray()
        });
        records.Proof = records.Proof with { DispatchId = id };
        return records;
    }

    private static void StampBrief(string path, int version)
    {
        var metadata = JsonNode.Parse(File.ReadAllText(path))!;
        metadata["briefVersion"] = version;
        File.WriteAllText(path, metadata.ToJsonString());
    }
}
