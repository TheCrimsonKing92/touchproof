namespace Mcg.AgentOrchestrator.Core;

public enum WorkerWriteIntegrityLabel
{
    Other,
    Unlabeled,
    Low,
    Medium
}

// Query-derived facts about one denied-write target, carried only within the current round.
public sealed record WorkerWriteIntegrityObservation(
    string WorktreeRelativePath,
    WorkerWriteIntegrityLabel ObservedLabel,
    bool LabelQuerySucceeded)
{
    public bool EstablishesAboveLow => LabelQuerySucceeded && ObservedLabel == WorkerWriteIntegrityLabel.Medium;
}
