using System.Reflection;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: immutable candidate inputs and local selector observations only.
public sealed class MergeTrainSkipReasonTests
{
    [Fact]
    public void Select_ReportsFirstConditionForEverySkippedReadyCandidate()
    {
        var fixture = new Candidates();
        var skips = new Dictionary<GoalId, ConductorMergeTrainSkipReason>();
        var selected = fixture.Select(fixture.Ordered, (id, reason) => skips.Add(id, reason));
        Assert.Equal(fixture.ExpectedReasons.OrderBy(pair => pair.Key.Value), skips.OrderBy(pair => pair.Key.Value));
        Assert.Equal(new[] { fixture.Ordered[0].GoalId, fixture.Ordered[^1].GoalId },
            Assert.IsType<ConductorMergeTrainSelection>(selected).Members.Select(member => member.GoalId));
    }

    [Fact]
    public void Select_PreservesPreChangeMembersAcrossCandidateOrders()
    {
        var fixture = new Candidates();
        var orders = Enumerable.Range(0, fixture.Ordered.Length)
            .Select(offset => fixture.Ordered.Skip(offset).Concat(fixture.Ordered.Take(offset)).ToArray())
            .Append(fixture.Ordered.Reverse().ToArray());
        foreach (var order in orders)
        {
            var before = BeforeChange(order, fixture.Suppressed, fixture.Ineligible, fixture.Attributed, fixture.Implicated);
            Assert.Equal(before, fixture.Select(order)?.Members.Select(member => member.GoalId).ToArray() ?? []);
            Assert.Equal(before, fixture.Select(order, (_, _) => { })?.Members.Select(member => member.GoalId).ToArray() ?? []);
        }
    }

    [Fact]
    public void Select_StopsAtCapAndDoesNotReportNonReadyCandidates()
    {
        var ready = Enumerable.Range(1, 3).Select(index => Ready(index)).ToArray();
        var excluded = new ConductorSpeculativeAcceptanceCandidate(Id(90),
            new GateReadyCandidateProjectionResult.Excluded(GateReadyCandidateExclusionReason.ScopeEmpty));
        var protectedGoal = Ready(91, paths: ["config/acceptance-manifest.json"]);
        var skips = new List<GoalId>();
        var selected = ConductorMergeTrainSelector.Select([excluded, .. ready, protectedGoal],
            onSkipped: (goal, _) => skips.Add(goal));
        Assert.Equal(ready.Select(candidate => candidate.GoalId), Assert.IsType<ConductorMergeTrainSelection>(selected).Members.Select(member => member.GoalId));
        Assert.Empty(skips);
    }

    [Fact]
    public void Select_ReportsOverlapBeforeSuppressionForEachSelectedMember()
    {
        var first = Ready(1);
        var second = Ready(2);
        var skipped = Ready(3, paths: ["src/2.cs"]);
        var suppressed = new HashSet<string> { ConductorAcceptanceCohortSelector.PairFingerprint(Projection(first), Projection(skipped)) };
        var skips = new List<ConductorMergeTrainSkipReason>();
        ConductorMergeTrainSelector.Select([first, second, skipped], suppressed, onSkipped: (_, reason) => skips.Add(reason));
        // Suppression against the first member wins over overlap against the second.
        Assert.Equal([ConductorMergeTrainSkipReason.SuppressedPair], skips);
    }

    private sealed class Candidates
    {
        internal readonly ConductorSpeculativeAcceptanceCandidate[] Ordered;
        internal readonly HashSet<string> Suppressed = [], Ineligible = [], Attributed = [], Implicated = [];
        internal readonly Dictionary<GoalId, ConductorMergeTrainSkipReason> ExpectedReasons = [];

