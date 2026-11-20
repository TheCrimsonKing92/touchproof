using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

public static class DecisionSnapshotComparer
{
    public const string GateEligibility = "gate-eligibility";
    public const string DispatchReadiness = "dispatch-readiness";
    public const string Exception = "exception";
    private const string DecisionDetails = "decision-details";

    public static DecisionSnapshotComparison Compare(
        string adoptedPath, string candidatePath, IReadOnlyList<DecisionDeclaredChange> declaredChanges)
    {
        try
        {
            var exemptions = ReadExemptions(declaredChanges);
            var adopted = DecisionSnapshot.Read(adoptedPath);
            var candidate = DecisionSnapshot.Read(candidatePath);
            var before = Validate(adopted, "Adopted");
            var after = Validate(candidate, "Candidate");
            var invalidBaseline = before.Values.FirstOrDefault(goal => goal.Exception is not null);
            if (invalidBaseline is not null)
            {
                throw new InvalidDataException(
                    $"Adopted goal '{invalidBaseline.GoalId}' recorded {invalidBaseline.Exception!.Type}: {invalidBaseline.Exception.Message}");
            }

            ValidateCompatibility(adopted.EvaluationContext, candidate.EvaluationContext, before, after);
            var differences = new List<DecisionSnapshotDifference>();
            foreach (var id in before.Keys.Union(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var previous = before.GetValueOrDefault(id);
                var current = after.GetValueOrDefault(id);
                void Add(string kind, bool regression = true) =>
                    differences.Add(new(id, kind, previous, current, regression,
                        regression && exemptions.Contains(new(id, kind))));

                if (current?.Exception is not null)
                {
                    Add(Exception);
                    continue;
                }

                if (previous?.Decision is not { } oldDecision) continue;

                var newDecision = current?.Decision;
                if (oldDecision.GateOutcome == DecisionSnapshotDecision.CandidateBuilt &&
                    newDecision?.GateOutcome != DecisionSnapshotDecision.CandidateBuilt)
                {
                    Add(GateEligibility);
                }

                if (oldDecision.DispatchReadyTaskIds.Count > 0 && newDecision?.DispatchReadyTaskIds.Count is null or 0)
                {
                    Add(DispatchReadiness);
                }
                else if (newDecision is not null &&
                    !newDecision.DispatchReadyTaskIds.ToHashSet(StringComparer.Ordinal)
                        .SetEquals(oldDecision.DispatchReadyTaskIds))
                {
                    Add(DispatchReadiness, regression: false);
                }

                if (newDecision is not null &&
                    (oldDecision.Status != newDecision.Status || oldDecision.HoldState != newDecision.HoldState ||
                    oldDecision.CountsAsPassed.Count != newDecision.CountsAsPassed.Count ||
                    oldDecision.CountsAsPassed.Any(task =>
                        !newDecision.CountsAsPassed.TryGetValue(task.Key, out var passed) || task.Value != passed) ||
                    oldDecision.GateOutcome != newDecision.GateOutcome &&
                        oldDecision.GateOutcome != DecisionSnapshotDecision.CandidateBuilt ||
                    oldDecision.GateOutcome == newDecision.GateOutcome && oldDecision.DeclineReason != newDecision.DeclineReason))
                {
                    Add(DecisionDetails, regression: false);
                }
            }

            return new(null, differences);
        }
        catch (System.Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException)
        {
            return new(ex.Message, []);
        }
    }

    private static HashSet<DecisionDeclaredChange> ReadExemptions(IReadOnlyList<DecisionDeclaredChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var exemptions = new HashSet<DecisionDeclaredChange>();
        foreach (var change in changes)
        {
            if (change is null || !ValidId(change.GoalId) ||
                change.DecisionKind is not (GateEligibility or DispatchReadiness or Exception))
            {
                throw new InvalidDataException("A declared change requires an exact goalId and a supported decisionKind.");
            }
            exemptions.Add(change);
        }
        return exemptions;
    }

    private static Dictionary<string, DecisionSnapshotGoal> Validate(DecisionSnapshot snapshot, string side)
    {
        if (snapshot.SchemaVersion != DecisionSnapshot.CurrentSchemaVersion)
            throw new InvalidDataException($"{side} snapshot has unsupported schema version {snapshot.SchemaVersion}.");

        if (snapshot.EvaluationContext is not { } context ||
            context.AsOf.Offset != TimeSpan.Zero || context.SlotIndex != 0 ||
            !Enum.GetNames<Mcg.AgentOrchestrator.Core.Conductor.ConductorTransitionDecision>()
                .Contains(context.VerifiedTransition, StringComparer.Ordinal) ||
            string.IsNullOrWhiteSpace(context.BuildIdentity) ||
            context.StateCopyHash is { } hash && (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) ||
            snapshot.Goals is null)
        {
            throw new InvalidDataException($"{side} snapshot has invalid evaluation context or goals.");
        }

        var goals = new Dictionary<string, DecisionSnapshotGoal>(StringComparer.Ordinal);
        foreach (var goal in snapshot.Goals)
        {
            if (goal is null || !ValidId(goal.GoalId) || !goals.TryAdd(goal.GoalId, goal))
                throw new InvalidDataException($"{side} snapshot has a missing, invalid or duplicate goal ID.");
            if ((goal.Decision is null) == (goal.Exception is null) ||
                goal.Exception is { } error && (string.IsNullOrWhiteSpace(error.Type) || error.Message is null) ||
                goal.Decision is { } decision && !ValidDecision(decision))
            {
                throw new InvalidDataException($"{side} snapshot goal '{goal.GoalId}' has an invalid outcome.");
            }
        }

        return goals;
    }

    private static bool ValidDecision(DecisionSnapshotDecision decision)
    {
        var validGate = decision.GateOutcome switch
        {
            DecisionSnapshotDecision.CandidateBuilt => decision.DeclineReason is null,
            DecisionSnapshotDecision.Declined => !string.IsNullOrWhiteSpace(decision.DeclineReason),
            _ => false
        };

        return validGate && Enum.IsDefined(decision.Status) &&
            decision.DispatchReadyTaskIds is { } ready && decision.CountsAsPassed is { } tasks &&
            tasks.Keys.All(ValidId) && ready.All(id => ValidId(id) && tasks.ContainsKey(id)) &&
            ready.Distinct(StringComparer.Ordinal).Count() == ready.Count;
    }

    private static bool ValidId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.IndexOfAny(['*', '?']) < 0;

    private static void ValidateCompatibility(
        DecisionSnapshotContext adopted, DecisionSnapshotContext candidate,
        Dictionary<string, DecisionSnapshotGoal> before, Dictionary<string, DecisionSnapshotGoal> after)
    {
        if (adopted.AsOf != candidate.AsOf || adopted.SlotIndex != candidate.SlotIndex ||
            adopted.VerifiedTransition != candidate.VerifiedTransition)
        {
            throw new InvalidDataException("Snapshots have incompatible evaluation time, policy or slot.");
        }

        var sameCopy = adopted.StateCopyHash is null && candidate.StateCopyHash is null
            ? before.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(after.Keys)
            : string.Equals(adopted.StateCopyHash, candidate.StateCopyHash, StringComparison.OrdinalIgnoreCase);
        if (!sameCopy) throw new InvalidDataException("Snapshots have different state-copy identities.");
    }
}
