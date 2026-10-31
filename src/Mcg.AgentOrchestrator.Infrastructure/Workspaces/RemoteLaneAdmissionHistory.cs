using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class RemoteLaneAdmissionHistory(IReadOnlyList<RemoteExecutorHealthRecord> records)
{
    internal IReadOnlyList<RemoteExecutorHealthRecord> Records { get; } = records;

    internal static RemoteLaneAdmissionHistory Read(string path)
    {
        try
        {
            return new(SharedJsonlFile.ReadLines(path).TakeLast(RemoteLaneOfferHistoryReader.LineLimit)
                .Select(RemoteExecutorHealthLedger.TryParse).OfType<RemoteExecutorHealthRecord>().ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new([]); }
    }

    internal (double? Median, int Samples) MatchingDuration(string lane, string filterHash,
        string executorId, int concurrency)
    {
        var durations = Records.Where(record => record.Outcome == RemoteLaneOutcomeCode.Accepted &&
                record.Lane == lane && record.Expected?.Filter == filterHash && record.ExecutorId == executorId &&
                record.Admission?.Concurrency == concurrency)
            .Select(Duration).OfType<double>().Order().ToArray();
        return durations.Length == 0 ? (null, 0) : (RemoteLaneCostModel.Median(durations), durations.Length);
    }

    // Match the overall offer history's submission-to-fetch boundaries and status fallback.
    private static double? Duration(RemoteExecutorHealthRecord record)
    {
        if (record.Attempt is not { } attempt) return null;
        double? seconds = null;
        if (attempt.Steps is { Count: > 0 } && attempt.Fetches is { Count: > 0 })
            seconds = (attempt.Fetches.Max(step => step.EndedAt) - attempt.Steps.Min(step => step.StartedAt)).TotalSeconds;
        if (seconds is not > 0 && attempt.LastStatus is { ValueKind: JsonValueKind.Object } status &&
            status.TryGetProperty("seconds", out var duration) && duration.ValueKind == JsonValueKind.Number &&
            duration.TryGetDouble(out var fallback)) seconds = fallback;
        return seconds is { } total && double.IsFinite(total) && total > 0 ? total : null;
    }
}
