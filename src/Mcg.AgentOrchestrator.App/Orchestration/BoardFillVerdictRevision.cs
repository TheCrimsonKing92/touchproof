namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record BoardFillVerdictRevisionRequest(string BacklogItemId, string MainHead, string Prompt);

internal sealed record BoardFillVerdictRevisionResult(string Kind, int ExitCode, string? Markdown,
    string? MainHead, IReadOnlyList<AuthorBriefDraftCheck> Checks, string? Detail = null);

// One additional Author attempt repairs evidence/preflight failures after structural drafting.
internal static class BoardFillVerdictRevision
{
    internal static bool ShouldRevise(BoardFillPremiseVerification verification,
        IReadOnlyList<AuthorBriefDraftCheck> preflight) => verification.Status == "complete" &&
        (verification.Verdicts.Any(verdict => verdict.Verdict is "contradicted" or "unverifiable") ||
         preflight.Any(check => !check.Passed));

    internal static bool Accept(BoardFillVerdictRevisionRequest request, BoardFillVerdictRevisionResult result) =>
        result.Kind == "draft" && result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Markdown) &&
        !string.IsNullOrWhiteSpace(request.MainHead) &&
        string.Equals(result.MainHead, request.MainHead, StringComparison.Ordinal);

    internal static string BulletText(string premise, int bullet)
    {
        var claims = BoardFillPremiseBullets.SourceClaims(premise);
        return bullet < 1 || bullet > claims.Count ? "bullet text unavailable" : claims[bullet - 1].Text;
    }
}
