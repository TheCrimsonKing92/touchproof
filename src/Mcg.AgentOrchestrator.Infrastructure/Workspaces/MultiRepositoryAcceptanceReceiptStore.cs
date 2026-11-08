using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Goal-owned tuple receipts and their immutable per-attempt repository evidence.</summary>
public sealed class MultiRepositoryAcceptanceReceiptStore(string orchestratorDirectory)
{
    public string DirectoryPath { get; } = Path.Combine(orchestratorDirectory, "multi-repository-acceptance");

    public MultiRepositoryAcceptanceReceipt? TryRead(GoalId goalId)
    {
        try
        {
            var path = ReceiptPath(goalId);
            if (!File.Exists(path)) return null;
            var receipt = JsonSerializer.Deserialize<MultiRepositoryAcceptanceReceipt>(File.ReadAllText(path));
            return receipt?.GoalId == goalId ? receipt : null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or IOException)
        {
            // An unreadable receipt cannot authorize a landing hold; the candidate must gate again.
            return null;
        }
    }

    public void Write(MultiRepositoryAcceptanceReceipt receipt, GoalRepositorySet repositories)
    {
        if (!receipt.IsComplete(repositories)) throw new ArgumentException("Incomplete repository receipt.", nameof(receipt));
        Directory.CreateDirectory(DirectoryPath);
        var temporary = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(receipt));
            File.Move(temporary, ReceiptPath(receipt.GoalId), overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public string WriteEvidence(GoalId goalId, string attemptId, string key, object evidence)
    {
        if (!GoalRepositorySet.IsCanonicalKey(key)) throw new ArgumentException("Invalid repository key.", nameof(key));
        if (!Guid.TryParseExact(attemptId, "N", out _)) throw new ArgumentException("Invalid attempt id.", nameof(attemptId));
        var directory = Path.Combine(DirectoryPath, goalId.Value, attemptId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, key + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(evidence));
        return path;
    }

    private string ReceiptPath(GoalId goalId) => Path.Combine(DirectoryPath, goalId.Value + ".json");
}
