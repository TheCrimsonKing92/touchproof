using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: payloads, sparse projections and policy inputs are invocation-local.
public sealed class ProviderHoldSeedEvidenceReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectAsync_SkipsUnrelatedGraphsAndKeepsLegacyProviderEvidence(bool compressed)
    {
        const string id = "aaaaaaaa000000000000000000000000";
        // Deliberately invalid types for unrelated graphs: full snapshot reconstruction would fail.
        var json = """
            {"Id":"aaaaaaaa000000000000000000000000","Status":"Completed",
             "RefinedSpec":42,"DependsOn":42,"Tasks":[{
               "Id":"task","RequiredRole":"Tester","Status":"Failed","AssignedAgentId":"tester",
               "LastExecution":42,"LastProcess":42,"CriterionRetryFeedback":42,
               "LastDispatch":{"WorkerName":"fixture","Command":"fixture","WorkingDirectory":"C:\\fixture",
                 "DispatchedAt":"2026-10-17T11:07:00Z","ProviderName":"xAI",
                 "ClaudeCredentialSourceDirectory":"C:\\login","BriefSnapshot":{"invalid":true}},
               "LastVerification":{"Command":"fixture","WorkingDirectory":"C:\\fixture","ExitCode":1,
                 "StandardOutput":"","StandardError":"Provider budget exhausted.",
                 "CompletedAt":"2026-10-17T11:08:00Z","ProviderFailureKind":"BudgetExhausted",
                 "DispatchStartedAt":"2026-10-17T11:07:00Z"}}],"Timeline":[]}
            """;
        var options = Options();
        var row = await ProviderHoldSeedEvidenceReader.ProjectAsync(id,
            compressed ? GoalSnapshotArchiveCodec.Encode(json) : (object)json, options);
        var task = Assert.Single(row.Tasks);
        Assert.Equal(@"C:\login", task.LastDispatch!.ClaudeCredentialSourceDirectory);
        Assert.Equal(ProviderFailureKind.BudgetExhausted, task.LastVerification!.ProviderFailureKind);
        Assert.Equal(new[] { id }, ProviderHoldSeedPruning.RequiredGoals([id], [row], new(), []));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectAsync_PreservesResetProbeAndCancellationOrder(bool compressed)
    {
        const string id = "aaaaaaaa000000000000000000000000";
        var at = DateTimeOffset.Parse("2026-10-17T11:07:00Z");
        var task = new TaskSnapshot("task", "Fixture", AgentRole.Tester, WorkTaskStatus.Assigned, "tester",
            null, null, [], null, null);
        var reset = ProviderBudgetHoldSchedules.FormatReset(new("xai::<provider-default>", at, "intent"));
        var probe = ProviderBudgetHoldSchedules.FormatProbe(new("xai::<provider-default>", at, 1, "operator")
            { ResetIntentId = "intent" });
        var events = new[]
        {
            new ProgressEventSnapshot(id, null, ProgressKind.TaskNote, reset, at),
            new ProgressEventSnapshot(id, "task", ProgressKind.TaskNote, probe, at),
            new ProgressEventSnapshot(id, "task", ProgressKind.TaskCancelled, "Cancelled", at),
            new ProgressEventSnapshot(id, null, ProgressKind.GoalPolicyDecision, "Unrelated note", at)
        };
        var snapshot = new GoalSnapshot(id, "Fixture", GoalStatus.Active, [task], events);
        var json = JsonSerializer.Serialize(snapshot, Options());
        var row = await ProviderHoldSeedEvidenceReader.ProjectAsync(id,
            compressed ? GoalSnapshotArchiveCodec.Encode(json) : (object)json, Options());
        Assert.Equal(events.Take(3).Select(evt => evt.Message), row.Timeline.Select(evt => evt.Message));
        Assert.Equal(WorkTaskStatus.Assigned, Assert.Single(row.Tasks).Status);
        Assert.Equal("tester", row.Tasks[0].AssignedAgentId);
        Assert.Equal(new TaskId("task"), row.Timeline[1].TaskId);
    }

    [Fact]
    public async Task ProjectAsync_RejectsWrongGoalIdentity()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ProviderHoldSeedEvidenceReader.ProjectAsync(
            "expected", """{"Id":"wrong","Status":"Completed"}""", Options()));
    }

    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
