using System.Globalization;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

// Offline policies learn from earlier receipts; only retrospective labels may use later passes.
internal static class MergeTrainDropBacktest
{
    private static readonly string[] Features =
        ["prior-red-count", "revision-count", "recent-landing-overlap", "diff-size", "change-risk-tier"];

    internal static int Feature(string name, IReadOnlyList<MergeTrainReceipt> receipts,
        int decisionIndex, MergeTrainMemberBinding member)
    {
        var at = receipts[decisionIndex].CompletedAt;
        var earlier = receipts.Where(receipt => receipt.CompletedAt < at);
        return name switch
        {
            "prior-red-count" => earlier.Count(receipt => receipt.Outcome == MergeTrainGateOutcome.Failed &&
                receipt.Identity.Members.Any(prior => prior.GoalId == member.GoalId)),
            "revision-count" => earlier.SelectMany(receipt => receipt.Identity.Members)
                .Where(prior => prior.GoalId == member.GoalId).Select(prior => prior.CandidateRevision)
                .Append(member.CandidateRevision).Distinct(StringComparer.Ordinal).Count(),
            "recent-landing-overlap" => member.LandingPaths.Distinct(StringComparer.OrdinalIgnoreCase)
                .Intersect(earlier.Where(receipt => receipt.Outcome == MergeTrainGateOutcome.Passed &&
                        receipt.CompletedAt >= at.AddHours(-24))
                    .SelectMany(receipt => receipt.Identity.Members).SelectMany(prior => prior.LandingPaths),
                    StringComparer.OrdinalIgnoreCase).Count(),
            "diff-size" => member.LandingPaths.Count,
            "change-risk-tier" => (int)member.ChangeRiskTier,
            _ => throw new ArgumentException($"Unknown feature: {name}", nameof(name))
        };
    }

    internal static IReadOnlyList<string> Run(IReadOnlyList<MergeTrainReceipt> receipts, int trainingCount) =>
        Compute(receipts, trainingCount).Lines;

    internal static ReplayHarnessResult Replay(IReadOnlyList<MergeTrainReceipt> receipts, int trainingCount) =>
        Compute(receipts, trainingCount).Result;

