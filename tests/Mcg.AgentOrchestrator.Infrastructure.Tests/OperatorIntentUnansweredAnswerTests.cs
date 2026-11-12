using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Assert = Xunit.Assert;

// Parallel-safe: each test owns its database and wake directory; no processes are started.
public sealed class OperatorIntentUnansweredAnswerTests
{
    [Xunit.Theory]
    [Xunit.InlineData(OperatorAnswerTargetKind.HumanInput)]
    [Xunit.InlineData(OperatorAnswerTargetKind.Clarification)]
    public async Task UnansweredRequest_EnqueuesOnceAndWakesConductor(OperatorAnswerTargetKind kind)
    {
        using var fixture = new Fixture();
        var recommendation = fixture.Answer(kind, OperatorActorKind.Agent);
        Assert.True(await fixture.Conductor.TryEnqueueUnansweredAnswerAsync(recommendation));
        var persisted = await fixture.Conductor.GetAsync(recommendation.Id);
        Assert.NotNull(persisted);
        Assert.Equal(recommendation.PayloadJson, persisted.PayloadJson);
        Assert.Equal(OperatorActorKind.Agent, persisted.ActorKind);
        Assert.True(File.Exists(fixture.WakePath(recommendation.Id)));

        // Restart/another connection cannot reclaim the same question, even with a new key.
        var duplicate = fixture.Answer(kind, OperatorActorKind.Agent);
        Assert.False(await fixture.Owner.TryEnqueueUnansweredAnswerAsync(duplicate));
        Assert.Single(await fixture.Conductor.ListForGoalAsync("goal"));
        Assert.False(File.Exists(fixture.WakePath(duplicate.Id)));
    }

    [Xunit.Fact]
    public async Task ConcurrentApplications_EnqueueExactlyOneAnswer()
    {
        using var fixture = new Fixture();
        var first = fixture.Answer(OperatorAnswerTargetKind.HumanInput, OperatorActorKind.Agent);
        var second = fixture.Answer(OperatorAnswerTargetKind.HumanInput, OperatorActorKind.Agent);
        var outcomes = await Task.WhenAll(fixture.Conductor.TryEnqueueUnansweredAnswerAsync(first),
            fixture.Owner.TryEnqueueUnansweredAnswerAsync(second));
        Assert.Single(outcomes, enqueued => enqueued);
        var persisted = Assert.Single(await fixture.Conductor.ListForGoalAsync("goal"));
        Assert.Equal(outcomes[0] ? first.Id : second.Id, persisted.Id);
        Assert.False(File.Exists(fixture.WakePath(outcomes[0] ? second.Id : first.Id)));
    }

    [Xunit.Theory]
    [Xunit.InlineData(OperatorAnswerTargetKind.HumanInput, OperatorIntentStatus.Pending)]
    [Xunit.InlineData(OperatorAnswerTargetKind.HumanInput, OperatorIntentStatus.Claimed)]
    [Xunit.InlineData(OperatorAnswerTargetKind.HumanInput, OperatorIntentStatus.Applied)]
    [Xunit.InlineData(OperatorAnswerTargetKind.Clarification, OperatorIntentStatus.Pending)]
    [Xunit.InlineData(OperatorAnswerTargetKind.Clarification, OperatorIntentStatus.Claimed)]
    [Xunit.InlineData(OperatorAnswerTargetKind.Clarification, OperatorIntentStatus.Applied)]
    public async Task RecordedHumanAnswer_PreventsEnqueueAcrossConnections(
        OperatorAnswerTargetKind kind, OperatorIntentStatus status)
    {
        using var fixture = new Fixture();
        var human = fixture.Answer(kind, OperatorActorKind.Human);
        await fixture.Owner.EnqueueAsync(human);
        if (status != OperatorIntentStatus.Pending)
        {
            Assert.Equal(human.Id, (await fixture.Owner.ClaimNextAsync("goal", "owner"))!.Id);
            if (status == OperatorIntentStatus.Applied)
                await fixture.Owner.CompleteAsync(human.Id, "owner", status, "Human answer applied", Fixture.Now);
        }
        Assert.Equal(status, (await fixture.Conductor.GetAsync(human.Id))!.Status);

        var recommendation = fixture.Answer(kind, OperatorActorKind.Agent);
        Assert.False(await fixture.Conductor.TryEnqueueUnansweredAnswerAsync(recommendation));
        Assert.Equal(human.Id, Assert.Single(await fixture.Conductor.ListForGoalAsync("goal")).Id);
        Assert.False(File.Exists(fixture.WakePath(recommendation.Id)));
    }

