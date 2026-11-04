using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: GUID workspaces, isolated databases and no process/environment changes.
public sealed class OperatorProgressCompletionBoundaryTests
{
    [Theory]
    [InlineData(WorkTaskStatus.Running)]
    [InlineData(WorkTaskStatus.Failed)]
    [InlineData(WorkTaskStatus.Cancelled)]
    public async Task CliProgress_OtherStatuses_RequestNoAttestation(WorkTaskStatus status)
    {
        using var fixture = new OperatorProgressCompletionFixture();
        await fixture.Save();
        fixture.Submit(["progress", "1", status.ToString().ToLowerInvariant(), "Operator observed status"]);
        var intent = Assert.Single(await fixture.Store.ListForGoalAsync(fixture.GoalId.Value));
        var payload = JsonSerializer.Deserialize<ProgressOperatorIntentPayload>(intent.PayloadJson, JsonSerializerOptions.Web)!;
        Assert.Null(payload.CompletionVerification);
        Assert.IsType<OperatorIntentOfflineOutcome.Applied>(await fixture.ApplyOffline());
        await fixture.Reload();
        Assert.Equal(status, fixture.Task.Status);
        Assert.Empty(fixture.Task.VerificationHistory);
    }

    [Fact]
    public async Task AgentCliCompletion_OperatorNamedText_DoesNotAttest()
    {
        using var fixture = new OperatorProgressCompletionFixture();
        await fixture.Save();
        fixture.Submit(["progress", "1", "completed", "human operator verified this",
            "--actor-kind", "agent", "--operator-actor", "operator"]);
        var intent = Assert.Single(await fixture.Store.ListForGoalAsync(fixture.GoalId.Value));
        Assert.Equal(OperatorActorKind.Agent, intent.ActorKind);
        Assert.Null(JsonSerializer.Deserialize<ProgressOperatorIntentPayload>(intent.PayloadJson,
            JsonSerializerOptions.Web)!.CompletionVerification);
        Assert.IsType<OperatorIntentOfflineOutcome.Applied>(await fixture.ApplyOffline());
        await fixture.Reload();
        Assert.Equal(WorkTaskStatus.Completed, fixture.Task.Status);
        Assert.Empty(fixture.Task.VerificationHistory);
        Assert.Equal(VerificationGateStatus.MissingVerification,
            Assert.Single(AgentOrchestratorKernel.BuildVerificationWorklist(fixture.Goal).Items).GateStatus);
    }

