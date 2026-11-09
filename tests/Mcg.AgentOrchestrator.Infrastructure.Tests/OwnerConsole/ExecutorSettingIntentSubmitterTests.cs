using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Harness = OwnerConsoleExecutorsSettingsTestContext;

// Parallel-safe: each case owns its workspace, executor file and SQLite inbox.
public sealed class ExecutorSettingIntentSubmitterTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SubmitToggle_StoredState_QueuesOppositeWithHumanAttribution(bool enabled)
    {
        using var fixture = new Harness();
        fixture.Write("{\"executors\":[{\"id\":\"selected\",\"enabled\":" +
            enabled.ToString().ToLowerInvariant() + "}],\"lanes\":[]}");
        var original = fixture.FileBytes();

        var submission = new ExecutorSettingIntentSubmitter(fixture.Workspace).SubmitToggle("selected");

        var intent = await fixture.Intents.ClaimNextPendingAsync(OperatorIntentScopes.Workspace, "test");
        Assert.NotNull(intent);
        Assert.Equal(submission.IntentId, intent.Id);
        Assert.Equal("selected", submission.ExecutorId);
        Assert.Equal(!enabled, submission.Enabled);
        Assert.Equal(OperatorIntentVerbs.ExecutorSetEnabled, intent.Verb);
        Assert.Equal(OperatorIntentScopes.Workspace, intent.GoalId);
        Assert.Null(intent.TaskId);
        Assert.Equal(new ExecutorSetEnabledOperatorIntentPayload("selected", !enabled),
            JsonSerializer.Deserialize<ExecutorSetEnabledOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options));
        Assert.Equal("operator", intent.Actor);
        Assert.Equal("owner-console", intent.Channel);
        Assert.Equal(OperatorActorKind.Human, intent.ActorKind);
        Assert.Equal("local-process", intent.AuthenticationAssurance);
        Assert.Null(await fixture.Intents.ClaimNextPendingAsync(OperatorIntentScopes.Workspace, "test"));
        Assert.Equal(original, fixture.FileBytes());
    }

    [Fact]
    public async Task SubmitToggle_UnknownId_RefusesWithoutIntentOrStoreWrite()
    {
        using var fixture = new Harness();
        var original = fixture.FileBytes();
        var error = Assert.Throws<ArgumentException>(() =>
            new ExecutorSettingIntentSubmitter(fixture.Workspace).SubmitToggle("ghost"));
        Assert.Contains("ghost", error.Message);
        Assert.Contains("not found", error.Message);
        Assert.Null(await fixture.Intents.ClaimNextPendingAsync(OperatorIntentScopes.Workspace, "test"));
        Assert.Equal(original, fixture.FileBytes());
    }
}
