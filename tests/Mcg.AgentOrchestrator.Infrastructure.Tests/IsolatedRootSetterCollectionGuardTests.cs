using System.Reflection;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

// Read-only source inspection: safe to run alongside every collection it inspects.
public sealed class IsolatedRootSetterCollectionGuardTests
{
    [Fact]
    public void Direct_root_setters_are_reached_only_by_serial_collections()
    {
        var repository = VerifiedRepositoryRoot.Find();
        var snapshot = RepositorySourceSnapshotCache.Get(repository);
        var project = Path.Combine(repository, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        var nestedProjects = Directory.EnumerateFiles(project, "*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName).OfType<string>().Where(path => path != project).ToArray();
        var files = snapshot.EnumerateFiles(project)
            .Where(path => !SourceGuardFileFilter.WorkspaceAndReports.Excludes(Path.GetRelativePath(repository, path)))
            .Where(path => !nestedProjects.Any(nested => Path.GetRelativePath(nested, path) is var relative &&
                relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)));
        var includes = XDocument.Load(Path.Combine(project, "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"))
            .Descendants().Where(element => element.Name.LocalName == "Compile")
            .Select(element => (string?)element.Attribute("Include")).OfType<string>()
            .Select(path => Path.GetFullPath(Path.Combine(project, path.Replace('\\', Path.DirectorySeparatorChar))));
        var trees = files.Concat(includes).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => snapshot.GetSyntaxTree(path)).ToArray();
        var compilation = IsolatedRootSetterCollectionScanner.CreateCompilation(trees);
        var result = IsolatedRootSetterCollectionScanner.Scan(compilation, requireFixtureExemption: true);
        Assert.True(result.SetterCount > 0, "No direct isolated-root setter sites were scanned.");
        Assert.True(result.ExemptedCount > 0, "The exact IsolatedDotnetRootFixture exemption is stale.");
        Assert.NotEmpty(result.Collections);
        Assert.True(result.Violations.Count == 0, string.Join(Environment.NewLine, result.Violations));

        // Source attribution must agree with the actual candidate assembly and xUnit inheritance.
        foreach (var (className, collectionName) in result.Collections)
        {
            var type = typeof(IsolatedRootSetterCollectionGuardTests).Assembly.GetType(className);
            Assert.True(type is not null, $"Attributed test class is absent from the candidate assembly: {className}.");
            var attribute = type!.GetCustomAttributes(inherit: true).OfType<Xunit.v3.ICollectionAttribute>().SingleOrDefault();
            Assert.Equal(collectionName, attribute?.Type is { } definitionType ? "type:" + definitionType.FullName : attribute?.Name);
        }
    }

