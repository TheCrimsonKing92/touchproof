using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Answer absence and insertion share the SQLite write transaction used by ordinary intake.
internal static class UnansweredOperatorAnswerEnqueue
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task<bool> TryEnqueueAsync(SqliteConnection connection, OperatorIntentRecord intent,
        Action<SqliteCommand, OperatorIntentRecord> bindIntent, CancellationToken cancellationToken)
    {
        var answer = JsonSerializer.Deserialize<AnswerOperatorIntentPayload>(intent.PayloadJson, Json);
        if (intent.Verb != OperatorIntentVerbs.Answer || intent.ActorKind != OperatorActorKind.Agent ||
            intent.TaskId is not null || answer is null || answer.ActorKind != OperatorActorKind.Agent ||
            !Enum.IsDefined(answer.TargetKind) || string.IsNullOrWhiteSpace(answer.TargetId) ||
            answer.GoalId != intent.GoalId)
            throw new ArgumentException("Conditional enqueue requires a goal-scoped Agent answer.", nameof(intent));

        // BeginTransaction is immediate: another connection cannot commit an answer between
        // reading absence and inserting this answer, including connections in another process.
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT idempotency_key, payload_json FROM operator_intents
                WHERE goal_id = $goal_id AND
                    (idempotency_key = $key OR (verb = $verb AND status <> $rejected))
                """;
            read.Parameters.AddWithValue("$goal_id", intent.GoalId);
            read.Parameters.AddWithValue("$key", intent.IdempotencyKey);
            read.Parameters.AddWithValue("$verb", OperatorIntentVerbs.Answer);
            read.Parameters.AddWithValue("$rejected", OperatorIntentStatus.Rejected.ToString());
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetString(0) == intent.IdempotencyKey) return false;
                AnswerOperatorIntentPayload? existing;
                try { existing = JsonSerializer.Deserialize<AnswerOperatorIntentPayload>(reader.GetString(1), Json); }
                catch (JsonException) { continue; }
                if (existing?.TargetKind == answer.TargetKind && existing.TargetId == answer.TargetId) return false;
            }
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT OR IGNORE INTO operator_intents (
                id, idempotency_key, verb, goal_id, task_id, payload_json,
                payload_file_references_json, actor, channel, authentication_assurance,
                created_at, status, claim_owner, claimed_at, completed_at, outcome, actor_kind, admission_expires_at
            ) VALUES (
                $id, $idempotency_key, $verb, $goal_id, $task_id, $payload_json,
                $payload_file_references_json, $actor, $channel, $authentication_assurance,
                $created_at, $status, $claim_owner, $claimed_at, $completed_at, $outcome, $actor_kind, $admission_expires_at
            )
            """;
        bindIntent(insert, intent);
        var enqueued = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return enqueued;
    }
}
