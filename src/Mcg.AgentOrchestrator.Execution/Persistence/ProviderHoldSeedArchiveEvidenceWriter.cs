using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class ProviderHoldSeedArchiveEvidenceWriter
{
    internal static async Task WriteAsync(SqliteConnection connection, string id, string snapshotJson,
        JsonSerializerOptions options, CancellationToken token)
    {
        var evidence = await ProviderHoldSeedEvidenceReader.ProjectAsync(id, snapshotJson, options, token, connection);

        // The caller has inserted the archive and still owns the write transaction.
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {ProviderHoldSeedEvidenceStore.MarkerSql(hasArchive: true)}
            FROM goals g JOIN goal_snapshot_archive a ON a.goal_id = g.id
            WHERE g.id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        var marker = (string)(await command.ExecuteScalarAsync(token))!;
        await ProviderHoldSeedEvidenceStore.UpsertAsync(command, id, marker,
            JsonSerializer.SerializeToUtf8Bytes(evidence, options), token);
    }
}