    [Theory]
    [InlineData("direct", false, 1)]
    [InlineData("direct", true, 0)]
    [InlineData("explicit-false", false, 1)]
    [InlineData("missing", true, 1)]
    [InlineData("undefined", true, 1)]
    [InlineData("duplicate", true, 1)]
    [InlineData("alias", false, 1)]
    [InlineData("alias", true, 0)]
    [InlineData("using-static", false, 1)]
    [InlineData("partial", false, 1)]
    [InlineData("inherited", false, 1)]
    [InlineData("inherited", true, 0)]
    [InlineData("generic-base", false, 1)]
    [InlineData("base-helper", false, 1)]
    [InlineData("base-helper", true, 0)]
    [InlineData("unrelated-dispose", false, 1)]
    [InlineData("unrelated-dispose", true, 0)]
    [InlineData("constructor", false, 1)]
    [InlineData("field", false, 1)]
    [InlineData("method-group", false, 1)]
    [InlineData("interface", false, 1)]
    [InlineData("virtual", false, 1)]
    [InlineData("fixture", false, 1)]
    [InlineData("fixture", true, 0)]
    [InlineData("collection-fixture", false, 1)]
    [InlineData("injected", false, 1)]
    [InlineData("target-typed", false, 1)]
    [InlineData("type-collection", true, 0)]
    [InlineData("generic-collection", true, 0)]
    [InlineData("named-type-collection", true, 0)]
    [InlineData("explicit-dispose", false, 1)]
    [InlineData("static-field", false, 1)]
    [InlineData("helper-base-constructor", false, 1)]
    [InlineData("base-initializer", false, 1)]
    [InlineData("wrapper", false, 0)]
    public void Scanner_requires_serial_collection_for_direct_setter_probes(string shape, bool serial, int violations)
    {
        const string setter = "Environment.SetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, null);";
        var definition = shape == "explicit-false" ? ", DisableParallelization = false" :
            serial ? ", DisableParallelization = true" : "";
        var membership = shape == "missing" ? "" : shape == "undefined" ? "[Collection(\"Unknown\")]" :
            shape is "type-collection" or "named-type-collection" ? "[Collection(typeof(ProbeLane))]" :
            shape == "generic-collection" ? "[Collection<ProbeLane>]" : "[Collection(\"ProbeLane\")]";
        var body = shape switch
        {
            "alias" => "using Env = System.Environment; using Manager = Mcg.AgentOrchestrator.Infrastructure.DotnetBuildEnvironmentManager; " +
                membership + " public class Probe { const string Root = Manager.IsolatedRootOverrideVariable; " +
                "[Fact] public void Run() { Env.SetEnvironmentVariable(value: \"value\", variable: Root); } }",
            "using-static" => "using static System.Environment; " + membership +
                " public class Probe { [Fact] public void Run() { SetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, null); } }",
            "partial" => membership + " public partial class Probe { [Fact] public void Run() { Set(); } } " +
                "public partial class Probe { void Set() { " + setter + " } }",
            "inherited" => membership + " public abstract class Parent { [Fact] public void Run() { " + setter +
                " } } public class Probe : Parent { }",
            "generic-base" => membership + " public abstract class Parent<T> { [Fact] public void Run() { " + setter +
                " } } public class Probe : Parent<int> { }",
            "base-helper" => "public abstract class Parent { protected sealed class Scope : IDisposable { public void Dispose() { " +
                setter + " } } protected static IDisposable Make() => new Scope(); } " + membership +
                " public class Probe : Parent { [Fact] public void Run() { using var scope = Make(); } } " +
                "public class UnrelatedParallel : Parent { [Fact] public void Run() { } }",
            "unrelated-dispose" => "public abstract class Parent { protected sealed class Scope : IDisposable { public void Dispose() { " +
                setter + " } } protected static IDisposable Make() => new Scope(); } " + membership +
                " public class Probe : Parent { [Fact] public void Run() { using var scope = Make(); } } " +
                "public class UnrelatedParallel : Parent { [Fact] public void Run() { IDisposable other = new Other(); other.Dispose(); } } " +
                "public class Other : IDisposable { public void Dispose() { } }",
            "constructor" => membership + " public class Probe : Parent { [Fact] public void Run() { } } " +
                "public abstract class Parent { protected Parent() { " + setter + " } }",
            "field" => membership + " public class Probe : Parent { [Fact] public void Run() { } } " +
                "public abstract class Parent { readonly int value = Set(); static int Set() { " + setter + " return 0; } }",
            "static-field" => membership + " public class Probe { [Fact] public void Run() { Setter.Touch(); } } " +
                "static class Setter { static readonly int value = Set(); static int Set() { " + setter +
                " return 0; } public static void Touch() { } }",
            "method-group" => membership + " public class Probe : Parent { [Fact] public void Run() { Action action = Set; action(); } } " +
                "public abstract class Parent { protected static void Set() { " + setter + " } }",
            "interface" => membership + " public class Probe { [Fact] public void Run() { Invoke(null!); } " +
                "void Invoke(ISet value) => value.Set(); } interface ISet { void Set(); } " +
                "class Setter : ISet { public void Set() { " + setter + " } }",
            "virtual" => membership + " public class Probe { [Fact] public void Run() { Invoke(null!); } " +
                "void Invoke(Parent value) => value.Set(); } abstract class Parent { public abstract void Set(); } " +
                "class Setter : Parent { public override void Set() { " + setter + " } }",
            "fixture" or "injected" or "collection-fixture" => membership + " public class Probe" +
                (shape == "fixture" ? " : IClassFixture<Setter>" : "") +
                " { " + (shape == "injected" ? "public Probe(Setter fixture) { } " : "") + "[Fact] public void Run() { } } " +
                "public class Setter : IDisposable { public void Dispose() { " + setter + " } }",
            "explicit-dispose" => membership + " public class Probe { [Fact] public void Run() { using var value = new Setter(); } } " +
                "class Setter : IDisposable { void IDisposable.Dispose() { " + setter + " } }",
            "helper-base-constructor" => membership + " public class Probe { [Fact] public void Run() { _ = new Child(); } } " +
                "class Parent { public Parent() { " + setter + " } } class Child : Parent { }",
            "base-initializer" => membership + " public class Probe { [Fact] public void Run() { _ = new Child(); } } " +
                "class Parent { public Parent(int ignored) { " + setter + " } } class Child : Parent { public Child() : base(0) { } }",
            "target-typed" => "using Alias = Setter; " + membership +
                " public class Probe { [Fact] public void Run() { using Alias value = new(); } } " +
                "class Setter : IDisposable { public void Dispose() { " + setter + " } }",
            "wrapper" => membership + " public class Probe { [Fact] public void Run() { Set(\"other\"); } " +
                "void Set(string name) { Environment.SetEnvironmentVariable(name, null); } }",
            _ => membership + " public class Probe { [Fact] public void Run() { " + setter + " } }"
        };
        var collection = shape is "type-collection" or "generic-collection" ? $"[CollectionDefinition(DisableParallelization = {serial.ToString().ToLowerInvariant()})]" :
            $"[CollectionDefinition(\"ProbeLane\"{definition})]";
        var source = "using System; using Xunit; using Mcg.AgentOrchestrator.Infrastructure; " + body + collection +
            " public class ProbeLane" + (shape == "collection-fixture" ? " : ICollectionFixture<Setter>" : "") + " { } " +
            (shape == "duplicate" ? "[CollectionDefinition(\"ProbeLane\", DisableParallelization = true)] public class DuplicateLane { }" : "");
        var compilation = IsolatedRootSetterCollectionScanner.CreateFixtureCompilation(CSharpSyntaxTree.ParseText(source));
        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var result = IsolatedRootSetterCollectionScanner.Scan(compilation);
        Assert.Equal(shape == "wrapper" ? 0 : 1, result.SetterCount);
        Assert.Equal(violations, result.Violations.Count);
        if (violations != 0)
            Assert.Contains("Probe", Assert.Single(result.Violations));
    }

