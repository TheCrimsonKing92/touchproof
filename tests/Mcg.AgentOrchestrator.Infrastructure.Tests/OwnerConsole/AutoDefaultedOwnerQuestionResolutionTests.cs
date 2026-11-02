using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each scene owns its kernel and uniquely rooted stores.
public sealed class AutoDefaultedOwnerQuestionResolutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutoDefaultedQuestion_ShowsPolicyAndAnswer_EvenWithMatchingIntent(bool matchingIntent)
    {
        using var scene = new OwnerQuestionResolutionDialogTests.Scene();
        var request = scene.Harness.Kernel.RequestHumanInput(scene.Goal.Id, null, "Use proposed scope?",
            suggestedDefaultAnswer: "Use proposed scope.");
        Assert.Single(scene.Harness.Kernel.SweepStaleHumanWaits(TimeSpan.Zero));
        if (matchingIntent)
        {
            var store = SqliteOperatorIntentStore.ForDirectories(scene.DirectoryPath, scene.DirectoryPath);
            var at = request.AnsweredAt!.Value.AddSeconds(1);
            var payload = new AnswerOperatorIntentPayload(OperatorAnswerTargetKind.HumanInput, request.Id.Value,
                scene.Goal.Id.Value, "Stale intent answer", OperatorActorKind.Human, []);
            var intent = new OperatorIntentRecord(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
                OperatorIntentVerbs.Answer, scene.Goal.Id.Value, null, JsonSerializer.Serialize(payload),
                [], "owner", "test", "human", at, ActorKind: OperatorActorKind.Human);
            await store.EnqueueAsync(intent);
            Assert.NotNull(await store.ClaimNextAsync(scene.Goal.Id.Value, "test"));
            await store.CompleteAsync(intent.Id, "test", OperatorIntentStatus.Applied, "Answer processed", at);
            scene.Harness.Kernel.RecordOperatorIntentApplied(scene.Goal.Id, intent.Id, intent.Verb, null,
                intent.Actor, intent.Channel, "human", "Applied answer", OperatorActorKind.Human);
        }

        var resolution = Assert.Single(await scene.Reader.ListForGoalAsync(scene.Goal.Id.Value, CancellationToken.None));

        Assert.Equal("Auto-defaulted", resolution.Outcome);
        Assert.Equal("the stale-wait policy", resolution.Answerer);
        Assert.Equal("Use proposed scope.", resolution.Answer);
        Assert.Equal(request.AnsweredAt, resolution.ResolvedAt);
        Assert.Empty(resolution.Evidence);
        var text = OwnerQuestionResolutionText.Build(resolution, TimeZoneInfo.Utc);
        Assert.Contains("Resolution: Auto-defaulted by the stale-wait policy", text);
        Assert.Contains("Use proposed scope.", text);
        Assert.DoesNotContain("Answered", text);
        Assert.DoesNotContain("Stale intent answer", text);
    }
}
