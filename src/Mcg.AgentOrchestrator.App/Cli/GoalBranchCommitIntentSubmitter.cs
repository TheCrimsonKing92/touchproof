using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed class GoalBranchCommitIntentSubmitter(IOperatorIntentStore store, GoalId goalId,
    CliPersistentStateRunner.OperatorIntentAttribution attribution)
{
    internal OperatorIntentRecord Submit(string key, string verb, object payload)
    {
        // Production does an indexed lookup. Store decorators/test doubles can retain the same contract.
        var existing = store is SqliteOperatorIntentStore sqlite
            ? sqlite.GetByIdempotencyKeyAsync(key).GetAwaiter().GetResult()
            : store.ListForGoalAsync(goalId.Value, int.MaxValue).GetAwaiter().GetResult()
                .SingleOrDefault(intent => intent.IdempotencyKey == key);
        if (existing is not null && (existing.GoalId != goalId.Value || existing.Verb != verb ||
            payload is CriterionEvidenceMappingOperatorIntentPayload mapping &&
            JsonSerializer.Deserialize<CriterionEvidenceMappingOperatorIntentPayload>(existing.PayloadJson, OperatorIntentJson.Options) != mapping))
            throw new InvalidOperationException($"IdempotencyConflict: {key} already names a different request.");
        var intent = existing ?? store.EnqueueAsync(new OperatorIntentRecord(
            Guid.NewGuid().ToString("N"), key, verb, goalId.Value, null,
            JsonSerializer.Serialize(payload, payload.GetType(), OperatorIntentJson.Options), [],
            attribution.Actor, attribution.Channel, attribution.AuthenticationAssurance, DateTimeOffset.UtcNow,
            ActorKind: attribution.ActorKind)).GetAwaiter().GetResult();
        Console.WriteLine($"Operator intent {intent.Id}: {intent.Status} verb={intent.Verb}");
        if (intent.Status == OperatorIntentStatus.Rejected)
            throw new InvalidOperationException($"IntentRejected: {intent.Id} {intent.Outcome}");
        return intent;
    }
}
