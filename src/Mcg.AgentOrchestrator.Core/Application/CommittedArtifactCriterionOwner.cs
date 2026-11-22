using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

internal static partial class CommittedArtifactCriterionOwner
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
    private const string ArtifactModifiers = """
        (?:
            (?!\b(?:a|an|the|and|or|that|which|whether|of|from|for|in|on|to|by|with|without|using|about|no|not)\b)
            [\p{L}\p{N}]+(?:-[\p{L}\p{N}]+)*[ \t]+
        )*
        """;

    private const string QualifiedArtifact = ArtifactModifiers + Artifact;
    private const string ArtifactArticle = @"(?:an?|(?:at\s+least\s+)?one)\s+";
    private const string CommittedQualifier = @"(?:committed|checked-in)\s+";
    private const string ArtifactProofVerb =
        @"(?:(?:assert|cover|check|exercise|demonstrate|confirm|capture|document|contain)s?|verif(?:y|ies))\b";

    // Adding tests already in the repository to an evidence run only changes the run's selection.
    private const string ProductionQualifiedArtifact =
        "(?!" + ArtifactModifiers + @"\b(?:existing|(?:already|previously)\s+(?:committed|checked-in))\s+)" +
        ArtifactModifiers + @"\b(?:new|committed|checked-in)\s+" + QualifiedArtifact;

    internal static IReadOnlyList<string> RewriteAll(IReadOnlyList<string> criteria) =>
        criteria.Select(criterion => Rewrite(criterion, out _)).ToArray();

    internal static string Rewrite(string criterion, out string? originalOwner)
    {
        var declaration = ArtifactOwnerDeclaration().Match(criterion);
        if (!declaration.Success || !ArtifactProductionRequirement().IsMatch(criterion[..declaration.Index]))
        {
            originalOwner = null;
            return criterion;
        }

        var role = declaration.Groups["role"];
        originalOwner = role.Value.Equals("Tester", StringComparison.OrdinalIgnoreCase) ? "Tester" : "Reviewer";
        return criterion[..role.Index] + "Developer" + criterion[(role.Index + role.Length)..];
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
        (?:^|[.!?;]\s+|\b(?:and|then)\s+|,\s+(?=(?:an?|the|at|one|asserted|verified|covered|checked|exercised|demonstrated|confirmed)\b))\s*
        (?:
            (?: (?:asserted|verified|covered|checked|exercised|demonstrated|confirmed)\s+by\s+ )?
            (?:
                (?:the\s+|
        """ + ArtifactArticle + """
                )?new\s+
        """ + QualifiedArtifact + """
              |
        """ + ArtifactArticle + CommittedQualifier + QualifiedArtifact + """
              | (?:the\s+)?
        """ + CommittedQualifier + QualifiedArtifact + """
                \s+
        """ + ArtifactProofVerb + """
            )
          | (?:(?:the\s+|this\s+)?(?:developer|candidate|change|implementation|patch|solution)\s+(?:(?:must|will|shall)\s+)?)?
            (?!
                adds?\s+(?:(?:an?|the)\s+)?
                (?!
        """ + ProductionQualifiedArtifact + """
                )
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
