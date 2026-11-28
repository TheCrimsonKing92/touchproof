using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Source discovery is deliberately conservative: unresolved test/trait attributes, inheritance,
// conditional source or syntax errors cannot authorize removing a requested class.
internal sealed class FocusedEvidenceTraitExclusionDiscovery
{
    private readonly IReadOnlyList<Declaration> _declarations;
    private readonly IReadOnlyList<FocusedEvidenceUsingDirectiveFact> _globalUsings;

    private FocusedEvidenceTraitExclusionDiscovery(IReadOnlyList<Declaration> declarations,
        IReadOnlyList<FocusedEvidenceUsingDirectiveFact> globalUsings)
    {
        _declarations = declarations;
        _globalUsings = globalUsings;
    }

    private sealed record Declaration(FocusedEvidenceDeclarationFacts.DeclaredType Type, FocusedEvidenceDeclarationFacts File);

    internal static FocusedEvidenceTraitExclusionDiscovery? Read(string worktreePath, string project)
    {
        try
        {
            var store = FocusedEvidenceDeclarationFactStore.Current;
            var files = FocusedEvidenceRequestResolver.EnumerateFocusedEvidenceSourceFiles(worktreePath, project)
                .Select(path => store.Read(path, worktreePath)).ToArray();
            // Unrelated conditional files need not defeat the proof; inspect selected declarations below.
            if (files.Any(file => file.ContainsDiagnostics)) return null;
            return new(files.SelectMany(file => file.Types.Select(type => new Declaration(type, file))).ToArray(),
                files.SelectMany(file => file.Usings).Where(item => item.IsGlobal).ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal bool HasMatchingDeclaration(string selector) => MatchingDeclarations(selector).Any();

    private IEnumerable<Declaration> MatchingDeclarations(string selector) =>
        _declarations.Where(declaration => declaration.Type.QualifiedName.Contains(selector, StringComparison.OrdinalIgnoreCase));

    internal IReadOnlyList<string>? ProveExcluded(string selector, IReadOnlyList<FocusedEvidenceFilterToken> tokens,
        IReadOnlyList<(string Key, string Value)> exclusions)
    {
        var matched = MatchingDeclarations(selector).GroupBy(declaration => declaration.Type.QualifiedName).ToArray();
        if (matched.Length == 0) return null;
        var reasons = new SortedSet<string>(StringComparer.Ordinal);
        var discovered = 0;
        foreach (var type in matched)
        {
            var className = type.Key;
            if (tokens.Any(token => token.Kind == FocusedEvidenceTokenKind.ExcludedClass &&
                className.Contains(token.Value, StringComparison.OrdinalIgnoreCase))) continue;
            if (type.Any(part => part.Type.HasBaseList || part.Type.Shape.HasTypeParameters ||
                part.Type.Shape.IsAbstract || part.Type.Shape.IsStructOrInterface || part.Type.Shape.IsNested ||
                part.File.ContainsDirectives)) return null;
            var classTraits = new List<(string Key, string Value)>();
            foreach (var part in type)
                if (!ReadAttributes(part.Type.Shape.Attributes, part.File, classTraits, out _)) return null;
            foreach (var (file, method) in type.SelectMany(part => part.Type.Shape.Methods.Select(method => (part.File, method))))
            {
                var traits = new List<(string Key, string Value)>(classTraits);
                if (!ReadAttributes(method.Attributes, file, traits, out var isTest)) return null;
                if (!isTest) continue;
                discovered++;
                var removing = exclusions.Where(exclusion => traits.Any(trait =>
                    trait.Key.Equals(exclusion.Key, StringComparison.Ordinal) &&
                    trait.Value.Equals(exclusion.Value, StringComparison.Ordinal))).ToArray();
                if (removing.Length == 0) return null;
                foreach (var exclusion in removing)
                    reasons.Add($"excluded-by-trait:{exclusion.Key}={exclusion.Value}");
            }
        }
        return discovered > 0 ? reasons.ToArray() : null;
    }

    private bool ReadAttributes(IEnumerable<FocusedEvidenceAttributeUsageFact> attributes,
        FocusedEvidenceDeclarationFacts file, List<(string Key, string Value)> traits, out bool isTest)
    {
        isTest = false;
        foreach (var attribute in attributes)
        {
            if (IsXunit(attribute.Name, "Trait", file))
            {
                if (attribute is not { HasTwoStringLiteralArguments: true, FirstArgument: { } key, SecondArgument: { } value })
                    return false;
                traits.Add((key, value));
            }
            else if (IsTestAttribute(attribute.Name, file, new HashSet<string>(StringComparer.Ordinal)))
                isTest = true;
            else if (!new[] { "InlineData", "MemberData", "ClassData", "Collection" }
                .Any(name => IsXunit(attribute.Name, name, file)))
                return false;
        }
        return true;
    }

    private bool IsTestAttribute(string name, FocusedEvidenceDeclarationFacts file, HashSet<string> seen)
    {
        if (HasUsingAlias(file)) return false;
        if (IsXunit(name, "Fact", file) || IsXunit(name, "Theory", file)) return true;
        var normalized = NormalizeAttributeName(name);
        if (!seen.Add(normalized)) return false;
        var candidates = _declarations.Where(declaration => NormalizeAttributeName(declaration.Type.QualifiedName) == normalized ||
            NormalizeAttributeName(declaration.Type.SimpleName) == normalized).ToArray();
        return candidates.Length == 1 && candidates[0].Type.Shape.BaseTypeNames is { Length: 1 } bases &&
            !candidates[0].File.ContainsDirectives && IsTestAttribute(bases[0], candidates[0].File, seen);
    }

    private bool IsXunit(string name, string expected, FocusedEvidenceDeclarationFacts file)
    {
        if (HasUsingAlias(file)) return false;
        var normalized = NormalizeAttributeName(name.Replace("global::", string.Empty, StringComparison.Ordinal));
        if (_declarations.Any(declaration => NormalizeAttributeName(declaration.Type.QualifiedName) == "Xunit." + expected))
            return false;
        if (normalized == "Xunit." + expected) return true;
        if (normalized != expected) return false;
        // A local lookalike or alias is not proof of an xUnit attribute.
        if (_declarations.Any(declaration => NormalizeAttributeName(declaration.Type.SimpleName) == expected)) return false;
        return file.Usings.Any(item => item.Name == "Xunit") || _globalUsings.Any(item => item.Name == "Xunit");
    }

    private bool HasUsingAlias(FocusedEvidenceDeclarationFacts file) =>
        file.Usings.Any(item => item.HasAlias) || _globalUsings.Any(item => item.HasAlias);

    private static string NormalizeAttributeName(string name) => name.EndsWith("Attribute", StringComparison.Ordinal)
        ? name[..^9] : name;
}