    [Theory]
    [InlineData(OperatorActorKind.Agent, "cli", "local-process")]
    [InlineData(OperatorActorKind.Human, "import", "local-process")]
    [InlineData(OperatorActorKind.Human, "cli", "unverified")]
    public async Task AttestationRequest_OutsideHumanContract_RejectsWithoutMutation(
        OperatorActorKind kind, string channel, string assurance)
    {
        using var fixture = new OperatorProgressCompletionFixture();
        var before = JsonSerializer.Serialize(fixture.Kernel.ExportGoalSnapshot(fixture.GoalId));
        var intent = await fixture.Enqueue(fixture.Completion(), kind, channel, assurance);
        Assert.False(fixture.Apply().MutatedGoalState);
        Assert.Equal(OperatorIntentStatus.Rejected, (await fixture.Store.GetAsync(intent.Id))!.Status);
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Kernel.ExportGoalSnapshot(fixture.GoalId)));
    }

    [Theory]
    [InlineData("note")]
    [InlineData("passed")]
    [InlineData("status")]
    [InlineData("empty")]
    public async Task AttestationRequest_InvalidPair_RejectsBeforeCompletion(string invalid)
    {
        using var fixture = new OperatorProgressCompletionFixture();
        var payload = fixture.Completion();
        payload = invalid switch
        {
            "note" => payload with { CompletionVerification = new(true, "Different note", fixture.Root) },
            "passed" => payload with { CompletionVerification = new(false, payload.Message, fixture.Root) },
            "status" => payload with { Status = WorkTaskStatus.Running },
            _ => payload with { Message = "  ", CompletionVerification = new(true, "  ", fixture.Root) }
        };
        var intent = await fixture.Enqueue(payload);
        Assert.False(fixture.Apply().MutatedGoalState);
        Assert.Equal(OperatorIntentStatus.Rejected, (await fixture.Store.GetAsync(intent.Id))!.Status);
        Assert.Equal(WorkTaskStatus.Assigned, fixture.Task.Status);
        Assert.Empty(fixture.Task.VerificationHistory);
        Assert.DoesNotContain(fixture.Goal.Timeline, entry => entry.Kind == ProgressKind.TaskCompleted);
    }

    [Fact]
    public async Task WorkerAndImportedCompletion_WithoutRequest_RetainVerificationHold()
    {
        using var fixture = new OperatorProgressCompletionFixture();
        await fixture.Enqueue(new(WorkTaskStatus.Completed, "Imported operator completion"));
        Assert.True(fixture.Apply().MutatedGoalState);
        Assert.Empty(fixture.Task.VerificationHistory);
        Assert.Single(AgentOrchestratorKernel.BuildVerificationWorklist(fixture.Goal).Items);
        var worker = fixture.Kernel.CreateGoal("Worker", [new(TaskId.New(), "Worker work", AgentRole.Developer)]);
        fixture.Kernel.ReportWorkerTaskProgress(worker.Id, worker.Tasks.Single().Id,
            WorkTaskStatus.Completed, "Operator approved");
        Assert.Empty(worker.Tasks.Single().VerificationHistory);
        Assert.Single(AgentOrchestratorKernel.BuildVerificationWorklist(worker).Items);
    }

    [Fact]
    public async Task HumanCompletion_IndependentObligations_StayUnsatisfied()
    {
        using var fixture = new OperatorProgressCompletionFixture();
        fixture.Kernel.SetGoalRefinedSpec(fixture.GoalId, new RefinedSpec("Operator progress",
            ["Acceptance must verify the result"], VerificationClass.TestVerifiable, [], []));
        var obligation = fixture.Kernel.MapCriterionEvidenceOwner(fixture.GoalId, 0, 1,
            CriterionEvidenceOwner.Acceptance, "operator", "acceptance:full-gate",
            expectedCandidateSha: new string('a', 40));
        var obligationBefore = JsonSerializer.Serialize(obligation);
        var decisions = CollaborationItemStore.ForDirectory(fixture.Workspace.OrchestratorDirectory);
        const string manifest = "config/acceptance-manifest.json";
        Directory.CreateDirectory(Path.Combine(fixture.Root, "config"));
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, manifest),
            "{\"engine\":{\"maxConcurrentShards\":3}}");
        OwnerProtectedConfigurationPolicy.OwnerProtectedDecision ProtectedGate() =>
            OwnerProtectedConfigurationPolicy.EvaluateOwnerProtectedConfigurationForTests(
                fixture.Root, fixture.GoalId, [manifest], null,
                (_, args) => args[0] == "diff" ? manifest + "\n" : "{\"engine\":{\"maxConcurrentShards\":2}}",
                decisions, () => new([], new HashSet<string>(), new HashSet<string>()), new string('a', 40));
        var before = ProtectedGate();
        Assert.NotNull(before.Failure);
        await fixture.Enqueue(fixture.Completion());
        Assert.True(fixture.Apply().MutatedGoalState);
        Assert.Equal(VerificationEvidenceSource.Manual, Assert.Single(fixture.Task.VerificationHistory).EvidenceSource);
        Assert.Equal(obligationBefore, JsonSerializer.Serialize(Assert.Single(fixture.Goal.CriterionEvidenceObligations)));
        Assert.Equal(before, ProtectedGate());
        Assert.False(ProtectedGate().OwnerApprovalSatisfied);
        Assert.False(OwnerProtectedConfigurationPolicy.HasOwnerPolicyApprovalForTests(
            decisions, fixture.GoalId, new string('a', 40)));
        Assert.NotEqual(GoalStatus.Verified, fixture.Goal.Status);
        Assert.NotEqual(GoalStatus.Completed, fixture.Goal.Status);
        Assert.Null(fixture.Task.LastExecution);
    }

    [Fact]
    public void ManualFactory_DefaultCall_KeepsExistingTrimming()
    {
        var receipt = ManualVerificationRecorder.Create(true, "  Existing note  ", "fixture", DateTimeOffset.UtcNow);
        Assert.Equal("Existing note", receipt.AuthoritativeStandardOutput);
    }
}