    [Xunit.Theory]
    [Xunit.InlineData(OperatorAnswerTargetKind.HumanInput)]
    [Xunit.InlineData(OperatorAnswerTargetKind.Clarification)]
    public async Task LaterHumanAnswer_RemainsASeparateAnswerToSameQuestion(OperatorAnswerTargetKind kind)
    {
        using var fixture = new Fixture();
        var recommendation = fixture.Answer(kind, OperatorActorKind.Agent);
        Assert.True(await fixture.Conductor.TryEnqueueUnansweredAnswerAsync(recommendation));
        var human = fixture.Answer(kind, OperatorActorKind.Human);
        Assert.Equal(human.Id, (await fixture.Owner.EnqueueAsync(human)).Id);
        var recorded = await fixture.Conductor.ListForGoalAsync("goal");
        Assert.Equal(2, recorded.Count);
        Assert.Contains(recorded, record => record.Id == recommendation.Id && record.ActorKind == OperatorActorKind.Agent);
        Assert.Contains(recorded, record => record.Id == human.Id && record.ActorKind == OperatorActorKind.Human);
    }

    [Xunit.Theory]
    [Xunit.InlineData("different-question")]
    [Xunit.InlineData("different-kind")]
    [Xunit.InlineData("different-goal")]
    [Xunit.InlineData("rejected")]
    public async Task UnrelatedOrRejectedAnswer_DoesNotBlockEnqueue(string context)
    {
        using var fixture = new Fixture();
        var human = fixture.Answer(context == "different-kind" ? OperatorAnswerTargetKind.Clarification :
            OperatorAnswerTargetKind.HumanInput, OperatorActorKind.Human,
            context == "different-question" ? "another-request" : "request",
            context == "different-goal" ? "another-goal" : "goal");
        await fixture.Owner.EnqueueAsync(human);
        if (context == "rejected")
        {
            Assert.Equal(human.Id, (await fixture.Owner.ClaimNextAsync("goal", "owner"))!.Id);
            await fixture.Owner.CompleteAsync(human.Id, "owner", OperatorIntentStatus.Rejected,
                "Answer was rejected", Fixture.Now);
        }
        var recommendation = fixture.Answer(OperatorAnswerTargetKind.HumanInput, OperatorActorKind.Agent);
        Assert.True(await fixture.Conductor.TryEnqueueUnansweredAnswerAsync(recommendation));
        Assert.NotNull(await fixture.Conductor.GetAsync(recommendation.Id));
    }

    [Xunit.Fact]
    public async Task ConditionalEnqueue_RejectsHumanSubmission()
    {
        using var fixture = new Fixture();
        var human = fixture.Answer(OperatorAnswerTargetKind.HumanInput, OperatorActorKind.Human);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Conductor.TryEnqueueUnansweredAnswerAsync(human));
        Assert.Empty(await fixture.Conductor.ListForGoalAsync("goal"));
    }

    private sealed class Fixture : IDisposable
    {
        internal static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"unanswered-answer-{Guid.NewGuid():N}");
        internal SqliteOperatorIntentStore Owner { get; }
        internal SqliteOperatorIntentStore Conductor { get; }

        internal Fixture()
        {
            var database = Path.Combine(_root, "operator-intents.db");
            var wake = Path.Combine(_root, "wake");
            Owner = new SqliteOperatorIntentStore(database, wake, utcNow: () => Now);
            Conductor = new SqliteOperatorIntentStore(database, wake, utcNow: () => Now);
        }

        internal OperatorIntentRecord Answer(OperatorAnswerTargetKind kind, OperatorActorKind actor,
            string targetId = "request", string goalId = "goal")
        {
            var payload = new AnswerOperatorIntentPayload(kind, targetId, goalId,
                actor == OperatorActorKind.Agent ? "AUTHOR_RECOMMENDATION_APPLIED Use Windows." : "Use Linux.", actor);
            return new OperatorIntentRecord(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
                OperatorIntentVerbs.Answer, goalId, null,
                JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)), [],
                actor == OperatorActorKind.Agent ? "author" : "owner", "test", "test", Now, ActorKind: actor);
        }

        internal string WakePath(string intentId) => Path.Combine(_root, "wake", intentId + SqliteOperatorIntentStore.WakeFileSuffix);
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
