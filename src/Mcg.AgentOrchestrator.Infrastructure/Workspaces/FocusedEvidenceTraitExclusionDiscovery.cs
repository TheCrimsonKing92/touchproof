using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Source discovery is deliberately conservative: unresolved test/trait attributes, inheritance,
// conditional source or syntax errors cannot authorize removing a requested class.
internal sealed class FocusedEvidenceTraitExclusionDiscovery(
    IReadOnlyList<TypeDeclarationSyntax> declarations, IReadOnlyList<UsingDirectiveSyntax> globalUsings)
{
    internal static FocusedEvidenceTraitExclusionDiscovery? Read(string worktreePath, string project)
    {
        try
        {
            var roots = FocusedEvidenceRequestResolver.EnumerateFocusedEvidenceSourceFiles(worktreePath, project)
                .Select(FocusedEvidenceRequestResolver.ParseCSharpRoot).ToArray();
            // Unrelated conditional files need not defeat the proof; inspect selected declarations below.
            if (roots.Any(root => root.ContainsDiagnostics)) return null;
            return new(roots.SelectMany(root => root.DescendantNodes().OfType<TypeDeclarationSyntax>()).ToArray(),
                roots.SelectMany(root => root.Usings).Where(item => item.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)).ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal IReadOnlyList<string>? ProveExcluded(string selector, IReadOnlyList<FocusedEvidenceFilterToken> tokens,
        IReadOnlyList<(string Key, string Value)> exclusions)
    {
        var matched = declarations.Where(type => QualifiedName(type).Contains(selector, StringComparison.OrdinalIgnoreCase))
            .GroupBy(QualifiedName).ToArray();
        if (matched.Length == 0) return null;
        var reasons = new SortedSet<string>(StringComparer.Ordinal);
        var discovered = 0;
        foreach (var type in matched)
        {
            var className = type.Key;
            if (tokens.Any(token => token.Kind == FocusedEvidenceTokenKind.ExcludedClass &&
                className.Contains(token.Value, StringComparison.OrdinalIgnoreCase))) continue;
            if (type.Any(part => part.BaseList is not null || part.TypeParameterList is not null ||
                part.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.AbstractKeyword)) ||
                part is InterfaceDeclarationSyntax or StructDeclarationSyntax ||
                part is RecordDeclarationSyntax record && record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ||
                part.Ancestors().OfType<TypeDeclarationSyntax>().Any() ||
                part.SyntaxTree.GetRoot().ContainsDiagnostics || part.SyntaxTree.GetRoot().ContainsDirectives)) return null;
            var classTraits = new List<(string Key, string Value)>();
            foreach (var part in type)
                if (!ReadAttributes(part.AttributeLists.SelectMany(list => list.Attributes), classTraits, out _)) return null;
            foreach (var method in type.SelectMany(part => part.Members.OfType<MethodDeclarationSyntax>()))
            {
                var traits = new List<(string Key, string Value)>(classTraits);
                if (!ReadAttributes(method.AttributeLists.SelectMany(list => list.Attributes), traits, out var isTest)) return null;
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

    private bool ReadAttributes(IEnumerable<AttributeSyntax> attributes, List<(string Key, string Value)> traits,
        out bool isTest)
    {
        isTest = false;
        foreach (var attribute in attributes)
        {
            if (IsXunit(attribute.Name.ToString(), "Trait", attribute))
            {
                if (attribute.ArgumentList?.Arguments is not { Count: 2 } args ||
                    args.Any(arg => arg.NameColon is not null || arg.NameEquals is not null ||
                        arg.Expression is not LiteralExpressionSyntax literal || !literal.IsKind(SyntaxKind.StringLiteralExpression)))
                    return false;
                traits.Add((((LiteralExpressionSyntax)args[0].Expression).Token.ValueText,
                    ((LiteralExpressionSyntax)args[1].Expression).Token.ValueText));
            }
            else if (IsTestAttribute(attribute.Name.ToString(), attribute, new HashSet<string>(StringComparer.Ordinal)))
                isTest = true;
            else if (!new[] { "InlineData", "MemberData", "ClassData", "Collection" }
                .Any(name => IsXunit(attribute.Name.ToString(), name, attribute)))
                return false;
        }
        return true;
    }

    private bool IsTestAttribute(string name, Microsoft.CodeAnalysis.SyntaxNode context, HashSet<string> seen)
    {
        if (context.SyntaxTree.GetRoot().DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Concat(globalUsings).Any(item => item.Alias is not null)) return false;
        if (IsXunit(name, "Fact", context) || IsXunit(name, "Theory", context)) return true;
        var normalized = NormalizeAttributeName(name);
        if (!seen.Add(normalized)) return false;
        var candidates = declarations.Where(type => NormalizeAttributeName(QualifiedName(type)) == normalized ||
            NormalizeAttributeName(type.Identifier.ValueText) == normalized).ToArray();
        return candidates.Length == 1 && candidates[0].BaseList?.Types is { Count: 1 } bases &&
            !candidates[0].ContainsDiagnostics && !candidates[0].SyntaxTree.GetRoot().ContainsDirectives &&
            IsTestAttribute(bases[0].Type.ToString(), candidates[0], seen);
    }

    private bool IsXunit(string name, string expected, Microsoft.CodeAnalysis.SyntaxNode context)
    {
        var usings = context.SyntaxTree.GetRoot().DescendantNodes().OfType<UsingDirectiveSyntax>().Concat(globalUsings).ToArray();
        if (usings.Any(item => item.Alias is not null)) return false;
        var normalized = NormalizeAttributeName(name.Replace("global::", string.Empty, StringComparison.Ordinal));
        if (declarations.Any(type => NormalizeAttributeName(QualifiedName(type)) == "Xunit." + expected)) return false;
        if (normalized == "Xunit." + expected) return true;
        if (normalized != expected) return false;
        // A local lookalike or alias is not proof of an xUnit attribute.
        if (declarations.Any(type => NormalizeAttributeName(type.Identifier.ValueText) == expected)) return false;
        return usings.Any(item => item.Name?.ToString() == "Xunit");
    }

    private static string NormalizeAttributeName(string name) => name.EndsWith("Attribute", StringComparison.Ordinal)
        ? name[..^9] : name;

    private static string QualifiedName(TypeDeclarationSyntax type) => string.Join('.',
        type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(item => item.Name.ToString())
            .Concat(type.Ancestors().OfType<TypeDeclarationSyntax>().Reverse().Select(item => item.Identifier.ValueText))
            .Append(type.Identifier.ValueText));
}
