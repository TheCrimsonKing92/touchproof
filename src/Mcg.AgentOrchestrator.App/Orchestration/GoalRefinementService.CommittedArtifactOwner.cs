using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class GoalRefinementService
{
    private const string QuotedArtifactPath = @"`[^`\r\n]+\.[a-z0-9]+`";
    private const string Artifact = """
        (?:
        """ + QuotedArtifactPath + """
            (?:\s+(?:file|script|document|fixture|test)\b)?
          | \b(?:(?:unit|integration|end-to-end|regression|negative-control|source|configuration|test)\s+)*
            (?:tests?|files?|fixtures?|documents?|scripts?|reports?|artifacts?|records?|receipts?|snapshots?)\b
        )
        """;

    // Explicit production verbs allow descriptive modifiers within the artifact noun phrase.
    // Stop at clause and preposition boundaries, such as "a run of existing tests".
    private const string QualifiedArtifact = """
        (?:
            (?!\b(?:a|an|the|and|or|that|which|whether|of|from|for|in|on|to|by|with|without|using|about|no|not)\b)
            [\p{L}\p{N}]+(?:-[\p{L}\p{N}]+)*[ \t]+
        )*
        """ + Artifact;

    private static Dictionary<string, string> RewriteCommittedArtifactOwners(
        List<string> criteria, List<string> diagnostics)
    {
        var rewrites = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < criteria.Count; index++)
        {
            var criterion = criteria[index];
            var declaration = ArtifactOwnerDeclaration().Match(criterion);
            if (!declaration.Success || !ArtifactProductionRequirement().IsMatch(criterion[..declaration.Index]))
                continue;

            var role = declaration.Groups["role"];
            var rewritten = criterion[..role.Index] + "Developer" + criterion[(role.Index + role.Length)..];
            criteria[index] = rewritten;
            rewrites.Add(criterion, rewritten);
            var originalOwner = role.Value.Equals("Tester", StringComparison.OrdinalIgnoreCase) ? "Tester" : "Reviewer";
            diagnostics.Add(
                $"Spec refiner owner rewritten: declared criterion {index + 1} requires a committed artifact; owner {originalOwner} -> Developer");
        }

        return rewrites;
    }

    [GeneratedRegex(
        """
        (?:^|(?<=[.!?\r\n]))\s*(?:the\s+)?(?<role>Tester|Reviewer)\s+owns\b[^.!?\r\n]*\.?
        (?:\s+[a-z]+(?:[\s-]+gate)?\s+(?:executes\b|owns\s+execution\b)[^.!?\r\n]*\.)?
        (?:\s+(?:TEST-VERIFIABLE|REAL-WORLD-DEPENDENT)\b[^\r\n]*)?\s*\z
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex ArtifactOwnerDeclaration();

    [GeneratedRegex(
        """
        (?:^|[.!?;]\s+|\b(?:and|then)\s+)\s*
        (?:
            (?:(?:an?|the|(?:at\s+least\s+)?one)\s+)?(?:new|committed|checked-in)\s+
        """ + Artifact + """
          | (?:(?:the\s+|this\s+)?(?:developer|candidate|change|implementation|patch|solution)\s+(?:(?:must|will|shall)\s+)?)?
            (?!
                adds?\s+(?:(?:an?|the)\s+)?(?!(?:new|committed|checked-in)\s+)
        """ + QualifiedArtifact + """
                \s+to\s+(?:(?:an?|the)\s+)?(?:focused\s+)?(?:evidence|test)\s+(?:run|selection)\b
            )
            (?:
                (?:adds?|creates?|writes?|commits?|updates?|modifies?|produces?|ships?|introduces?|extends?|checks?\s+in)\s+
                (?:(?:an?|the)\s+)?(?:(?:new|existing|committed|checked-in)\s+)?
              | (?:includes?|contains?)\s+(?:(?:an?|the)\s+)?(?:new|committed|checked-in)\s+
            )
        """ + QualifiedArtifact + """
          | (?:(?:an?|the)\s+)?(?:(?:new|existing|named|committed|checked-in)\s+)?
        """ + QualifiedArtifact + """
            \s+(?:(?:is|are)|(?:must|shall|will)\s+be)\s+
            (?:added|created|written|committed|updated|modified|produced|shipped|introduced|extended|checked\s+in
              | included\s+in\s+(?:(?:a|the|this)\s+)?commit)\b
        )
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex ArtifactProductionRequirement();
}
