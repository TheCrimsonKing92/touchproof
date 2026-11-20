using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class OperatorControlReceiptStore(string orchestratorDirectory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        NumberHandling = JsonNumberHandling.Strict,
        AllowDuplicateProperties = false
    };

    private readonly string directory = Path.Combine(orchestratorDirectory, "operator-evidence", "control-receipts");

    public OperatorControlReceipt Record(GoalId goalId, string obligationId, string kind, string command,
        int exitStatus, int passedCount, int failedCount, IReadOnlyList<string> trxPaths, string candidateSha)
    {
        var receipt = new OperatorControlReceipt(Guid.NewGuid().ToString("N"), goalId.Value, obligationId, kind,
            command, exitStatus, passedCount, failedCount, trxPaths.ToArray(), candidateSha, DateTimeOffset.UtcNow);
        Validate(receipt);

        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
        var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write);
        try
        {
            using (stream)
            {
                JsonSerializer.Serialize(stream, receipt, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, ReceiptPath(receipt.ReceiptId), overwrite: false);
        }
        catch
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }

        return receipt;
    }

    public (OperatorControlReceipt Receipt, byte[] Bytes)? Read(string receiptId)
    {
        if (!Guid.TryParseExact(receiptId, "N", out var id) ||
            !string.Equals(receiptId, id.ToString("N"), StringComparison.Ordinal))
            return null;

        try
        {
            var bytes = File.ReadAllBytes(ReceiptPath(receiptId));
            var receipt = JsonSerializer.Deserialize<OperatorControlReceipt>(bytes, JsonOptions);
            if (receipt is null || !string.Equals(receipt.ReceiptId, receiptId, StringComparison.Ordinal))
                return null;

            Validate(receipt);
            return (receipt, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    internal static bool IsSafeIdentifier(string? value) =>
        !string.IsNullOrEmpty(value) && char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private string ReceiptPath(string receiptId) => Path.Combine(directory, receiptId + ".json");

    private static void Validate(OperatorControlReceipt receipt)
    {
        if (!IsSafeIdentifier(receipt.GoalId))
            throw new ArgumentException("--goal requires a safe goal identifier.");
        if (!IsSafeIdentifier(receipt.ObligationId))
            throw new ArgumentException("--obligation requires a safe obligation identifier.");
        if (receipt.Kind is not ("negative-control" or "series" or "shard"))
            throw new ArgumentException("--kind requires negative-control, series or shard.");
        if (string.IsNullOrWhiteSpace(receipt.Command) || string.IsNullOrWhiteSpace(receipt.CandidateSha) ||
            receipt.TrxPaths is null || receipt.TrxPaths.Any(string.IsNullOrWhiteSpace) ||
            receipt.PassedCount < 0 || receipt.FailedCount < 0 ||
            receipt.RecordedAt == default || receipt.RecordedAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("Control receipt has invalid field values.");
    }
}
