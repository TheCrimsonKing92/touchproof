using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Core;

/// <summary>A complete, immutable acceptance verdict for exactly one repository candidate tuple.</summary>
public sealed record MultiRepositoryAcceptanceReceipt
{
    [JsonConstructor]
    public MultiRepositoryAcceptanceReceipt(GoalId goalId, string tupleCanonical,
        IReadOnlyList<RepositoryAcceptanceOutcome> outcomes, DateTimeOffset recordedAt,
        IReadOnlyDictionary<string, string>? integrationBases = null)
    {
        ArgumentNullException.ThrowIfNull(goalId);
        ArgumentNullException.ThrowIfNull(outcomes);
        if (outcomes.Any(outcome => outcome is null)) throw new ArgumentException("Null repository outcome.", nameof(outcomes));
        if (!RepositoryCandidateTuple.TryParse(tupleCanonical, out var tuple))
            throw new ArgumentException("A canonical repository tuple is required.", nameof(tupleCanonical));
        var snapshot = outcomes.OrderBy(outcome => outcome.Key, StringComparer.Ordinal).ToArray();
        if (!tuple!.Entries.Select(entry => entry.Key).SequenceEqual(snapshot.Select(outcome => outcome.Key), StringComparer.Ordinal)
            || snapshot.Any(outcome => string.IsNullOrWhiteSpace(outcome.EvidenceReference)))
            throw new ArgumentException("Exactly one evidence-backed outcome per repository is required.", nameof(outcomes));
        GoalId = goalId;
        TupleCanonical = tupleCanonical;
        Outcomes = Array.AsReadOnly(snapshot);
        RecordedAt = recordedAt;
        var bases = integrationBases ?? new Dictionary<string, string>();
        if (bases.Any(pair => !tuple.Entries.Any(entry => entry.Key == pair.Key)
            || pair.Value is null || pair.Value.Length != 40 || !pair.Value.All(Uri.IsHexDigit)))
            throw new ArgumentException("Integration bases must be repository-keyed commit identities.", nameof(integrationBases));
        IntegrationBases = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            bases.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }

    public GoalId GoalId { get; }
    public string TupleCanonical { get; }
    public IReadOnlyList<RepositoryAcceptanceOutcome> Outcomes { get; }
    public DateTimeOffset RecordedAt { get; }
    public IReadOnlyDictionary<string, string> IntegrationBases { get; }
    [JsonIgnore] public bool Passed => Outcomes.All(outcome => outcome.Passed);
    [JsonIgnore] public IReadOnlyList<string> FailedKeys => Outcomes.Where(outcome => !outcome.Passed).Select(outcome => outcome.Key).ToArray();

    public bool IsComplete(GoalRepositorySet repositories) =>
        repositories.Keys.SequenceEqual(Outcomes.Select(outcome => outcome.Key), StringComparer.Ordinal);

    public bool IsCurrentFor(RepositoryCandidateTuple tuple) => string.Equals(TupleCanonical, tuple.Canonical, StringComparison.Ordinal);
}
