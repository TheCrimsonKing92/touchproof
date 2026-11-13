using System.Text.RegularExpressions;

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
        var text = premise.ReplaceLineEndings("\n");
        // Match the verifier's numbering, retaining continuation lines as part of the claim.
        var matches = Regex.Matches(text, @"^[ \t]*[-*][ \t]+\S", RegexOptions.Multiline);
        if (bullet < 1 || bullet > matches.Count) return "bullet text unavailable";
        var end = bullet < matches.Count ? matches[bullet].Index : text.Length;
        return text[matches[bullet - 1].Index..end].TrimEnd();
    }
}
