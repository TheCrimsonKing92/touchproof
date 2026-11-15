using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Mcg.SemanticImpactSpike;

public sealed record ReferenceSite(string Target, string Caller, string Path, int Line, string Kind);
public sealed record DelegateSupply(string Target, string Parameter, string Caller, string ReachedType);

// Nodes are semantic members/types, never spelling-only names. Edges mean "caller depends on target".
public sealed class ReverseReferenceIndex
{
    public Dictionary<string, ISymbol> Symbols { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, HashSet<string>> Reverse { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, HashSet<string>> FrameworkReverse { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, HashSet<string>> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, HashSet<string>> TestsByMethod { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string[]> TestMethods { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, HashSet<string>> AssemblyClosure { get; } = new(StringComparer.Ordinal);
    public List<ReferenceSite> Sites { get; } = [];
    public List<DelegateSupply> DelegateSupplies { get; } = [];
    public Dictionary<string, HashSet<string>> Implementations { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, HashSet<string>> ImplementingTypes { get; } = new(StringComparer.Ordinal);
    public double BuildSeconds { get; private set; }
    public int IgnoredNameofReferences { get; private set; }
    public int FrameworkDispatchEdges { get; private set; }
    public int TestClassCount => TestMethods.Count;

    private string? Register(ISymbol? symbol)
    {
        var key = SymbolIdentity.Key(symbol);
        if (key is not null) Symbols.TryAdd(key, SymbolIdentity.Normalize(symbol!));
        return key;
    }

    private static void Add(Dictionary<string, HashSet<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var values)) map[key] = values = new(StringComparer.Ordinal);
        values.Add(value);
    }

    private void Edge(ISymbol? target, ISymbol? caller)
    {
        var to = Register(target);
        var from = Register(caller);
        if (to is not null && from is not null && to != from) Add(Reverse, to, from);
    }

    public void Build(SolutionWorkspaceLoader loader, string repositoryRoot)
    {
        var timer = Stopwatch.StartNew();
        var types = new List<INamedTypeSymbol>();
        foreach (var (project, compilation) in loader.Compilations)
        {
            var closure = loader.Solution!.GetProjectDependencyGraph().GetProjectsThatThisProjectTransitivelyDependsOn(project.Id)
                .Select(id => loader.Solution.GetProject(id)!.AssemblyName!).ToHashSet(StringComparer.Ordinal);
            closure.Add(compilation.AssemblyName!);
            AssemblyClosure[compilation.AssemblyName!] = closure;
            foreach (var tree in compilation.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot();
                var path = Path.GetRelativePath(repositoryRoot, tree.FilePath).Replace('\\', '/');
                foreach (var declaration in root.DescendantNodes().Where(n => n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax
                    or BaseMethodDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax or VariableDeclaratorSyntax))
                {
                    var symbol = model.GetDeclaredSymbol(declaration);
                    if (symbol is not (INamedTypeSymbol or IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol)) continue;
                    var key = Register(symbol);
                    if (key is not null) Add(Files, path, key);
                    if (symbol is INamedTypeSymbol type) types.Add(type);
                }
                foreach (var node in root.DescendantNodes().Where(n => n is SimpleNameSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax))
                {
                    // Documentation is structured trivia; nameof has no runtime dependency.
                    if (node.Ancestors().OfType<InvocationExpressionSyntax>().Any(i => i.Expression is IdentifierNameSyntax name && name.Identifier.ValueText == "nameof"))
                    {
                        IgnoredNameofReferences++;
                        continue;
                    }
                    var target = model.GetSymbolInfo(node).Symbol;
                    if (target is not (INamedTypeSymbol or IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol)) continue;
                    var caller = EnclosingMember(model.GetEnclosingSymbol(node.SpanStart));
                    if (caller is null) continue;
                    Edge(target, caller);
                    if (target.ContainingType is not null) Edge(target.ContainingType, caller);
                }
                foreach (var node in root.DescendantNodes().Where(n => n is InvocationExpressionSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax))
                {
                    if (model.GetSymbolInfo(node).Symbol is not IMethodSymbol target) continue;
                    var key = Register(target);
                    var caller = Register(EnclosingMember(model.GetEnclosingSymbol(node.SpanStart)));
                    if (key is not null && caller is not null)
                    {
                        Sites.Add(new(key, caller, path, tree.GetLineSpan(node.Span).StartLinePosition.Line + 1,
                            node is InvocationExpressionSyntax ? "call" : "construction"));
                        if (node is InvocationExpressionSyntax && target.Parameters.Any(p => p.Type.TypeKind == TypeKind.Delegate)
                            && model.GetOperation(node) is IInvocationOperation operation)
                            foreach (var argument in operation.Arguments.Where(a => !a.IsImplicit && a.Parameter?.Type.TypeKind == TypeKind.Delegate))
                            {
                                var invoke = ((INamedTypeSymbol)argument.Parameter!.Type).DelegateInvokeMethod;
                                if (invoke?.ReturnType is INamedTypeSymbol returned)
                                    DelegateSupplies.Add(new(key, argument.Parameter.Name, caller, "T:" + returned.ToDisplayString()));
                            }
                    }
                }
            }
        }
        foreach (var type in types.Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default))
        {
            if (!type.IsAbstract && type.TypeKind == TypeKind.Class)
            {
                var methods = TestClassCatalog.Methods(type).ToArray();
                if (methods.Length > 0)
                {
                    var testKey = Register(type)!;
                    TestMethods[testKey] = methods.Select(m => m.GetDocumentationCommentId()!).Order(StringComparer.Ordinal).ToArray();
                    foreach (var method in methods) Add(TestsByMethod, Register(method)!, testKey);
                }
            }
            foreach (var iface in type.AllInterfaces)
            {
                Add(ImplementingTypes, Register(iface)!, Register(type)!);
                foreach (var member in iface.GetMembers())
                {
                    var impl = type.FindImplementationForInterfaceMember(member);
                    if (impl is null) continue;
                    var memberKey = Register(member)!;
                    var implKey = Register(impl)!;
                    Add(Implementations, memberKey, implKey);
                    var sourceInterface = member.Locations.Any(l => l.IsInSource) || Symbols[memberKey].Locations.Any(l => l.IsInSource);
                    // Interface symbols viewed through a CompilationReference may be metadata-like.
                    sourceInterface |= loader.Compilations.Any(c => c.Compilation.AssemblyName == member.ContainingAssembly.Name);
                    if (sourceInterface) Edge(impl, member);
                    else if (implKey != memberKey) { Add(FrameworkReverse, implKey, memberKey); FrameworkDispatchEdges++; }
                }
            }
            foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
                if (method.OverriddenMethod is { } parent)
                {
                    if (loader.Compilations.Any(c => c.Compilation.AssemblyName == parent.ContainingAssembly.Name)) Edge(method, parent);
                    else { Add(FrameworkReverse, Register(method)!, Register(parent)!); FrameworkDispatchEdges++; }
                }
        }
        BuildSeconds = timer.Elapsed.TotalSeconds;
    }

    private static ISymbol? EnclosingMember(ISymbol? symbol)
    {
        while (symbol is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction }
            or IParameterSymbol or ILocalSymbol) symbol = symbol.ContainingSymbol;
        return symbol is IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol or INamedTypeSymbol ? symbol : null;
    }

    public string[] Affected(IEnumerable<string> targets, bool frameworkDispatch = false)
    {
        var roots = targets.ToArray();
        var targetAssemblies = roots.Select(t => t.Split('|')[0]).ToHashSet(StringComparer.Ordinal);
        var visited = roots.ToHashSet(StringComparer.Ordinal);
        var pending = new Queue<string>(visited);
        var tests = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryDequeue(out var key))
        {
            if (TestsByMethod.TryGetValue(key, out var found))
                foreach (var test in found)
                    if (AssemblyClosure[Symbols[test].ContainingAssembly.Name].IsSupersetOf(targetAssemblies))
                        tests.Add(SymbolIdentity.TypeName((INamedTypeSymbol)Symbols[test]));
            if (Reverse.TryGetValue(key, out var next))
                foreach (var item in next) if (visited.Add(item)) pending.Enqueue(item);
            if (frameworkDispatch && FrameworkReverse.TryGetValue(key, out var framework))
                foreach (var item in framework) if (visited.Add(item)) pending.Enqueue(item);
        }
        return tests.Order(StringComparer.Ordinal).ToArray();
    }
}
