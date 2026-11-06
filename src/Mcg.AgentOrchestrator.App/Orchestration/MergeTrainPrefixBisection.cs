using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Prefix 0 proves main green; the confirmed receipt supplies the failing upper bound.
// Failure is monotonic in prefix length by the operator's specification ruling.
internal static class MergeTrainPrefixBisection
{
    internal delegate RunResult? Runner(IReadOnlyList<MergeTrainMemberBinding> prefix, string mainRevision,
        IReadOnlyList<string> tests, CancellationToken cancellationToken);
    internal sealed record RunResult(MergeTrainReproductionAttribution.RunResult Execution,
        IReadOnlyList<string> TrxPaths, string RunId);
    internal sealed record Run(int PrefixLength, int Repetition, IReadOnlyList<string> Members,
        IReadOnlyList<string> Tests, string Verdict, IReadOnlyList<string> TrxPaths, string RunId);
    internal sealed record Result(string Outcome, string? Reason, MergeTrainMemberBinding? Member,
        IReadOnlyList<Run> Runs)
    {
        internal bool OrdinaryFallback => Outcome is "inconclusive-flake" or "inconclusive-split" or "main-baseline-red";
        internal IReadOnlyList<AcceptanceTrxFailure> RoutedFailures(MergeTrainReceipt receipt) =>
            MergeTrainRedAttribution.ReadFatalFailures(receipt.GateTestResultPaths).Select(failure => failure with
            {
                Message = MergeTrainAttributedFailure.BoundMessage(failure.Message) + "\nprefix_trx=" +
                    string.Join(",", Runs.SelectMany(run => run.TrxPaths).Distinct(StringComparer.Ordinal)),
                StackTrace = null
            }).ToArray();
    }

    internal static Result Search(IReadOnlyList<MergeTrainMemberBinding> members, string mainRevision,
        IReadOnlyList<string> tests, bool repeat, Runner? runner, Action<Run> observe,
        CancellationToken cancellationToken)
    {
        var runs = new List<Run>();
        Result Finish(string outcome, string? reason = null, MergeTrainMemberBinding? member = null) =>
            new(outcome, reason, member, runs.ToArray());
        if (members.Count == 0) throw new ArgumentException("Bisection requires members.", nameof(members));
        if (runner is null) return Finish("prefix-bisection unavailable", "runner-absent");
        if (ConductorAcceptanceCohortFocusedAttribution.TrySelect(tests.ToArray(), []) is null)
            return Finish("prefix-bisection unavailable", "unselectable-tests");
        var selected = tests.ToHashSet(StringComparer.Ordinal);
        string Evaluate(int count)
        {
            HashSet<string>? previous = null;
            for (var repetition = 1; repetition <= (repeat ? 2 : 1); repetition++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RunResult? result = null;
                string? reason = null;
                try { result = runner(members.Take(count).ToArray(), mainRevision, tests, cancellationToken); }
                catch (OperationCanceledException)
                {
                    observe(new(count, repetition, members.Take(count).Select(member => member.GoalId.Value).ToArray(),
                        tests, "cancelled", [], "cancelled"));
                    throw;
                }
                catch (Exception ex) { reason = "runner-failed:" + ex.GetType().Name; }
                var execution = result?.Execution;
                if (execution is null || !selected.SetEquals(execution.ExecutedTests) ||
                    execution.Failures.Any(failure => !selected.Contains(failure.TestName ?? "") ||
                        !AcceptanceTrxOutcomeTaxonomy.IsFatal(failure.Outcome) ||
                        ApparatusInfrastructureSignatures.Match(failure.Message, failure.StackTrace) is not null))
                    reason ??= "unusable-result";
                var failed = execution?.Failures.Select(failure => failure.TestName!).ToHashSet(StringComparer.Ordinal) ?? [];
                var verdict = reason is not null ? "unavailable" : failed.Count == 0 ? "passing" :
                    failed.SetEquals(selected) ? "failing" : "inconclusive-split";
                var run = new Run(count, repetition, members.Take(count).Select(member => member.GoalId.Value).ToArray(),
                    tests, verdict, result?.TrxPaths ?? [], result?.RunId ?? "no-result");
                runs.Add(run);
                observe(run); // Exactly one observation for each actual runner invocation.
                if (reason is not null) return "unavailable:" + reason;
                if (previous is not null && !previous.SetEquals(failed)) return "inconclusive-flake";
                previous = failed;
            }
            return previous!.Count == 0 ? "passing" : previous.SetEquals(selected) ? "failing" : "inconclusive-split";
        }
        Result Stop(string verdict) => verdict.StartsWith("unavailable:", StringComparison.Ordinal)
            ? Finish("prefix-bisection unavailable", verdict[12..])
            : Finish(verdict, "tests=" + string.Join(",", tests) + " runs=" + JsonSerializer.Serialize(runs));
        var baseline = Evaluate(0);
        if (baseline == "failing") return Finish("main-baseline-red", "tests=" + string.Join(",", tests));
        if (baseline != "passing") return Stop(baseline);
        var green = 0;
        var red = members.Count;
        while (red - green > 1)
        {
            var middle = green + (red - green) / 2;
            var verdict = Evaluate(middle);
            if (verdict == "failing") red = middle;
            else if (verdict == "passing") green = middle;
            else return Stop(verdict);
        }
        return Finish("culprit", member: members[red - 1]);
    }

    internal static bool HasExternalHistory(CrossGoalTestFailureHistory history,
        IReadOnlyList<MergeTrainMemberBinding> members, IReadOnlyList<string> tests, DateTimeOffset now)
    {
        Dictionary<string, HashSet<string>>? intersection = null;
        foreach (var member in members)
        {
            var read = history.Read(member.GoalId, tests, now);
            if (intersection is null)
                intersection = read.ToDictionary(pair => pair.Key, pair => pair.Value.ToHashSet(StringComparer.OrdinalIgnoreCase));
            else
                foreach (var pair in intersection)
                    pair.Value.IntersectWith(read.TryGetValue(pair.Key, out var goals) ? goals : []);
        }
        return intersection?.Any(pair => pair.Value.Count > 0) == true;
    }

    internal static MergeTrainEjection CreateEjection(MergeTrainReceipt receipt, Result result) =>
        new(result.Member!.GoalId, MergeTrainEjectionReason.RedAttributedMember, [],
            $"Dropped after RED receipt {receipt.ReceiptId}.\nbisection=" + JsonSerializer.Serialize(result.RoutedFailures(receipt)));

    internal static IReadOnlyList<AcceptanceTrxFailure>? ReadEjectionFailures(MergeTrainReceipt receipt, string detail) =>
        MergeTrainReproductionAttribution.ReadEjectionFailures(receipt,
            detail.Replace($"Dropped after RED receipt {receipt.ReceiptId}.\nbisection=",
                $"Dropped after RED receipt {receipt.ReceiptId}.\nreproduction=", StringComparison.Ordinal));
}