        internal Candidates()
        {
            var first = Ready(1);
            var ineligible = Ready(2, paths: ["config/acceptance-manifest.json"]);
            var attributed = Ready(3);
            var implicated = Ready(4);
            var mismatch = Ready(5) with { GoalId = Id(50) };
            var empty = Ready(6);
            // The constructor forbids this state; inject it solely to exercise the selector's
            // defensive empty-scope clause, without weakening the production invariant.
            typeof(GateReadyCandidateProjection).GetField("<LandingPaths>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Projection(empty), Array.Empty<string>());
            var noResources = Ready(7, resources: []);
            var docs = Ready(8, paths: ["docs/readme.md"]);
            var protectedGoal = Ready(9, paths: ["config/acceptance-manifest.json"]);
            var main = Ready(10, main: new string('e', 40));
            var overlap = Ready(11, resources: Projection(first).ResourceKeys);
            var suppressed = Ready(12);
            Ineligible.Add(ineligible.GoalId.Value);
            Attributed.Add(Key(attributed));
            Implicated.Add(Key(implicated));
            Suppressed.Add(ConductorAcceptanceCohortSelector.PairFingerprint(Projection(first), Projection(suppressed)));
            ConductorSpeculativeAcceptanceCandidate[] skipped =
                [ineligible, attributed, implicated, mismatch, empty, noResources, docs, protectedGoal, main, overlap, suppressed];
            ConductorMergeTrainSkipReason[] reasons =
            [
                ConductorMergeTrainSkipReason.IneligibleCriterionEvidence, ConductorMergeTrainSkipReason.CohortAttributedMember,
                ConductorMergeTrainSkipReason.TrainImplicatedMember, ConductorMergeTrainSkipReason.ProjectionGoalMismatch,
                ConductorMergeTrainSkipReason.NoLandingPaths, ConductorMergeTrainSkipReason.NoResourceKeys,
                ConductorMergeTrainSkipReason.DocsTreeOnly, ConductorMergeTrainSkipReason.OwnerProtectedPath,
                ConductorMergeTrainSkipReason.MainRevisionMismatch, ConductorMergeTrainSkipReason.OverlapsSelectedMember,
                ConductorMergeTrainSkipReason.SuppressedPair
            ];
            for (var index = 0; index < skipped.Length; index++) ExpectedReasons.Add(skipped[index].GoalId, reasons[index]);
            Ordered = [first, .. skipped, Ready(13)];
        }

        internal ConductorMergeTrainSelection? Select(IReadOnlyList<ConductorSpeculativeAcceptanceCandidate> ordered,
            Action<GoalId, ConductorMergeTrainSkipReason>? skipped = null) =>
            ConductorMergeTrainSelector.Select(ordered, Suppressed, Ineligible, Attributed, Implicated, skipped);
    }

    private static GoalId Id(int index) => new(index.ToString("x32"));
    private static GateReadyCandidateProjection Projection(ConductorSpeculativeAcceptanceCandidate candidate) =>
        ((GateReadyCandidateProjectionResult.Ready)candidate.ProjectionResult).Projection;
    private static string Key(ConductorSpeculativeAcceptanceCandidate candidate) =>
        ConductorAcceptanceCohortAttributedMembers.Key(candidate.GoalId, Projection(candidate).CandidateRevision);

    private static ConductorSpeculativeAcceptanceCandidate Ready(int index, IReadOnlyList<string>? paths = null,
        IReadOnlyList<string>? resources = null, string? main = null) => new(Id(index),
        new GateReadyCandidateProjectionResult.Ready(new(Id(index), GoalLifecycleState.Verified,
            GateReadyVerificationState.Satisfied, ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto,
            paths ?? [$"src/{index}.cs"], resources ?? [$"resource:{index}"],
            new(index.ToString("x40"), main ?? new string('f', 40), GateReadyMergeStatus.Clean, GateReadyMergeReason.NoConflictsDetected))));

    // Frozen pre-change selector (c958b4b36): an independent oracle for membership parity.
    private static GoalId[] BeforeChange(IReadOnlyList<ConductorSpeculativeAcceptanceCandidate> candidates,
        IReadOnlySet<string> suppressed, IReadOnlySet<string> ineligible, IReadOnlySet<string> attributed,
        IReadOnlySet<string> implicated)
    {
        var selected = new List<GateReadyCandidateProjection>(3);
        foreach (var candidate in candidates)
        {
            if (candidate.ProjectionResult is not GateReadyCandidateProjectionResult.Ready ready) continue;
            var projection = ready.Projection;
            if (ineligible.Contains(candidate.GoalId.Value) ||
                attributed.Contains(ConductorAcceptanceCohortAttributedMembers.Key(candidate.GoalId, projection.CandidateRevision)) ||
                implicated.Contains(ConductorAcceptanceCohortAttributedMembers.Key(candidate.GoalId, projection.CandidateRevision)) ||
                projection.GoalId != candidate.GoalId || projection.LandingPaths.Count == 0 || projection.ResourceKeys.Count == 0 ||
                GoalAcceptanceVerifier.ClassifyDotnetShardDisposition(projection.LandingPaths) == DotnetShardDisposition.DocsTreeOnlyCandidate ||
                OwnerProtectedLandingPaths.Contains(projection.LandingPaths) ||
                (selected.Count > 0 && !projection.MainRevision.Equals(selected[0].MainRevision, StringComparison.Ordinal)) ||
                selected.Any(existing =>
                    existing.ResourceKeys.Intersect(projection.ResourceKeys, StringComparer.OrdinalIgnoreCase).Any() ||
                    existing.LandingPaths.Any(left => projection.LandingPaths.Any(right => RepositoryPathOverlap.Overlaps(left, right))) ||
                    suppressed.Contains(ConductorAcceptanceCohortSelector.PairFingerprint(existing, projection)))) continue;
            selected.Add(projection);
            if (selected.Count == 3) break;
        }
        return selected.Count >= 2 ? selected.Select(member => member.GoalId).ToArray() : [];
    }
}
