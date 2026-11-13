using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class SourceReadingGuardTestClasses
{
    internal static ImpactReplaySourceGuardReport? Read(ImpactReplayTestSourceIndex sources,
        IReadOnlyList<AcceptanceTestClassDescriptor> classes)
    {
        if (!sources.Trees.Any(tree => tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Any(type => type.Identifier.ValueText == "RepositorySourceSnapshotCache"))) return null;

        // Bind checkout declarations against core runtime types, without loading or building test assemblies.
        // Candidate symbols are included conservatively when external dependencies prevent overload resolution.
        var compilation = CSharpCompilation.Create("ImpactReplaySources", sources.Trees,
            references: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var edges = new Dictionary<ISymbol, HashSet<ISymbol>>(SymbolEqualityComparer.Default);
        var owners = new Dictionary<string, HashSet<ISymbol>>(StringComparer.Ordinal);
        var readers = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var tree in sources.Trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var declaration in tree.GetRoot().DescendantNodes()
                         .Where(node => node is BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax ||
                             node is VariableDeclaratorSyntax { Parent: { Parent: BaseFieldDeclarationSyntax } }))
            {
                var owner = declaration.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
                var symbol = model.GetDeclaredSymbol(declaration)?.OriginalDefinition;
                if (owner is null || symbol is null) continue;
                var name = ImpactReplayTestSourceIndex.ClassName(owner);
                if (!owners.TryGetValue(name, out var members)) owners[name] = members = new(SymbolEqualityComparer.Default);
                members.Add(symbol);
                if (!edges.TryGetValue(symbol, out var calls)) edges[symbol] = calls = new(SymbolEqualityComparer.Default);
                foreach (var expression in declaration.DescendantNodes().OfType<ExpressionSyntax>()
                             .Where(node => node is InvocationExpressionSyntax or MemberAccessExpressionSyntax or ObjectCreationExpressionSyntax))
                {
                    var info = model.GetSymbolInfo(expression);
                    foreach (var target in info.Symbol is { } resolved ? new[] { resolved } : info.CandidateSymbols.ToArray())
                    {
                        var canonical = target.OriginalDefinition;
                        calls.Add(canonical);
                        if (IsSnapshotRead(canonical)) readers.Add(symbol);
                    }
                }
            }
        }
        bool changed;
        do
        {
            changed = false;
            foreach (var (caller, targets) in edges)
                if (!readers.Contains(caller) && targets.Any(readers.Contains)) changed |= readers.Add(caller);
        } while (changed);
        // A concrete test can inherit its runnable method and initialization without declaring a member itself.
        foreach (var tree in sources.Trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var name = ImpactReplayTestSourceIndex.ClassName(declaration);
                if (!owners.TryGetValue(name, out var members)) owners[name] = members = new(SymbolEqualityComparer.Default);
                var type = model.GetDeclaredSymbol(declaration)?.BaseType;
                var visited = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                while (type is not null && visited.Add(type))
                {
                    foreach (var member in type.GetMembers())
                        if (member is IFieldSymbol { IsStatic: false } or IMethodSymbol { MethodKind: MethodKind.Constructor } ||
                            member.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() is MethodDeclarationSyntax method &&
                                method.AttributeLists.SelectMany(list => list.Attributes).Any(attribute =>
                                    attribute.Name.ToString().Split('.').Last() is "Fact" or "FactAttribute" or "Theory" or "TheoryAttribute")))
                            members.Add(member.OriginalDefinition);
                    type = type.BaseType;
                }
            }
        }
        return new(classes.Where(item => owners.TryGetValue(item.FullName, out var members) && members.Any(readers.Contains))
            .Select(item => item.FullName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    private static bool IsSnapshotRead(ISymbol symbol) =>
        symbol.ContainingType?.Name == "RepositorySourceSnapshotCache" && symbol.Name == "Get" ||
        symbol.ContainingType?.Name == "RepositorySourceSnapshot" &&
        symbol.Name is "EnumerateFiles" or "GetText" or "GetLines" or "GetSyntaxTree";
}
