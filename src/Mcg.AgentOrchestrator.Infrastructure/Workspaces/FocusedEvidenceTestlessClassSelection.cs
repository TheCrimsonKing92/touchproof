using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Only positive source proof can remove a class; an empty execution receipt cannot.
internal static class FocusedEvidenceTestlessClassSelection
{
    internal static List<(string Target, string Project, FocusedEvidenceFilter? Filter)> Apply(
        string worktreePath,
        IReadOnlyList<(string Target, string Project, FocusedEvidenceFilter? Filter)> validated,
        IReadOnlyList<FocusedEvidenceDroppedSelection> priorDrops,
        out IReadOnlyList<FocusedEvidenceDroppedSelection> dropped)
    {
        var result = new List<(string Target, string Project, FocusedEvidenceFilter? Filter)>();
        var omissions = new List<FocusedEvidenceDroppedSelection>(priorDrops);
        var discoveries = new Dictionary<string, Discovery?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in validated)
        {
            if (item.Filter is not { } filter ||
                !filter.Tokens.Any(token => token.Kind == FocusedEvidenceTokenKind.Class) ||
                filter.Tokens.Any(token => token.Kind == FocusedEvidenceTokenKind.Method))
            {
                result.Add(item);
                continue;
            }

            if (!discoveries.TryGetValue(item.Project, out var discovery))
                discoveries[item.Project] = discovery = Read(worktreePath, item.Project);
            var retained = new List<FocusedEvidenceFilterToken>();
            foreach (var token in filter.Tokens)
            {
                if (token.Kind == FocusedEvidenceTokenKind.Class && discovery?.ProvesTestless(token.Value) == true)
                    omissions.Add(new(item.Target, item.Project, token.Value, ["declares-no-tests"]));
                else
                    retained.Add(token);
            }
            if (retained.Count == filter.Tokens.Count)
                result.Add(item);
            else if (retained.Any(token => token.Kind == FocusedEvidenceTokenKind.Class))
                result.Add((item.Target, item.Project, filter with { Tokens = retained,
                    CanonicalText = FocusedEvidenceRequestResolver.CanonicalLaunchFilter(retained) }));
        }
        dropped = omissions;
        return result;
    }

    private static Discovery? Read(string worktreePath, string project)
    {
        try
        {
            var roots = FocusedEvidenceRequestResolver.EnumerateFocusedEvidenceSourceFiles(worktreePath, project)
                .Select(FocusedEvidenceRequestResolver.ParseCSharpRoot).ToArray();
            if (roots.Length == 0 || roots.Any(root => root.ContainsDiagnostics)) return null;
            var declarations = roots.SelectMany(root => root.DescendantNodes().OfType<TypeDeclarationSyntax>()).ToArray();
            var tests = declarations.SelectMany(type => type.Members.OfType<MethodDeclarationSyntax>()
                .Where(method => method.AttributeLists.SelectMany(list => list.Attributes)
                    .Any(attribute => FocusedEvidenceMethodResolver.IsTestAttribute(attribute.Name.ToString())))
                .Select(method => QualifiedName(type) + "." + method.Identifier.ValueText)).ToArray();
            // With no recognised tests, this may be another framework or custom-attribute-only project.
            return tests.Length == 0 ? null : new(roots, declarations, tests);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record Discovery(IReadOnlyList<CompilationUnitSyntax> Roots,
        IReadOnlyList<TypeDeclarationSyntax> Declarations, IReadOnlyList<string> TestNames)
    {
        internal bool ProvesTestless(string selector)
        {
            // A substring-only match does not prove the named class is testless. Keep the
            // original selector so downstream guards can still inspect its merge-base identity.
            if (!Declarations.Any(type => FocusedEvidenceRequestResolver.FocusedEvidenceTypeMatches(type, selector))) return false;
            if (TestNames.Any(name => name.Contains(selector, StringComparison.OrdinalIgnoreCase))) return false;
            // Disabled conditional text may hide another partial part or a substring match.
            var segments = selector.Split('.');
            if (Roots.Any(root => root.ContainsDirectives && segments.All(segment =>
                root.ToFullString().Contains(segment, StringComparison.OrdinalIgnoreCase)))) return false;
            var matches = Declarations.Where(type => QualifiedName(type)
                .Contains(selector, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0) return false;
            return matches.All(type => type.BaseList is null &&
                !type.SyntaxTree.GetRoot().ContainsDirectives &&
                !type.Members.OfType<MethodDeclarationSyntax>().SelectMany(method => method.AttributeLists)
                    .SelectMany(list => list.Attributes).Any(attribute => CouldDeclareTest(attribute.Name.ToString())));
        }
    }

    private static bool CouldDeclareTest(string name)
    {
        if (FocusedEvidenceMethodResolver.IsTestAttribute(name)) return true;
        var simpleName = name.Split('.').Last();
        if (simpleName.EndsWith("Attribute", StringComparison.Ordinal)) simpleName = simpleName[..^9];
        // A custom Fact/Theory subclass is uncertainty, without expanding test recognition.
        return simpleName.EndsWith("Fact", StringComparison.Ordinal) || simpleName.EndsWith("Theory", StringComparison.Ordinal);
    }

    private static string QualifiedName(TypeDeclarationSyntax type) => string.Join('.',
        type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(item => item.Name.ToString())
            .Concat(type.Ancestors().OfType<TypeDeclarationSyntax>().Reverse().Select(item => item.Identifier.ValueText))
            .Append(type.Identifier.ValueText));
}
