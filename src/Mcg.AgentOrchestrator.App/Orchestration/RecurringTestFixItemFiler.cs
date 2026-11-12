using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal interface IRecurringTestFixItemStore
{
    IReadOnlyList<BacklogItem> Find(string prefix);
    bool Insert(BacklogItem item);
    void AppendNote(string id, string text);
}

// The identity hash and generation make insert-if-absent the cross-process deduplication boundary.
internal sealed class RecurringTestFixItemFiler(IRecurringTestFixItemStore store,
    Action<string, string> raiseAttention, Action<string> resolveAttention)
{
    internal static string Prefix(string key) => "recurring-test-" +
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16] + "-";

    internal BacklogItem? Latest(string key) => store.Find(Prefix(key))
        .OrderByDescending(item => int.TryParse(item.Id[Prefix(key).Length..], out var generation) ? generation : -1).FirstOrDefault();

    internal DateTimeOffset? ClosedAt(string key)
    {
        var closed = store.Find(Prefix(key)).Where(item => item.Status != BacklogItemStatus.Open).ToArray();
        foreach (var item in closed)
            try { resolveAttention(item.Id); }
            catch (Exception) { /* Attention availability cannot erase the store's closure watermark. */ }
        return closed.Length == 0 ? null : closed.Max(item => item.UpdatedAt);
    }

    internal string? Ensure(string goalId, ApparatusRedFailingTest test,
        IReadOnlyList<RecurringTestFailureOccurrence> occurrences, string? candidateSha, DateTimeOffset now,
        int minimumGoals = RecurringUnrelatedTestRule.MinimumOtherGoals + 1, string? attemptId = null)
    {
        if (CrossGoalTestFailureHistory.TestKey(test.TestIdentity) is not { } key) return null;
        var latest = Latest(key);
        var message = FirstLine(test.FailureMessage);
        if (latest is { Status: BacklogItemStatus.Open })
        {
            store.AppendNote(latest.Id, $"goal={goalId} attempt={attemptId ?? "not recorded"} candidate={candidateSha ?? "unknown"} lane={test.CheckName} message={message}");
            return latest.Id;
        }
        if (occurrences.Select(row => row.GoalId).Append(goalId).Distinct(StringComparer.OrdinalIgnoreCase).Count() < minimumGoals)
            return null;
        var generation = latest is null ? 1 : int.Parse(latest.Id[Prefix(key).Length..]) + 1;
        var id = Prefix(key) + generation;
        var body = new StringBuilder($"Test identity: {key}\nOwner: operator. Repair the test and land its fix goal.\n" +
            $"Filing gate: goal={goalId} attempt={attemptId ?? "not recorded"} candidate={candidateSha ?? "unknown"} lane={test.CheckName} message={message}\n");
        foreach (var row in occurrences.OrderBy(row => row.At))
            body.AppendLine($"goal={row.GoalId} at={row.At:O} source={row.Source} attempt={row.AttemptId ?? "not recorded"} " +
                $"candidate={row.CandidateSha ?? "not recorded"} lane={row.Lane ?? "not recorded"} " +
                $"message fingerprint={row.MessageFingerprint ?? "not recorded"}; " +
                $"normalized fingerprint matches filing={row.MessageFingerprint is not null && row.MessageFingerprint == AcceptanceFailingTestIndex.ComputeMessageFingerprint(test.FailureMessage)}; earlier message text not recorded");
        var item = new BacklogItem(id, $"Repair recurring test {key}", body.ToString(), BacklogItemStatus.Open,
            now, now, goalId, Tags: "recurring-unrelated-test");
        if (store.Insert(item))
            try { raiseAttention(id, $"Recurring test {key}; operator owns backlog item {id}. Land or close the fix item; unrelated goals re-gate at most twice."); }
            catch (Exception ex)
            {
                var failure = new RecurringTestFixItemAttentionException(id, ex);
                store.AppendNote(id, failure.Message);
                throw failure;
            }
        return id;
    }

    private static string FirstLine(string? text) => string.IsNullOrWhiteSpace(text) ? "not recorded" : text.Split(['\r', '\n'])[0];
}