    [Fact]
    public void Shared_base_is_one_instance_with_bounded_explicit_references()
    {
        static CSharpCompilation GetBase() => SharedTestSourcesCompilation.Base;

        var shared = GetBase();
        Assert.Same(shared, GetBase());
        Assert.Single(shared.SyntaxTrees);
        Assert.Equal("System.Runtime", shared.GetSpecialType(SpecialType.System_Object).ContainingAssembly.Identity.Name);

        var referenceCount = shared.References.Count();
        var platformCount = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Length;
        var outputCount = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll").Count();
        var counts = $"Shared references: {referenceCount}, trusted-platform assemblies: {platformCount}, output DLLs: {outputCount}.";
        Assert.True(referenceCount < 256, counts);
        Assert.True(referenceCount < platformCount + outputCount, counts);
        Assert.DoesNotContain(shared.References.OfType<PortableExecutableReference>(), reference =>
            string.Equals(Path.GetFileName(reference.FilePath), Path.GetFileName(typeof(object).Assembly.Location),
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Fixture_compilation_is_small_and_independent_of_the_shared_base()
    {
        var tree = CSharpSyntaxTree.ParseText("class CompilationProbe { }");
        var fixture = IsolatedRootSetterCollectionScanner.CreateFixtureCompilation(tree);
        var shared = SharedTestSourcesCompilation.Base;

        Assert.NotSame(shared, fixture);
        var referenceCount = fixture.References.Count();
        Assert.True(referenceCount < 10, $"Fixture references: {referenceCount}.");
        Assert.Same(tree, Assert.Single(fixture.SyntaxTrees));
        Assert.All(fixture.References, reference => Assert.DoesNotContain(shared.References,
            sharedReference => ReferenceEquals(reference, sharedReference)));
    }
}

internal static class IsolatedRootSetterCollectionScanner
{
    private const string RootVariable = "MCG_DOTNET_ISOLATED_ROOT";
    private static readonly Lazy<CSharpCompilation> FixtureBase = new(() =>
    {
        var coreLibrary = typeof(object).Assembly.Location;
        var runtimeDirectory = Path.GetDirectoryName(coreLibrary)!;
        string[] paths =
        [
            coreLibrary,
            typeof(Environment).Assembly.Location,
            Path.Combine(runtimeDirectory, "System.Runtime.dll"),
            Path.Combine(runtimeDirectory, "netstandard.dll"),
            typeof(FactAttribute).Assembly.Location,
            typeof(CollectionAttribute).Assembly.Location,
            typeof(CollectionDefinitionAttribute).Assembly.Location,
            typeof(IClassFixture<>).Assembly.Location,
            typeof(ICollectionFixture<>).Assembly.Location,
            typeof(Xunit.Sdk.IMessageSink).Assembly.Location,
            typeof(DotnetBuildEnvironmentManager).Assembly.Location,
        ];
        return CSharpCompilation.Create(typeof(IsolatedRootSetterCollectionGuardTests).Assembly.GetName().Name!,
            references: paths.Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path)),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    });

    internal sealed record Result(int SetterCount, int ExemptedCount, List<string> Violations,
        Dictionary<string, string?> Collections);

    internal static CSharpCompilation CreateCompilation(IEnumerable<SyntaxTree> trees) =>
        SharedTestSourcesCompilation.Base.AddSyntaxTrees(trees);

    internal static CSharpCompilation CreateFixtureCompilation(SyntaxTree tree) =>
        FixtureBase.Value.AddSyntaxTrees(tree);

    internal static Result Scan(CSharpCompilation compilation, bool requireFixtureExemption = false)
    {
        var violations = new List<string>();
        var setters = new List<(ISymbol Member, SyntaxNode Site)>();
        var types = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var models = compilation.SyntaxTrees.ToDictionary(tree => tree, tree => compilation.GetSemanticModel(tree));
        var boundReferences = new Dictionary<SyntaxNode, ISymbol?>();
        var boundOwners = new Dictionary<SyntaxNode, ISymbol?>();
        // Index syntax once; bind references only for members that can reach a setter. This avoids
        // semantically binding every call in this large project merely to locate a handful of callers.
        var references = new Dictionary<string, List<SyntaxNode>>(StringComparer.Ordinal);
        var aliases = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var exempted = 0;
        foreach (var (tree, model) in models)
        {
            var root = tree.GetRoot();
            violations.AddRange(root.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error)
                .Select(item => $"Cannot parse setter guard source: {item}"));
            foreach (var node in root.DescendantNodes())
            {
                if (node is TypeDeclarationSyntax declaration && model.GetDeclaredSymbol(declaration) is INamedTypeSymbol type)
                    types.Add(type);
                if (node is UsingDirectiveSyntax { Alias: not null, Name: not null } alias &&
                    model.GetSymbolInfo(alias.Name).Symbol is INamedTypeSymbol aliased)
                {
                    if (!aliases.TryGetValue(aliased.Name, out var names)) aliases[aliased.Name] = names = [];
                    names.Add(alias.Alias.Name.Identifier.ValueText);
                }
                var key = node switch
                {
                    SimpleNameSyntax name => name.Identifier.ValueText,
                    ObjectCreationExpressionSyntax creation => creation.Type switch
                    {
                        SimpleNameSyntax name => name.Identifier.ValueText,
                        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
                        AliasQualifiedNameSyntax qualified => qualified.Name.Identifier.ValueText,
                        _ => null
                    },
                    ImplicitObjectCreationExpressionSyntax or ConstructorInitializerSyntax => ".ctor",
                    _ => null
                };
                if (key is not null)
                {
                    if (!references.TryGetValue(key, out var sites)) references[key] = sites = [];
                    sites.Add(node);
                }
                if (node is not InvocationExpressionSyntax call || CallName(call) != "SetEnvironmentVariable") continue;
                if (model.GetSymbolInfo(call).Symbol is not IMethodSymbol method)
                {
                    violations.Add($"Cannot bind SetEnvironmentVariable at {Location(call)}.");
                    continue;
                }
                if (method.ContainingType.ToDisplayString() != "System.Environment") continue;
                var operation = model.GetOperation(call) as IInvocationOperation;
                var variable = operation?.Arguments.SingleOrDefault(argument => argument.Parameter?.Ordinal == 0)?.Value;
                if (variable?.ConstantValue is { HasValue: true, Value: RootVariable })
                {
                    var member = Owner(model, call);
                    if (member?.ContainingType is { Name: "IsolatedDotnetRootFixture", ContainingType: null } fixture &&
                        fixture.ContainingNamespace.IsGlobalNamespace)
                        exempted++;
                    else if (member is not null)
                        setters.Add((member, call));
                    else
                        violations.Add($"Cannot attribute isolated-root setter at {Location(call)}.");
                }
                else if (variable is null || variable.Type?.TypeKind == TypeKind.Error ||
                         variable.Syntax.ToString().Contains("IsolatedRootOverrideVariable", StringComparison.Ordinal))
                    violations.Add($"Cannot resolve isolated-root variable at {Location(call)}.");
            }
        }
        foreach (var name in new[] { "System.Environment", "Xunit.FactAttribute", "Xunit.CollectionAttribute", "Xunit.CollectionDefinitionAttribute" })
            if (compilation.GetTypeByMetadataName(name) is null) violations.Add($"Setter guard cannot bind {name}.");
        if (requireFixtureExemption && exempted == 0) violations.Add("The exact IsolatedDotnetRootFixture exemption is stale.");

        var testTypes = types.Where(type => !type.IsAbstract && type.TypeKind == TypeKind.Class &&
            type.Arity == 0 && !HasGenericContainer(type) && Hierarchy(type).SelectMany(item => item.GetMembers())
                .OfType<IMethodSymbol>().Any(IsTest)).ToArray();
        var collections = new Dictionary<string, string?>();
        var definitions = types.SelectMany(type => type.GetAttributes().Where(attribute => IsAttribute(attribute, "CollectionDefinitionAttribute"))
            .Select(attribute => (Type: type, Attribute: attribute, Name: AttributeName(attribute, type)))).ToArray();

        foreach (var (member, site) in setters)
        {
            var reaching = Reach(member);
            var affected = testTypes.Where(type => reaching.Any(symbol => DeclaredWithin(symbol.ContainingType, type)) ||
                Hierarchy(type).Skip(1).Any(parent => reaching.Any(symbol =>
                    SymbolEqualityComparer.Default.Equals(symbol.ContainingType, parent.OriginalDefinition) && RunsImplicitly(symbol))) ||
                Fixtures(type).Any(fixture => Hierarchy(fixture).Any(parent => reaching.Any(symbol =>
                    SymbolEqualityComparer.Default.Equals(symbol.ContainingType, parent.OriginalDefinition) && RunsImplicitly(symbol))))).ToArray();
            if (affected.Length == 0)
                violations.Add($"Unreached isolated-root setter: {member.ToDisplayString()} at {Location(site)}.");
            foreach (var type in affected)
            {
                var name = FullName(type);
                if (collections.ContainsKey(name)) continue;
                var attributes = Hierarchy(type).Select(parent => parent.GetAttributes()
                        .Where(attribute => IsAttribute(attribute, "CollectionAttribute")).ToArray())
                    .FirstOrDefault(items => items.Length > 0) ?? [];
                var collection = attributes.Length == 1 ? AttributeName(attributes[0], type) : null;
                collections[name] = collection;
                var matches = definitions.Where(item => collection is not null &&
                    (item.Name == collection || "type:" + FullName(item.Type) == collection)).ToArray();
                if (attributes.Length != 1 || matches.Length != 1 || !matches[0].Attribute.NamedArguments
                        .Any(argument => argument.Key == "DisableParallelization" && argument.Value.Value is true))
                    violations.Add($"{name} reaches a direct isolated-root setter at {Location(site)} but collection " +
                        $"'{collection ?? "<missing or unresolved>"}' does not have exactly one explicitly serial definition.");
            }
        }
        return new Result(setters.Count + exempted, exempted, violations, collections);

        HashSet<ISymbol> Reach(ISymbol setter)
        {
            var reaching = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var pending = new Queue<ISymbol>();
            Add(setter);
            while (pending.TryDequeue(out var target))
            {
                // Construction owns field initializers and scope disposal, including implicit using
                // calls not represented as invocation syntax. Virtual slots include source overrides.
                if (IsInitializer(target) || IsLifecycle(target))
                    foreach (var constructor in target.ContainingType.InstanceConstructors.Concat(target.ContainingType.StaticConstructors))
                        Add(constructor);
                if (target.IsStatic && (IsInitializer(target) || target is IMethodSymbol { MethodKind: MethodKind.StaticConstructor }))
                    foreach (var staticMember in target.ContainingType.GetMembers().Where(item => item.IsStatic &&
                                 item is not INamedTypeSymbol and not IFieldSymbol { IsConst: true }))
                        Add(staticMember);
                if (target is IMethodSymbol method)
                {
                    if (method.MethodKind == MethodKind.Constructor && method.Parameters.All(parameter => parameter.IsOptional || parameter.IsParams))
                        foreach (var derived in types.Where(type => SymbolEqualityComparer.Default.Equals(
                                     type.BaseType?.OriginalDefinition, method.ContainingType)))
                        foreach (var constructor in derived.InstanceConstructors.Where(constructor => constructor.IsImplicitlyDeclared ||
                                     constructor.DeclaringSyntaxReferences.Any(reference =>
                                         reference.GetSyntax() is ConstructorDeclarationSyntax { Initializer: null })))
                            Add(constructor);
                    if (method.OverriddenMethod is not null) Add(method.OverriddenMethod);
                    // Construction and fixture ownership attribute lifecycle calls. The shared
                    // IDisposable slot alone cannot identify the concrete instance being disposed.
                    foreach (var contract in method.ContainingType.AllInterfaces.SelectMany(type => type.GetMembers()).OfType<IMethodSymbol>()
                                 .Where(item => !IsLifecycle(item) && (item.Name == method.Name ||
                                     method.ExplicitInterfaceImplementations.Contains(item, SymbolEqualityComparer.Default))))
                        if (SymbolEqualityComparer.Default.Equals(Normalize(method.ContainingType.FindImplementationForInterfaceMember(contract)), target))
                            Add(contract);
                }
                var isConstructor = target is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor };
                var keys = isConstructor ? new[] { target.ContainingType.Name, ".ctor" }
                    .Concat(aliases.GetValueOrDefault(target.ContainingType.Name) ?? []) : [target.Name];
                foreach (var node in keys.Distinct().Where(references.ContainsKey).SelectMany(key => references[key]))
                {
                    if (node.Ancestors().OfType<InvocationExpressionSyntax>().Any(call => CallName(call) == "nameof")) continue;
                    var model = models[node.SyntaxTree];
                    if (!boundReferences.TryGetValue(node, out var symbol))
                        boundReferences[node] = symbol = Normalize(model.GetSymbolInfo(node).Symbol);
                    if (!SymbolEqualityComparer.Default.Equals(symbol, target)) continue;
                    if (!boundOwners.TryGetValue(node, out var owner))
                        boundOwners[node] = owner = Owner(model, node);
                    if (owner is not null) Add(owner);
                }
            }
            return reaching;

            void Add(ISymbol symbol)
            {
                symbol = Normalize(symbol)!;
                if (reaching.Add(symbol)) pending.Enqueue(symbol);
            }
        }

        IEnumerable<INamedTypeSymbol> Fixtures(INamedTypeSymbol type)
        {
            var fixtureTypes = Hierarchy(type).SelectMany(parent => parent.Interfaces)
                .Where(contract => contract.ContainingNamespace.ToDisplayString() == "Xunit" && contract.Name == "IClassFixture")
                .SelectMany(contract => contract.TypeArguments).OfType<INamedTypeSymbol>();
            fixtureTypes = fixtureTypes.Concat(type.InstanceConstructors.SelectMany(constructor => constructor.Parameters)
                .Select(parameter => parameter.Type).OfType<INamedTypeSymbol>());
            var collection = Hierarchy(type).SelectMany(parent => parent.GetAttributes())
                .FirstOrDefault(attribute => IsAttribute(attribute, "CollectionAttribute"));
            var definitionTypes = definitions.Where(definition => collection is not null &&
                    (definition.Name == AttributeName(collection, type) ||
                     "type:" + FullName(definition.Type) == AttributeName(collection, type))).Select(definition => definition.Type);
            return fixtureTypes.Concat(definitionTypes.SelectMany(definition => definition.AllInterfaces)
                .Where(contract => contract.ContainingNamespace.ToDisplayString() == "Xunit" && contract.Name == "ICollectionFixture")
                .SelectMany(contract => contract.TypeArguments).OfType<INamedTypeSymbol>());
        }
    }

