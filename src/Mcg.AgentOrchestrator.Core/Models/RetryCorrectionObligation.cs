using System.Text;

namespace Mcg.AgentOrchestrator.Core;

public enum RetryCorrectionObligationKind { Structural, Semantic }
public enum RetryCorrectionCondition { SourceSizeRatchet }
public enum RetryCorrectionConditionVerdict { Passed, Failed, Unavailable }

// One accepted correction, bound once to the candidate that produced it. Later
// feedback replaces this obligation; candidate drift never implicitly rebinds it.
public sealed record RetryCorrectionObligation(
    RetryCorrectionObligationKind Kind,
    string CandidateSha,
    RetryCorrectionCondition? Condition,
    string FeedbackSha256)
{
    public static RetryCorrectionObligation ForSourceSizeRatchet(string candidateSha) =>
        new(RetryCorrectionObligationKind.Structural, candidateSha,
            RetryCorrectionCondition.SourceSizeRatchet, string.Empty);

    public string ConditionIdentity => Condition switch
    {
        RetryCorrectionCondition.SourceSizeRatchet => "source-size-ratchet",
        null => "none",
        _ => throw new InvalidOperationException("Unknown retry correction condition.")
    };

    public bool AppliesToSizeRatchet(string? candidateSha) =>
        Kind == RetryCorrectionObligationKind.Structural &&
        Condition == RetryCorrectionCondition.SourceSizeRatchet &&
        !string.IsNullOrWhiteSpace(candidateSha) &&
        string.Equals(CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase);

    public static string NormalizeFeedback(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();

    public static string HashFeedback(string value) =>
        WorkerContextArtifact.Hash(Encoding.UTF8.GetBytes(NormalizeFeedback(value)));
}
