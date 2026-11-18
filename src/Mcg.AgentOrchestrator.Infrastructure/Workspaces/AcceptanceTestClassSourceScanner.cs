using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record AcceptanceTestClassDescriptor(string FullName, string? Collection, string? Constraint = null);
internal sealed record AcceptanceTestClassSource(string FullName, IReadOnlyList<string> SourcePaths, string SourceText);

internal static class AcceptanceTestClassSourceScanner
{
    private static readonly AsyncLocal<ParseCountScope?> ParseCounter = new();

    internal static ParseCountScope CountParsesForTests() => new();

    // Flow-scoped: parallel extracts inherit the scope; unrelated scans cannot change its count.
    internal sealed class ParseCountScope : IDisposable
    {
        private readonly ParseCountScope? previous = ParseCounter.Value;
        private int count;
        internal int Count => Volatile.Read(ref count);

        internal ParseCountScope() => ParseCounter.Value = this;
        internal void Record() => Interlocked.Increment(ref count);
        public void Dispose() => ParseCounter.Value = previous;
    }

    private static SyntaxTree ParseText(string text, string path = "")
    {
        ParseCounter.Value?.Record();
        return CSharpSyntaxTree.ParseText(text, path: path);
    }

    // Strict inventory callers cannot interpret missing or malformed candidate input as no tests.
    internal static IReadOnlyList<AcceptanceTestClassDescriptor> ScanProject(string worktreePath, string project)
    {
        var projectPath = Path.Combine(worktreePath, project.Replace('/', Path.DirectorySeparatorChar));
        var document = System.Xml.Linq.XDocument.Load(projectPath);
        if (document.Root?.Name.LocalName != "Project")
            throw new InvalidDataException($"Invalid test project '{project}'.");
        var files = FocusedEvidenceRequestResolver.EnumerateFocusedEvidenceSourceFiles(worktreePath, project).ToArray();
        if (files.Length == 0) throw new InvalidDataException($"No test sources in '{project}'.");
        var counter = ParseCounter.Value;
        foreach (var file in files)
        {
            var syntax = CSharpSourceFileRootReader.Read(file, counter is null ? null : counter.Record);
            if (syntax.GetDiagnostics().Any(item => item.Severity == DiagnosticSeverity.Error))
                throw new InvalidDataException($"Cannot parse test source '{file}'.");
        }
        var classes = Scan(worktreePath, files);
        return classes.Count > 0 ? classes : throw new InvalidDataException($"No runnable test classes in '{project}'.");
    }

    // The selection already read its closure. Preserve strict whole-project validation,
    // while extracting from the same parse and handing source contents to subsequent parts.
    internal static IReadOnlyList<AcceptanceTestClassDescriptor> ScanProject(string worktreePath, string project,
        SelectionSourceTexts sourceTexts)
    {
        var projectPath = Path.Combine(worktreePath, project.Replace('/', Path.DirectorySeparatorChar));
        var document = System.Xml.Linq.XDocument.Parse(SelectionFileReadProbe.ReadAllText(projectPath));
        if (document.Root?.Name.LocalName != "Project")
            throw new InvalidDataException($"Invalid test project '{project}'.");
        var files = FocusedEvidenceRequestResolver.EnumerateFocusedEvidenceSourceFiles(worktreePath, project).ToArray();
        if (files.Length == 0) throw new InvalidDataException($"No test sources in '{project}'.");
        var classes = ScanTexts(files, sourceTexts);
        return classes.Count > 0 ? classes : throw new InvalidDataException($"No runnable test classes in '{project}'.");
    }

    internal static IReadOnlyList<AcceptanceTestClassDescriptor> Scan(string worktreePath, SelectionSourceTexts sourceTexts)
    {
        var files = LaneTestSourceRootResolver.SourceFiles(worktreePath, LaneTestSourceRootResolver.Resolve(worktreePath));
        if (files.Length == 0)
        {
            AcceptanceTestClassSourceCache.Current.Forget(worktreePath);
            return [];
        }
        // Lane identity uses the same non-validating extracts and warm/store hits as Scan.
        return ScanExtracts(AcceptanceTestClassSourceCache.Current.Extract(worktreePath, files, sourceTexts: sourceTexts));
    }

