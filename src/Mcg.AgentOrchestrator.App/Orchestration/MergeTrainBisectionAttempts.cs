using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Receipt-bound outcomes survive child/parent replay. Logs are observations, never the cache.
internal static class MergeTrainBisectionAttempts
{
    internal static MergeTrainPrefixBisection.Result Run(MergeTrainReceipt receipt,
        IReadOnlyList<MergeTrainMemberBinding> members, OrchestratorWorkspace workspace,
        IReadOnlyCollection<Goal> goals, MergeTrainPrefixBisection.Runner? runner,
        DateTimeOffset now, CancellationToken token)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db"),
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS merge_train_bisection_outcomes (
                receipt_id TEXT PRIMARY KEY, train_id TEXT NOT NULL, result_json TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
        command.CommandText = "SELECT result_json FROM merge_train_bisection_outcomes WHERE receipt_id = $receipt AND train_id = $train";
        command.Parameters.AddWithValue("$receipt", receipt.ReceiptId);
        command.Parameters.AddWithValue("$train", receipt.Identity.Value);
        var tests = MergeTrainRedAttribution.ReadFatalFailures(receipt.GateTestResultPaths)
            .Select(failure => failure.TestName ?? "").Distinct(StringComparer.Ordinal).ToArray();
        if (command.ExecuteScalar() is string cached)
        {
            var saved = JsonSerializer.Deserialize<MergeTrainPrefixBisection.Result>(cached)
                ?? throw new InvalidDataException("Stored bisection outcome is missing.");
            var member = members.SingleOrDefault(member => saved.Member is { } culprit && member.GoalId == culprit.GoalId &&
                member.CandidateRevision == culprit.CandidateRevision && member.BranchRevision == culprit.BranchRevision);
            if (saved.Outcome is not ("culprit" or "inconclusive-flake" or "inconclusive-split" or
                    "main-baseline-red" or "prefix-bisection unavailable") ||
                (saved.Outcome == "culprit" ? member is null : saved.Member is not null) ||
                saved.Runs.Any(run => run.PrefixLength < 0 || run.PrefixLength >= members.Count ||
                    !run.Members.SequenceEqual(members.Take(run.PrefixLength).Select(item => item.GoalId.Value)) ||
                    !run.Tests.ToHashSet(StringComparer.Ordinal).SetEquals(tests)))
                throw new InvalidDataException("Stored bisection outcome does not match its receipt.");
            return saved with { Member = member };
        }
        var history = new CrossGoalTestFailureHistory(Path.Combine(workspace.OrchestratorDirectory,
            "acceptance-gate-attempts", AcceptanceFailingTestIndex.FileName), workspace.ExecutionDirectory, () => goals);
        var repeat = MergeTrainPrefixBisection.HasExternalHistory(history, members, tests, now);
        var writer = new ConductEventLogWriter(workspace.ConductEventsLogPath);
        var result = MergeTrainPrefixBisection.Search(members, receipt.Identity.ObservedMainRevision, tests,
            repeat, runner, run => ConductorGateProgressLog.TryAppend(writer, members[0].GoalId.Value,
                $"receipt={receipt.ReceiptId} prefix={run.PrefixLength} members={string.Join(',', run.Members)} " +
                $"tests={string.Join(',', run.Tests)} run={run.Repetition}/{(repeat ? 2 : 1)} verdict={run.Verdict} " +
                $"run_id={run.RunId} trx={string.Join(',', run.TrxPaths)}", "train-bisection"), token);
        foreach (var member in members)
            ConductorGateProgressLog.TryAppend(writer, member.GoalId.Value,
                $"receipt={receipt.ReceiptId} {result.Outcome} reason={result.Reason ?? "none"} " +
                $"source=bisection culprit={result.Member?.GoalId.Value ?? "none"}", "train-bisection-outcome");
        command.CommandText = "INSERT INTO merge_train_bisection_outcomes(receipt_id, train_id, result_json) VALUES($receipt, $train, $result)";
        command.Parameters.AddWithValue("$result", JsonSerializer.Serialize(result));
        command.ExecuteNonQuery();
        return result;
    }
}
