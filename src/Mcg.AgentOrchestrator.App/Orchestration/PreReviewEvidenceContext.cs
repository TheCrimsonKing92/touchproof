using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record PreReviewEvidenceContext(
    string? CandidateSha,
    IReadOnlyList<string> SelectedFocusedTests,
    string? FocusedRequest,
    string MappingReason,
    bool NoApplicableTests,
    bool MappingNeedsInput,
    IReadOnlyList<string>? SourceCleanupPaths = null,
    RepositoryTestImpactDegradation? TestImpactDegradation = null,
    RepositoryTestImpactHeadroom? TestImpactHeadroom = null)
{
    public bool RequiresSourceCleanup => SourceCleanupPaths is { Count: > 0 };
}
