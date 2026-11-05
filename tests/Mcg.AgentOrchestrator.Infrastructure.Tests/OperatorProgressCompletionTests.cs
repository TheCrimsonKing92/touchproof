using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private workspaces and async-local console capture.
public sealed class OperatorProgressCompletionTests
{
    [Theory]
    [InlineData("current", false, false)]
    [InlineData("current", true, true)]
    [InlineData("positional", false, true)]
    [InlineData("positional", true, false)]
    [InlineData("explicit", false, false)]
    [InlineData("explicit", true, true)]
    public async Task CliCompletion_InputForms_PersistExactManualReceipt(
        string target, bool file, bool interactive)
    {
        using var fixture = new OperatorProgressCompletionFixture();
        var note = file ? "  Observed behavior\r\nsecond line  \r\n" : "Observed behavior\r\nsecond line";
        await fixture.Save();
        var args = new List<string> { "progress" };
        if (target == "explicit") args.Add("--goal");
        if (target != "current") args.Add(fixture.GoalId.Value[..8]);
        args.AddRange(["1", "completed"]);
        if (file)
        {
            var path = Path.Combine(fixture.Root, "note.txt");
            await File.WriteAllTextAsync(path, note);
            args.AddRange(["--text-file", path]);
        }
        else args.Add(note);
        args.AddRange(["--operator-actor", "reviewing-human", "--actor-kind", "human",
            "--idempotency-key", "completion-key"]);
        if (!interactive) args.Add("--json");
        IReadOnlyList<string> parts = interactive
            ? CliArgumentParser.SplitCommand("progress " + string.Join(' ', args.Skip(1).Select(arg => "\"" + arg + "\"")))
            : args;
        var output = fixture.Submit(parts);
        if (!interactive)
        {
            using var json = JsonDocument.Parse(output);
            Assert.Equal("mcg.operator-intent", json.RootElement.GetProperty("schema").GetString());
        }
        else Assert.Contains("Operator intent queued:", output);
        var intent = Assert.Single(await fixture.Store.ListForGoalAsync(fixture.GoalId.Value));
        var payload = JsonSerializer.Deserialize<ProgressOperatorIntentPayload>(intent.PayloadJson, JsonSerializerOptions.Web)!;
        Assert.Equal(note, payload.CompletionVerification!.Note);
        Assert.Equal(OperatorActorKind.Human, intent.ActorKind);
        Assert.Equal("cli", intent.Channel);
        Assert.Equal("local-process", intent.AuthenticationAssurance);
        Assert.Equal("reviewing-human", intent.Actor);
        Assert.Equal(WorkTaskStatus.Assigned, fixture.Task.Status);
        Assert.Empty(fixture.Task.VerificationHistory);

        Assert.IsType<OperatorIntentOfflineOutcome.Applied>(await fixture.ApplyOffline());
        await fixture.Reload();
        Assert.Equal(WorkTaskStatus.Completed, fixture.Task.Status);
        Assert.Single(fixture.Goal.Timeline, item => item.TaskId == fixture.TaskId && item.Kind == ProgressKind.TaskCompleted);
        var receipt = Assert.Single(fixture.Task.VerificationHistory);
        Assert.Equal("manual-verification passed", receipt.Command);
        Assert.Equal(VerificationEvidenceSource.Manual, receipt.EvidenceSource);
        Assert.Equal(note, receipt.AuthoritativeStandardOutput);
        Assert.Equal(fixture.Root, receipt.WorkingDirectory);
        Assert.Equal(intent.CreatedAt, receipt.CompletedAt);
        Assert.True(receipt.Succeeded);
        Assert.Null(fixture.Task.LastExecution);
        var applied = Assert.Single(fixture.Goal.Timeline, item => item.OperatorIntentApplied?.IntentId == intent.Id)
            .OperatorIntentApplied!;
        Assert.Equal(intent.Actor, applied.Actor);
        Assert.Equal(intent.Channel, applied.Channel);
        Assert.Equal(intent.AuthenticationAssurance, applied.AuthenticationAssurance);
        Assert.Equal(intent.ActorKind, applied.ActorKind);
        Assert.Empty(AgentOrchestratorKernel.BuildVerificationWorklist(fixture.Goal).Items);
        var attention = CollaborationItemStore.ForDirectory(fixture.Workspace.OrchestratorDirectory);
        await attention.RaiseAsync(CollaborationItemType.Decision, fixture.GoalId.Value,
            "Old task gate", "Missing receipt", AwaitingVerificationGateAttention.Prefix + fixture.GoalId.Value);
        new AwaitingVerificationGateAttention(attention).ReconcileAll([fixture.Goal]);
        Assert.Empty(await attention.GetAttentionQueueAsync());
        Assert.NotEqual(GoalStatus.Completed, fixture.Goal.Status);

        fixture.Submit(parts);
        Assert.Equal(intent.Id, Assert.Single(await fixture.Store.ListForGoalAsync(fixture.GoalId.Value)).Id);
        Assert.IsType<OperatorIntentOfflineOutcome.NothingPending>(await fixture.ApplyOffline());
        await fixture.Reload();
        Assert.Single(fixture.Task.VerificationHistory);
    }
}
