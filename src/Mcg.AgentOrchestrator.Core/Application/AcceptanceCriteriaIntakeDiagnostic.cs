namespace Mcg.AgentOrchestrator.Core;

public sealed record AcceptanceCriteriaIntakeDiagnostic(int HeadingLineNumber, string HeadingText, string Kind)
{
    public string Message =>
        $"Acceptance criteria {Kind} at line {HeadingLineNumber} ({HeadingText}): " +
        "add numbered or bulleted items directly under the heading, or remove the heading.";

    public static AcceptanceCriteriaIntakeDiagnostic? Evaluate(string objectiveText)
    {
        var section = AcceptanceCriteriaParser.FindDeclaredSection(objectiveText);
        if (section is not { } found || AcceptanceCriteriaParser.ParseDeclared(objectiveText).Count > 0)
            return null;

        var heading = found.Heading.Trim().TrimStart('#').Trim();
        if (heading.EndsWith(':'))
            heading = heading[..^1];
        if (!string.Equals(heading, "Acceptance", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(heading, "Acceptance criteria", StringComparison.OrdinalIgnoreCase))
            return null;

        return new(found.LineNumber, found.Heading,
            found.Body.Any(line => !string.IsNullOrWhiteSpace(line)) ? "malformed-section" : "empty-section");
    }
}