    private static (ReplayHarnessResult Result, IReadOnlyList<string> Lines) Compute(
        IReadOnlyList<MergeTrainReceipt> source, int trainingCount)
    {
        if (trainingCount <= 0) throw new ArgumentOutOfRangeException(nameof(trainingCount));
        IReadOnlyList<MergeTrainReceipt> receipts = source.OrderBy(receipt => receipt.CompletedAt)
            .ThenBy(receipt => receipt.ReceiptId, StringComparer.Ordinal).ToArray();
        var splitAt = receipts.Count == 0 ? DateTimeOffset.MinValue :
            receipts[Math.Min(trainingCount, receipts.Count) - 1].CompletedAt;
        var split = receipts.Count(receipt => receipt.CompletedAt <= splitAt);
        var horizon = receipts.Count == 0 ? DateTimeOffset.MinValue : receipts[^1].CompletedAt.AddHours(-24);
        var cases = new List<ReplayHarnessCase>();
        // Receipt ids may repeat; attribution belongs to this particular case instance.
        var points = new Dictionary<ReplayHarnessCase, (int Index, MergeTrainMemberBinding Culprit)>(
            ReferenceEqualityComparer.Instance);
        for (var i = 0; i < receipts.Count; i++)
        {
            var receipt = receipts[i];
            if (receipt.Outcome != MergeTrainGateOutcome.Failed) continue;
            string? reason;
            MergeTrainMemberBinding[] suspects = [];
            if (receipt.Identity.Members.Count < 3) reason = "pair";
            else if (receipt.CompletedAt > horizon) reason = "recent";
            else
            {
                suspects = receipt.Identity.Members.Where(member => !receipts.Any(later =>
                    later.CompletedAt > receipt.CompletedAt && later.Outcome == MergeTrainGateOutcome.Passed &&
                    later.Identity.Members.Any(passed => passed.GoalId == member.GoalId &&
                        passed.CandidateRevision == member.CandidateRevision))).ToArray();
                reason = suspects.Length switch { 0 => "no-culprit", 1 => null, _ => "ambiguous" };
            }
            var item = reason is null
                ? ReplayHarnessCase.CreateReplayable(receipt.ReceiptId, receipt.CompletedAt,
                    receipt.Identity.Members[^1].GoalId.Value)
                : ReplayHarnessCase.CreateNonReplayable(receipt.ReceiptId, receipt.CompletedAt, reason);
            cases.Add(item);
            if (item.Replayable) points.Add(item, (i, suspects[0]));
        }

        MergeTrainMemberBinding Pick(int index, string feature) => receipts[index].Identity.Members
            .Select((member, order) => (Member: member, Order: order, Value: Feature(feature, receipts, index, member)))
            .OrderByDescending(item => item.Value).ThenByDescending(item => item.Order).First().Member;
        var holdout = ReplayHoldoutSplit.Create(cases, splitAt);
        var chosen = holdout.Learn(trainingCases => Features.Select(feature => (Name: feature,
                Hits: trainingCases.Where(item => item.Replayable)
                    .Count(item => Pick(points[item].Index, feature) == points[item].Culprit)))
            .OrderByDescending(feature => feature.Hits).First());
        var training = holdout.Training.Where(item => item.Replayable).ToList();
        var evaluatedCases = holdout.Evaluation.Select(item =>
        {
            if (!item.Replayable) return item;
            var evaluated = item with { CandidateDecision = Pick(points[item].Index, chosen.Name).GoalId.Value };
            points.Add(evaluated, points[item]);
            return evaluated;
        }).ToArray();
        var evaluation = evaluatedCases.Where(item => item.Replayable).ToList();
        var pairs = evaluatedCases.Count(item => item.NotReplayableReason == "pair");
        var recent = evaluatedCases.Count(item => item.NotReplayableReason == "recent");
        var noCulprit = evaluatedCases.Count(item => item.NotReplayableReason == "no-culprit");
        var ambiguous = evaluatedCases.Count(item => item.NotReplayableReason == "ambiguous");
        var passedMinutes = receipts.Where(receipt => receipt.CompletedAt <= splitAt && receipt.Outcome == MergeTrainGateOutcome.Passed)
            .Select(receipt => receipt.GateElapsedMilliseconds / 60000.0).Order().ToArray();
        var median = passedMinutes.Length == 0 ? 0 :
            (passedMinutes[(passedMinutes.Length - 1) / 2] + passedMinutes[passedMinutes.Length / 2]) / 2;

        (int Hits, int Landings, double Minutes) Cost(Func<ReplayHarnessCase, string?> pick)
        {
            var hits = 0;
            var landings = 0;
            var minutes = 0.0;
            foreach (var point in evaluation)
            {
                var receipt = receipts[points[point].Index];
                var redMinutes = receipt.GateElapsedMilliseconds / 60000.0;
                if (pick(point) == points[point].Culprit.GoalId.Value)
                {
                    hits++;
                    landings += receipt.Identity.Members.Count - 1;
                    minutes += redMinutes + median;
                }
                else minutes += 2 * redMinutes;
            }
            return (hits, landings, minutes);
        }
        var newest = Cost(item => item.RecordedDecision);
        var risk = Cost(item => item.CandidateDecision);
        var result = new ReplayHarnessResult(holdout.Training.Concat(evaluatedCases), new Dictionary<string, double>
        {
            ["drop-newest.hits"] = newest.Hits,
            ["drop-newest.landings"] = newest.Landings,
            ["drop-newest.gate-minutes"] = newest.Minutes,
            ["risk-ordered.hits"] = risk.Hits,
            ["risk-ordered.landings"] = risk.Landings,
            ["risk-ordered.gate-minutes"] = risk.Minutes,
            ["training.decision-points"] = training.Count,
            ["evaluation.decision-points"] = evaluation.Count
        });
        string PerLanding((int Hits, int Landings, double Minutes) cost) => cost.Landings == 0 ? "none" :
            (cost.Minutes / cost.Landings).ToString("F1", CultureInfo.InvariantCulture);
        var verdict = evaluation.Count < 10 || passedMinutes.Length == 0 ? "inconclusive" :
            risk.Hits * 2 >= newest.Hits * 3 && risk.Hits > newest.Hits && risk.Landings > 0 &&
            (newest.Landings == 0 || risk.Minutes / risk.Landings <= 0.9 * newest.Minutes / newest.Landings)
                ? "confirmed" : "refuted";
        var lines = new List<string>
        {
            $"receipts: total={receipts.Count} training={split} evaluation={receipts.Count - split}",
            $"decision points: training={training.Count} evaluation={evaluation.Count}; evaluation excluded: pairs={pairs} recent={recent} no-culprit={noCulprit} ambiguous={ambiguous}",
            $"replay: replayability={result.ReplayableCount}/{result.Cases.Count} rate={result.ReplayabilityRateText} diff-cases={result.DecisionDiff.Count}",
            $"chosen feature: {chosen.Name} training hits={chosen.Hits}/{training.Count}",
            $"drop-newest: hits={newest.Hits}/{evaluation.Count} gate-minutes per landing={PerLanding(newest)}",
            $"risk-ordered: hits={risk.Hits}/{evaluation.Count} gate-minutes per landing={PerLanding(risk)}"
        };
        lines.AddRange(Features.Select(feature =>
            $"feature {feature}: evaluation hits={evaluation.Count(point => Pick(points[point].Index, feature) == points[point].Culprit)}/{evaluation.Count}"));
        lines.Add("bar: risk hits >= 1.5x newest hits and gate-minutes per landing <= 0.9x newest, with at least 10 evaluation decision points");
        lines.Add($"verdict: {verdict}");
        return (result, lines);
    }
}
