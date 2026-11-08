using Mcg.AgentOrchestrator.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Provenance = Mcg.AgentOrchestrator.App.Orchestration.PreTesterChangedFileGuardClasses.Provenance;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Topology guards declare membership on their classes, independently of symbol impact bounds.
internal static class PreTesterLiveTreeStructuralSelections
{
    internal static (IReadOnlyList<FindingEvidenceSelection> Selections, IReadOnlyList<Provenance> Provenance) Select(
        string worktreePath, IReadOnlyList<string> changedPaths, IReadOnlyList<string>? structuralPaths,
        Action<string>? progress)
    {
        var changed = changedPaths.Select(PreTesterChangedFileGuardClasses.Normalize).ToArray();
        var triggers = changed.Where(IsProjectInput).Concat(
            structuralPaths is null ? changed.Where(IsTreePath) :
                structuralPaths.Select(PreTesterChangedFileGuardClasses.Normalize).Where(IsTreePath))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        if (triggers.Length == 0) return ([], []);
        if (structuralPaths is null && changed.Any(IsTreePath))
            (progress ?? Console.WriteLine)(
                $"PRE_TESTER_GUARD_SELECTED class=none kind=live-tree-structural-unavailable reason=topology-unavailable path={triggers[0]}");
        var selections = DiscoverMarkedClasses(worktreePath);
        return (selections, selections.SelectMany(selection => triggers.Select(path =>
            new Provenance(selection.TestClass, "live-tree-structural", path))).ToArray());
    }

    internal static bool IsTreePath(string path)
    {
        path = PreTesterChangedFileGuardClasses.Normalize(path);
        return path.StartsWith("src/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("tests/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProjectInput(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".csproj" or ".sln" or ".slnx" ||
        Path.GetFileName(path).StartsWith("Directory.Build.", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("config/acceptance-manifest.json", StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyList<FindingEvidenceSelection> DiscoverMarkedClasses(string worktreePath)
    {
        var root = Path.Combine(worktreePath, "tests");
        if (!Directory.Exists(root)) return [];
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in DeveloperDeferredTestSelections.EnumerateSourceFiles(root))
        {
            var text = File.ReadAllText(path);
            if (!text.Contains("LiveTreeStructural", StringComparison.Ordinal)) continue;
            var syntax = CSharpSyntaxTree.ParseText(text);
            if (syntax.GetDiagnostics().Any(item => item.Severity == DiagnosticSeverity.Error))
                throw new InvalidDataException($"Cannot discover live-tree structural attributes in '{path}'.");
            foreach (var declaration in syntax.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (declaration.AttributeLists.SelectMany(list => list.Attributes).Any(attribute =>
                    attribute.Name switch
                    {
                        IdentifierNameSyntax name => IsMarker(name.Identifier.ValueText),
                        QualifiedNameSyntax name => IsMarker(name.Right.Identifier.ValueText),
                        AliasQualifiedNameSyntax name => IsMarker(name.Name.Identifier.ValueText),
                        _ => false
                    }))
                    names.Add(declaration.Identifier.ValueText);
            }
        }
        var resolved = DeveloperDeferredTestSelections.ResolveNames(worktreePath, names.Order().ToArray(),
            requireUniqueSourceFile: true);
        if (resolved.NotRun.Count != 0)
            throw new InvalidDataException("Cannot resolve live-tree structural classes: " + string.Join(",", resolved.NotRun));
        return resolved.Selections;
    }

    private static bool IsMarker(string name) => name is "LiveTreeStructural" or "LiveTreeStructuralAttribute";
}
