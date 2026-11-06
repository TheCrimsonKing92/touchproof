using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Receipt-bound outcomes survive child/parent replay. Logs are observations, never the cache.
internal static class MergeTrainBisectionAttempts
{
    internal static MergeTrainPrefixBisection.Result Run(MergeTrainReceipt receipt,
        IReadOnlyList<MergeTrainMemberBinding> members, OrchestratorWorkspace workspace,
        IReadOnlyCollection<Goal> goals, MergeTrainPrefixBisection.Runner? runner,
        DateTimeOffset now, CancellationToken token)
    {
        var store = new MergeTrainAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory,
            "merge-train-acceptance.db"));
        var tests = MergeTrainRedAttribution.ReadFatalFailures(receipt.GateTestResultPaths)
            .Select(failure => failure.TestName ?? "").Distinct(StringComparer.Ordinal).ToArray();
        MergeTrainPrefixBisection.Result Decode(string cached)
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
        if (store.ReadBisectionOutcome(receipt.ReceiptId, receipt.Identity.Value) is { } cached)
            return Decode(cached);
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
        return Decode(store.SaveBisectionOutcome(receipt.ReceiptId, receipt.Identity.Value,
            JsonSerializer.Serialize(result)));
    }
}