    private static ISymbol? Owner(SemanticModel model, SyntaxNode node)
    {
        var symbol = model.GetEnclosingSymbol(node.SpanStart);
        while (symbol is not null && symbol.ContainingSymbol is not INamedTypeSymbol)
            symbol = symbol.ContainingSymbol;
        return Normalize(symbol);
    }

    private static ISymbol? Normalize(ISymbol? symbol) => symbol is IMethodSymbol method
        ? (method.AssociatedSymbol ?? (ISymbol)(method.ReducedFrom ?? method)).OriginalDefinition
        : symbol?.OriginalDefinition;

    private static bool DeclaredWithin(INamedTypeSymbol? declared, INamedTypeSymbol owner)
    {
        for (; declared is not null; declared = declared.ContainingType)
            if (SymbolEqualityComparer.Default.Equals(declared.OriginalDefinition, owner.OriginalDefinition)) return true;
        return false;
    }

    private static IEnumerable<INamedTypeSymbol> Hierarchy(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
            yield return current;
    }

    private static bool HasGenericContainer(INamedTypeSymbol type)
    {
        for (var containing = type.ContainingType; containing is not null; containing = containing.ContainingType)
            if (containing.Arity != 0) return true;
        return false;
    }

    private static bool IsInitializer(ISymbol symbol) => symbol is IFieldSymbol || symbol is IPropertySymbol property &&
        property.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() is PropertyDeclarationSyntax { Initializer: not null });

    private static bool IsLifecycle(ISymbol symbol) => symbol is IMethodSymbol method &&
        (method.Name.Split('.').Last() is "Dispose" or "DisposeAsync" or "InitializeAsync");

    private static bool RunsImplicitly(ISymbol symbol) => IsInitializer(symbol) || IsLifecycle(symbol) ||
        symbol is IMethodSymbol method && (method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor ||
            IsTest(method));

    private static bool IsTest(IMethodSymbol method) => method.GetAttributes().Any(attribute =>
        attribute.AttributeClass is { } type && Hierarchy(type).Any(parent =>
            parent.ContainingNamespace.ToDisplayString() == "Xunit" && parent.Name is "FactAttribute" or "TheoryAttribute"));

    private static bool IsAttribute(AttributeData attribute, string name) => attribute.AttributeClass is { } type &&
        type.Name == name && type.ContainingNamespace.ToDisplayString() == "Xunit";

    private static string? AttributeName(AttributeData attribute, INamedTypeSymbol declaringType) =>
        attribute.ConstructorArguments.FirstOrDefault().Value switch
        {
            string name => name,
            INamedTypeSymbol type => "type:" + FullName(type),
            null when attribute.AttributeClass is { Arity: 1 } generic => "type:" + FullName((INamedTypeSymbol)generic.TypeArguments[0]),
            null when attribute.ConstructorArguments.Length == 0 => "type:" + FullName(declaringType),
            _ => null
        };

    private static string FullName(INamedTypeSymbol type) => type.ContainingType is { } containing
        ? FullName(containing) + "+" + type.MetadataName
        : (type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString() + ".") + type.MetadataName;

    private static string? CallName(InvocationExpressionSyntax call) => call.Expression switch
    {
        SimpleNameSyntax name => name.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => null
    };

    private static string Location(SyntaxNode node) => $"{node.SyntaxTree.FilePath}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";
}
