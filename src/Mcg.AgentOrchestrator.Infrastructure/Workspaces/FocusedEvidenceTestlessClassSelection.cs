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
            var store = FocusedEvidenceDeclarationFactStore.Current;
            var files = FocusedEvidenceRequestResolver.EnumerateFocusedEvidenceSourceFiles(worktreePath, project)
                .Select(path =>
                {
                    var facts = store.Read(path, worktreePath, out var source);
                    return (Facts: facts, DirectiveText: facts.ContainsDirectives ? source : null);
                }).ToArray();
            if (files.Length == 0 || files.Any(file => file.Facts.ContainsDiagnostics)) return null;
            var declarations = files.SelectMany(file => file.Facts.Types
                .Select(type => (Type: type, file.Facts.ContainsDirectives))).ToArray();
            var tests = declarations.SelectMany(declaration => declaration.Type.TestMethodNames
                .Select(name => declaration.Type.QualifiedName + "." + name)).ToArray();
            // With no recognised tests, this may be another framework or custom-attribute-only project.
            return tests.Length == 0 ? null : new(
                files.Select(file => file.DirectiveText).OfType<string>().ToArray(), declarations, tests);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record Discovery(IReadOnlyList<string> DirectiveTexts,
        IReadOnlyList<(FocusedEvidenceDeclarationFacts.DeclaredType Type, bool ContainsDirectives)> Declarations,
        IReadOnlyList<string> TestNames)
    {
        internal bool ProvesTestless(string selector)
        {
            // A substring-only match does not prove the named class is testless. Keep the
            // original selector so downstream guards can still inspect its merge-base identity.
            if (!Declarations.Any(declaration =>
                FocusedEvidenceParsedRootCache.FocusedEvidenceTypeMatches(declaration.Type.Name, selector))) return false;
            if (TestNames.Any(name => name.Contains(selector, StringComparison.OrdinalIgnoreCase))) return false;
            // Disabled conditional text may hide another partial part or a substring match.
            var segments = selector.Split('.');
            if (DirectiveTexts.Any(text => segments.All(segment =>
                text.Contains(segment, StringComparison.OrdinalIgnoreCase)))) return false;
            var matches = Declarations.Where(declaration => declaration.Type.QualifiedName
                .Contains(selector, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0) return false;
            return matches.All(declaration => !declaration.Type.HasBaseList && !declaration.ContainsDirectives &&
                !declaration.Type.MethodAttributeNames.Any(CouldDeclareTest));
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
}
