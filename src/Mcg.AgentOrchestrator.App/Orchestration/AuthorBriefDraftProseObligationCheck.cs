using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AuthorBriefDraftProseObligationCheck
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly Regex Heading = new(@"^(#{1,2})[ \t]+(.+)$", Options);
    private static readonly Regex SentenceBoundary = new(@"(?<=[.!?])\s+", Options);
    private static readonly Regex Tester = new(@"\bTester\b", Options);
    private static readonly Regex EvidenceRequest = new(@"\bevidence[ _-]+request\b", Options);
    private static readonly Regex Execution = new(@"\b(?:runs?|executes?)\b", Options);
    private static readonly Regex Ownership = new(
        @"\b(?:integration|evidence)[ \t-]+owners?\b|\bowns[ \t]+(?:the[ \t]+)?(?:integration|evidence)\b", Options);
    private static readonly Regex RequiredGate = new(
        @"\b(?:normal|broad)(?:[ \t-]+(?:normal|broad))?[ \t-]+gate\b.*?\b(?:remains?|stays?|still)[ \t]+required\b", Options);
    private static readonly Regex AfterThisLands = new(@"\bafter[ \t]+this[ \t]+lands\b", Options);
    private static readonly Regex Operator = new(@"\boperator\b", Options);

    internal static AuthorBriefDraftCheck Run(string markdown)
    {
        var matches = new List<string>();
        var inAcceptanceCriteria = false;
        foreach (var (line, _) in AuthorBriefDraftChecks.UnfencedLines(markdown))
        {
            var heading = Heading.Match(line);
            if (heading.Success)
                inAcceptanceCriteria = heading.Groups[1].Value == "##" &&
                    heading.Groups[2].Value.Trim().StartsWith("Acceptance criteria", StringComparison.OrdinalIgnoreCase);
            if (inAcceptanceCriteria && BriefLint.NumberedLine().IsMatch(line)) continue;

            foreach (var sentence in SentenceBoundary.Split(line).Select(text => text.Trim()))
            {
                var shapes = new List<string>();
                if (Tester.IsMatch(sentence) && EvidenceRequest.IsMatch(sentence) && Execution.IsMatch(sentence))
                    shapes.Add("Tester evidence_request");
                if (Ownership.IsMatch(sentence)) shapes.Add("integration/evidence owner");
                if (RequiredGate.IsMatch(sentence)) shapes.Add("normal/broad gate remains required");
                if ((BriefLint.PostLandingPhrase().IsMatch(sentence) || AfterThisLands.IsMatch(sentence)) &&
                    Operator.IsMatch(sentence)) shapes.Add("after-landing operator");
                if (shapes.Count > 0) matches.Add($"{string.Join(", ", shapes)}: \"{sentence}\"");
            }
        }

        return new("prose-obligations", matches.Count == 0, matches.Count == 0
            ? "No owner, gate or after-landing sentence was found outside the numbered acceptance criteria."
            : string.Join(" | ", matches) +
              ". Remove each quoted sentence or move its content into a numbered criterion ending with an owner sentence.");
    }
}