    private static IReadOnlyList<AcceptanceTestClassDescriptor> ScanTexts(string[] files, SelectionSourceTexts sourceTexts)
    {
        var extracts = new List<FileExtract>(files.Length);
        foreach (var file in files)
        {
            var root = ParseText(sourceTexts.Read(file), path: file).GetRoot();
            if (root.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
                throw new InvalidDataException($"Cannot parse test source '{file}'.");
            extracts.Add(ExtractRoot(root));
        }
        return ScanExtracts(extracts);
    }

    // Preserve Scan's membership contract; diagnostic callers can request only declaring files.
    internal static IReadOnlyList<AcceptanceTestClassSource> ScanSources(string worktreePath,
        bool includeAncestorSources = true)
    {
        var sourceFiles = LaneTestSourceRootResolver.SourceFiles(worktreePath, LaneTestSourceRootResolver.Resolve(worktreePath));
        var classes = Scan(worktreePath, sourceFiles);
        var declarations = new Dictionary<string, List<(string Path, string Text, string? BaseName)>>(StringComparer.Ordinal);
        foreach (var path in sourceFiles)
        {
            var text = File.ReadAllText(path);
            var syntax = ParseText(text).GetRoot();
            if (syntax.ContainsDiagnostics && syntax.GetDiagnostics().Any(item => item.Severity == DiagnosticSeverity.Error))
                throw new InvalidDataException($"Cannot parse test source '{path}'.");
            foreach (var node in syntax.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var name = NameOf(node);
                if (!declarations.TryGetValue(name, out var parts)) declarations[name] = parts = [];
                var baseType = node.BaseList?.Types.FirstOrDefault()?.Type;
                var baseName = BaseNameOf(baseType);
                parts.Add((Path.GetRelativePath(worktreePath, path).Replace('\\', '/'), text, baseName));
            }
        }
        var byShortName = declarations.Keys.GroupBy(name => name.Split('.', '+').Last(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        return classes.Select(item =>
        {
            var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            void Include(string name)
            {
                if (!visited.Add(name) || !declarations.TryGetValue(name, out var parts)) return;
                foreach (var part in parts)
                {
                    files[part.Path] = part.Text;
                    if (includeAncestorSources && part.BaseName is not null && byShortName.TryGetValue(part.BaseName, out var bases))
                        foreach (var parent in bases) Include(parent);
                }
            }
            Include(item.FullName);
            return new AcceptanceTestClassSource(item.FullName, files.Keys.ToArray(), string.Join("\n", files.Values));
        }).ToArray();
    }

    private static string? BaseNameOf(TypeSyntax? type) => type switch
    {
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        QualifiedNameSyntax qualified => BaseNameOf(qualified.Right),
        AliasQualifiedNameSyntax alias => BaseNameOf(alias.Name),
        _ => null
    };

    internal static IReadOnlyList<AcceptanceTestClassDescriptor> Scan(string worktreePath) =>
        Scan(worktreePath, LaneTestSourceRootResolver.SourceFiles(worktreePath, LaneTestSourceRootResolver.Resolve(worktreePath)));

    internal static IReadOnlyList<AcceptanceTestClassDescriptor> Scan(string worktreePath, string[] files)
    {
        if (files.Length == 0)
        {
            AcceptanceTestClassSourceCache.Current.Forget(worktreePath);
            return [];
        }

        var extracts = AcceptanceTestClassSourceCache.Current.Extract(worktreePath, files);
        return ScanExtracts(extracts);
    }

    internal static IReadOnlyList<AcceptanceTestClassDescriptor> ScanSourceTexts(IEnumerable<string> sources) =>
        ScanExtracts(sources.Select(ExtractText).ToArray());

    internal static IReadOnlyList<AcceptanceTestClassDescriptor> ScanSyntaxTreesForTests(IEnumerable<SyntaxTree> trees) =>
        ScanExtracts(trees.Select(tree => ExtractRoot(tree.GetRoot())).ToArray());

    private static IReadOnlyList<AcceptanceTestClassDescriptor> ScanExtracts(IReadOnlyList<FileExtract> extracts)
    {
        var derivedFacts = AcceptanceTestClassSourceScannerFactAncestry.Resolve(
            extracts.Select(extract => extract.Ancestry));
        var constants = extracts.SelectMany(extract => extract.Constants)
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.Select(item => item.Value).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

        var classes = extracts.SelectMany(extract => extract.Classes)
            .Select(item => new SourceClass(item.FullName, item.ShortName, item.Abstract, item.BaseName,
                item.HasFacts || derivedFacts.Contains(item.FullName),
                ResolveCollection(item.FullName, item.Collection, constants),
                item.IsolationTraits.Select(trait => ResolveIsolationTrait(item.FullName, trait, constants))
                    .Aggregate((string?)null, (left, right) => MergeConstraints(item.FullName, left, right))))
            .GroupBy(item => item.FullName, StringComparer.Ordinal)
            .Select(group => group.Aggregate((left, right) => left with
            {
                Abstract = left.Abstract || right.Abstract,
                HasFacts = left.HasFacts || right.HasFacts,
                Collection = left.Collection ?? right.Collection,
                Constraint = MergeConstraints(left.FullName, left.Constraint, right.Constraint),
                BaseName = left.BaseName ?? right.BaseName
            }))
            .ToArray();
        var byName = classes.GroupBy(item => item.ShortName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        return classes.Where(item => !item.Abstract && HasRunnableFacts(item, byName, []))
            .Select(item => new AcceptanceTestClassDescriptor(item.FullName,
                InheritedValue(item, byName, source => source.Collection, []),
                InheritedValue(item, byName, source => source.Constraint, [])))
            .OrderBy(item => item.FullName, StringComparer.Ordinal)
            .ToArray();
    }

    internal static FileExtract ExtractText(string text) => ExtractRoot(ParseText(text).GetRoot());

    private static FileExtract ExtractRoot(SyntaxNode root)
    {
        var constants = root.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .Where(field => field.Parent is ClassDeclarationSyntax)
            .Where(field => field.Modifiers.Any(SyntaxKind.ConstKeyword) &&
                            field.Declaration.Type.ToString() == "string")
            .SelectMany(field => field.Declaration.Variables.Select(variable => new
            {
                Owner = ((ClassDeclarationSyntax)field.Parent!).Identifier.ValueText,
                Variable = variable
            }))
            .Where(item => item.Variable.Initializer?.Value is LiteralExpressionSyntax)
            .Select(item => new ConstantExtract($"{item.Owner}.{item.Variable.Identifier.ValueText}",
                ((LiteralExpressionSyntax)item.Variable.Initializer!.Value).Token.ValueText))
            .ToArray();

        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Select(node => new ClassExtract(
                NameOf(node),
                node.Identifier.ValueText,
                node.Modifiers.Any(SyntaxKind.AbstractKeyword),
                node.BaseList?.Types.FirstOrDefault()?.Type.ToString().Split('.').Last(),
                HasFacts(node),
                ExtractCollection(node),
                ExtractIsolationTraits(node)))
            .ToArray();
        return new FileExtract(constants, classes, AcceptanceTestClassSourceScannerFactAncestry.Extract(root));
    }

    internal static string NameOf(ClassDeclarationSyntax node)
    {
        var namespaces = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse().Select(item => item.Name.ToString());
        var containing = node.Ancestors().OfType<ClassDeclarationSyntax>()
            .Reverse().Select(item => item.Identifier.ValueText);
        var typeName = string.Join("+", containing.Append(node.Identifier.ValueText));
        return string.Join(".", namespaces.Append(typeName));
    }

    private static bool HasFacts(ClassDeclarationSyntax node) =>
        node.Members.OfType<MethodDeclarationSyntax>().Any(method =>
            method.AttributeLists.SelectMany(list => list.Attributes).Any(attribute =>
                attribute.Name.ToString().Split('.').Last() is "Fact" or "FactAttribute" or
                    "Theory" or "TheoryAttribute"));

    private static CollectionExpression ExtractCollection(ClassDeclarationSyntax node)
    {
        var attribute = node.AttributeLists.SelectMany(list => list.Attributes)
            .FirstOrDefault(item => item.Name.ToString().Split('.').Last() is "Collection" or "CollectionAttribute");
        if (attribute is null)
        {
            return new(CollectionExpressionKind.None, null);
        }
        return ExtractExpression(attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression);
    }

    private static CollectionExpression[] ExtractIsolationTraits(ClassDeclarationSyntax node) =>
        node.AttributeLists.SelectMany(list => list.Attributes)
            .Where(item => item.Name.ToString().Split('.').Last() is "Trait" or "TraitAttribute")
            .Where(item => item.ArgumentList?.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax key &&
                key.IsKind(SyntaxKind.StringLiteralExpression) && key.Token.ValueText == "Isolation")
            .Select(item => ExtractExpression(item.ArgumentList?.Arguments.ElementAtOrDefault(1)?.Expression))
            .ToArray();

    private static CollectionExpression ExtractExpression(ExpressionSyntax? expression) =>
        expression switch
        {
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) =>
                new(CollectionExpressionKind.Literal, literal.Token.ValueText),
            InvocationExpressionSyntax invocation when invocation.Expression.ToString() == "nameof" =>
                new(CollectionExpressionKind.NameOf,
                    invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression.ToString().Split('.').Last()),
            MemberAccessExpressionSyntax member => new(CollectionExpressionKind.Member, member.ToString()),
            IdentifierNameSyntax identifier => new(CollectionExpressionKind.Identifier, identifier.Identifier.ValueText),
            _ => new(CollectionExpressionKind.Unresolvable, null)
        };

    private static string ResolveIsolationTrait(string fullName, CollectionExpression expression,
        IReadOnlyDictionary<string, string[]> constants)
    {
        var value = ResolveExpression(expression, constants);
        return !string.IsNullOrWhiteSpace(value) ? value :
            throw new InvalidDataException($"Cannot resolve Isolation trait on '{fullName}'.");
    }

    private static string? MergeConstraints(string fullName, string? left, string? right)
    {
        if (left is not null && right is not null && left != right)
            throw new InvalidDataException($"Test class '{fullName}' declares conflicting Isolation traits '{left}' and '{right}'.");
        return left ?? right;
    }

    private static string? ResolveExpression(CollectionExpression expression,
        IReadOnlyDictionary<string, string[]> constants) =>
        expression.Kind switch
        {
            CollectionExpressionKind.Literal or CollectionExpressionKind.NameOf => expression.Value,
            CollectionExpressionKind.Member when constants.TryGetValue(expression.Value!, out var resolved) &&
                                                 resolved.Length == 1 => resolved[0],
            CollectionExpressionKind.Identifier => ResolveUnqualifiedConstant(expression.Value!, constants),
            _ => null
        };

    private static string? ResolveCollection(string fullName, CollectionExpression expression,
        IReadOnlyDictionary<string, string[]> constants)
    {
        if (expression.Kind == CollectionExpressionKind.None) return null;
        var value = ResolveExpression(expression, constants);
        if (value is null && expression.Kind switch
            {
                CollectionExpressionKind.Member => constants.TryGetValue(expression.Value!, out var values) &&
                                                   values.Length > 1,
                CollectionExpressionKind.Identifier => constants.Any(item =>
                    item.Key.EndsWith($".{expression.Value}", StringComparison.Ordinal) &&
                    item.Value.Length > 1),
                _ => false
            }) return null;
        return value ?? throw new InvalidDataException($"Cannot resolve test collection on '{fullName}'.");
    }

    private static string? ResolveUnqualifiedConstant(string name, IReadOnlyDictionary<string, string[]> constants)
    {
        var matches = constants.Where(item => item.Key.EndsWith($".{name}", StringComparison.Ordinal)).ToArray();
        if (matches.Any(item => item.Value.Length != 1)) return null;
        var values = matches.Select(item => item.Value[0]).Distinct(StringComparer.Ordinal).ToArray();
        return values.Length == 1 ? values[0] : null;
    }

    private static bool HasRunnableFacts(SourceClass item, IReadOnlyDictionary<string, SourceClass[]> byName,
        HashSet<string> visited)
    {
        if (!visited.Add(item.FullName)) return false;
        return item.HasFacts || item.BaseName is not null && byName.TryGetValue(item.BaseName, out var bases) &&
            bases.Any(parent => HasRunnableFacts(parent, byName, visited));
    }

    private static string? InheritedValue(SourceClass item, IReadOnlyDictionary<string, SourceClass[]> byName,
        Func<SourceClass, string?> select, HashSet<string> visited)
    {
        var declared = select(item);
        if (declared is not null || !visited.Add(item.FullName)) return declared;
        return item.BaseName is not null && byName.TryGetValue(item.BaseName, out var bases)
            ? bases.Select(parent => InheritedValue(parent, byName, select, visited)).FirstOrDefault(value => value is not null)
            : null;
    }

    private sealed record SourceClass(string FullName, string ShortName, bool Abstract, string? BaseName,
        bool HasFacts, string? Collection, string? Constraint);

    // Only file-local syntax facts are cached; resolution against other files happens on every scan.
    internal sealed record FileExtract(IReadOnlyList<ConstantExtract> Constants, IReadOnlyList<ClassExtract> Classes,
        AcceptanceTestClassSourceScannerFactAncestry.AncestryFile? Ancestry = null);
    internal sealed record ConstantExtract(string Key, string Value);
    internal sealed record ClassExtract(string FullName, string ShortName, bool Abstract, string? BaseName,
        bool HasFacts, CollectionExpression Collection, IReadOnlyList<CollectionExpression> IsolationTraits);
    internal sealed record CollectionExpression(CollectionExpressionKind Kind, string? Value);
    internal enum CollectionExpressionKind { None, Literal, NameOf, Member, Identifier, Unresolvable }
}
