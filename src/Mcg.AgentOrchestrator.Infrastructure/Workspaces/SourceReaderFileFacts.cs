using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Rebuildable facts for exactly one path and content hash. No compilation-owned objects are persisted.
internal sealed record SourceReaderFileFacts(
    [property: JsonRequired] string Path,
    [property: JsonRequired] string ContentHash,
    [property: JsonRequired] bool DeclaresSnapshotCache,
    [property: JsonRequired] bool Unbounded,
    [property: JsonRequired] string[] DeclaredNames,
    [property: JsonRequired] string[] ReferencedNames,
    [property: JsonRequired] string[] TypeDeclarationShapes,
    [property: JsonRequired] string[] DeclaredSymbolIds,
    [property: JsonRequired] SourceReaderFileFacts.TypeFact[] Types,
    [property: JsonRequired] SourceReaderFileFacts.MemberFact[] Members)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AssemblyScanningGuardTestClasses.ScanFile? AssemblyScans { get; init; }

    // Only the live candidate can provide syntax for narrowing the plan.
    [JsonIgnore]
    private SyntaxNode? SyntaxRoot { get; init; }

    private const string IndexerName = "this[]";
    private const string ImplicitConversionName = "operator implicit";
    private const string ExplicitConversionName = "operator explicit";

    internal sealed record TypeFact(
        [property: JsonRequired] string Id,
        [property: JsonRequired] string Owner,
        [property: JsonRequired] string? BaseId,
        [property: JsonRequired] string BaseList);

    internal sealed record MemberFact(
        [property: JsonRequired] string Id,
        [property: JsonRequired] string Name,
        [property: JsonRequired] string Owner,
        [property: JsonRequired] string TypeId,
        [property: JsonRequired] int Ordinal,
        [property: JsonRequired] bool Inheritable,
        [property: JsonRequired] bool SnapshotSeed,
        [property: JsonRequired] bool RootSeed,
        [property: JsonRequired] bool ProcessSeed,
        [property: JsonRequired] string[] Parameters,
        [property: JsonRequired] string[] Targets,
        [property: JsonRequired] string[] InvocationTexts,
        [property: JsonRequired] string[][] InvocationTargets);

    internal static IEnumerable<SyntaxNode> Declarations(SyntaxNode root) => root.DescendantNodes()
        .Where(node => node is BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax ||
            node is VariableDeclaratorSyntax { Parent: { Parent: BaseFieldDeclarationSyntax } });

    internal static string RelativePath(ImpactReplayTestSourceIndex sources, SyntaxTree tree) =>
        (sources.RepositoryRoot is { } root && System.IO.Path.IsPathRooted(tree.FilePath)
            ? System.IO.Path.GetRelativePath(root, tree.FilePath) : tree.FilePath).Replace('\\', '/');

    internal static SourceReaderFileFacts Describe(ImpactReplayTestSourceIndex sources, SyntaxTree tree)
    {
        var root = tree.GetRoot();
        var nodes = root.DescendantNodes().ToArray();
        var references = root.DescendantTokens().Where(token => token.IsKind(SyntaxKind.IdentifierToken))
            .Select(token => token.ValueText).Concat(nodes.SelectMany(ImplicitReferences))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        // These causes cannot be bounded by declaration/reference names in either changed version.
        var unbounded = nodes.Any(node => node is BaseTypeDeclarationSyntax and not
                (ClassDeclarationSyntax or StructDeclarationSyntax or InterfaceDeclarationSyntax or
                    EnumDeclarationSyntax or RecordDeclarationSyntax) ||
            node is UsingDirectiveSyntax directive && directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)) ||
            references.Any(name => IsCollectionBuilder(name) || name is
                "InterpolatedStringHandler" or "InterpolatedStringHandlerAttribute" or
                "AsyncMethodBuilder" or "AsyncMethodBuilderAttribute");
        var contentHash = Hash(tree.GetText().ToString());
        var scanPositions = nodes.OfType<InvocationExpressionSyntax>()
            .Where(AssemblyScanningGuardTestClasses.IsScanSite).Select(call => call.SpanStart).Distinct().ToArray();
        return new(RelativePath(sources, tree), contentHash,
            nodes.OfType<ClassDeclarationSyntax>().Any(type => type.Identifier.ValueText == "RepositorySourceSnapshotCache"),
            unbounded, GetDeclaredNames(nodes), references, GetDeclarationShapes(nodes), [],
            nodes.OfType<ClassDeclarationSyntax>().Select(type => new TypeFact("", ImpactReplayTestSourceIndex.ClassName(type),
                null, type.BaseList?.WithoutTrivia().ToFullString() ?? "")).ToArray(), [])
        {
            AssemblyScans = scanPositions.Length == 0 ? null : new(contentHash, tree.Length, scanPositions),
            SyntaxRoot = root
        };
    }

    // Include namespace segments and attribute shorthand as conservative invalidation names.
    private static string[] GetDeclaredNames(IEnumerable<SyntaxNode> nodes) =>
        nodes.SelectMany(node => node switch
        {
            RecordDeclarationSyntax record => RecordNames(record),
            BaseTypeDeclarationSyntax type => new[] { type.Identifier.ValueText },
            DelegateDeclarationSyntax type => new[] { type.Identifier.ValueText },
            OperatorDeclarationSyntax op => new[] { OperatorName(op.OperatorToken.Text) },
            ConversionOperatorDeclarationSyntax conversion => new[] { OperatorName(conversion.ImplicitOrExplicitKeyword.Text) },
            IndexerDeclarationSyntax => new[] { IndexerName },
            MethodDeclarationSyntax method => new[] { method.Identifier.ValueText },
            PropertyDeclarationSyntax property => new[] { property.Identifier.ValueText },
            EventDeclarationSyntax member => new[] { member.Identifier.ValueText },
            VariableDeclaratorSyntax variable => new[] { variable.Identifier.ValueText },
            EnumMemberDeclarationSyntax member => new[] { member.Identifier.ValueText },
            BaseNamespaceDeclarationSyntax space => space.Name.DescendantTokens()
                .Where(token => token.IsKind(SyntaxKind.IdentifierToken)).Select(token => token.ValueText),
            _ => Enumerable.Empty<string>()
        }).Where(name => !string.IsNullOrWhiteSpace(name)).SelectMany(NameVariants)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static string OperatorName(string token) => "operator " + token;

    private static IEnumerable<string> RecordNames(RecordDeclarationSyntax record)
    {
        yield return record.Identifier.ValueText;
        yield return "operator ==";
        yield return "operator !=";
        if (record.ParameterList is not { } parameters) yield break;
        yield return "Deconstruct";
        foreach (var parameter in parameters.Parameters) yield return parameter.Identifier.ValueText;
    }

    private static string[] NameVariants(string name) =>
        name.Length > 9 && name.EndsWith("Attribute", StringComparison.Ordinal) ? [name, name[..^9]] : [name];

    private static string[] GetDeclarationShapes(IEnumerable<SyntaxNode> nodes) =>
        nodes.Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or BaseNamespaceDeclarationSyntax ||
                node is ConstructorDeclarationSyntax or TypeParameterConstraintClauseSyntax or EnumMemberDeclarationSyntax ||
                node is OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax or IndexerDeclarationSyntax ||
                node is PropertyDeclarationSyntax property && property.Modifiers.Any(SyntaxKind.RequiredKeyword) ||
                node is FieldDeclarationSyntax field && (field.Modifiers.Any(SyntaxKind.ConstKeyword) ||
                    field.Modifiers.Any(SyntaxKind.RequiredKeyword)))
            .Select(DeclarationShape).Order(StringComparer.Ordinal).ToArray();

    // Syntax-only declaration shape: scope, kind, name, arity and accessibility can all change lookup
    // inside an unchanged member signature. Constant values also flow through unchanged declarations
    // into overload resolution. Constructor availability, required members and constraints affect
    // generic overload eligibility through unchanged intermediary signatures as well.
    // These are invalidation facts, not persisted symbol IDs.
    private static string DeclarationShape(SyntaxNode declaration) => string.Join("/",
        declaration.AncestorsAndSelf().Reverse().Select(node => node switch
        {
            BaseNamespaceDeclarationSyntax space => "namespace " + space.Name.WithoutTrivia().ToFullString(),
            BaseTypeDeclarationSyntax type => string.Concat(type.AttributeLists.Select(list => list.WithoutTrivia().ToFullString())) +
                $"{type.Kind()} {string.Join(" ", type.Modifiers.Select(token => token.ValueText))} " +
                $"{type.Identifier.ValueText}`{(type as TypeDeclarationSyntax)?.TypeParameterList?.Parameters.Count ?? 0}" +
                (type as TypeDeclarationSyntax)?.ParameterList?.WithoutTrivia().ToFullString() +
                (type is ClassDeclarationSyntax ? null : type.BaseList?.WithoutTrivia().ToFullString()),
            DelegateDeclarationSyntax type => type.WithoutTrivia().ToFullString(),
            OperatorDeclarationSyntax op => op.WithBody(null).WithExpressionBody(null)
                .WithSemicolonToken(default).WithoutTrivia().ToFullString(),
            ConversionOperatorDeclarationSyntax conversion => conversion.WithBody(null).WithExpressionBody(null)
                .WithSemicolonToken(default).WithoutTrivia().ToFullString(),
            IndexerDeclarationSyntax indexer => indexer.WithAccessorList(null).WithExpressionBody(null)
                .WithSemicolonToken(default).WithoutTrivia().ToFullString() + " " +
                string.Join(" ", indexer.AccessorList?.Accessors.Select(accessor =>
                    accessor.WithBody(null).WithExpressionBody(null).WithSemicolonToken(default)
                        .WithoutTrivia().ToFullString()) ?? []),
            ConstructorDeclarationSyntax constructor => constructor.WithBody(null).WithExpressionBody(null)
                .WithSemicolonToken(default).WithoutTrivia().ToFullString(),
            MethodDeclarationSyntax method => method.WithBody(null).WithExpressionBody(null)
                .WithSemicolonToken(default).WithoutTrivia().ToFullString(),
            TypeParameterConstraintClauseSyntax or EnumMemberDeclarationSyntax => node.WithoutTrivia().ToFullString(),
            PropertyDeclarationSyntax property when property.Modifiers.Any(SyntaxKind.RequiredKeyword) =>
                property.WithAccessorList(null).WithExpressionBody(null).WithInitializer(null)
                    .WithSemicolonToken(default).WithoutTrivia().ToFullString(),
            FieldDeclarationSyntax field when field.Modifiers.Any(SyntaxKind.ConstKeyword) ||
                    field.Modifiers.Any(SyntaxKind.RequiredKeyword) => field.WithoutTrivia().ToFullString(),
            _ => null
        }).Where(part => part is not null));

    private static bool ImplicitBindingName(string name) => name is
        "GetEnumerator" or "Current" or "MoveNext" or "Dispose" or
        "GetAsyncEnumerator" or "MoveNextAsync" or "DisposeAsync" or
        "Deconstruct" or "GetAwaiter" or "IsCompleted" or "GetResult" or
        "OnCompleted" or "UnsafeOnCompleted" or "Invoke" or "Add" or
        "Slice" or "Length" or "Count" or "AppendLiteral" or "AppendFormatted" or
        "Select" or "SelectMany" or "Where" or "Join" or "GroupJoin" or "GroupBy" or
        "OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending" or "Cast";

    // Implicit syntax can bind members without an identifier token. Conservative names keep those
    // consumers in the same fixed-point dependency closure as explicit member references.
    private static string[] ImplicitReferences(SyntaxNode node) => node switch
    {
        CommonForEachStatementSyntax => ["GetEnumerator", "Current", "MoveNext", "Dispose",
            "GetAsyncEnumerator", "MoveNextAsync", "DisposeAsync", "GetAwaiter", "IsCompleted",
            "GetResult", "OnCompleted", "UnsafeOnCompleted", ImplicitConversionName, ExplicitConversionName],
        AwaitExpressionSyntax => ["GetAwaiter", "IsCompleted", "GetResult", "OnCompleted", "UnsafeOnCompleted"],
        UsingStatementSyntax => ["Dispose", "DisposeAsync", "GetAwaiter", "IsCompleted", "GetResult",
            "OnCompleted", "UnsafeOnCompleted"],
        LocalDeclarationStatementSyntax declaration when declaration.UsingKeyword.IsKind(SyntaxKind.UsingKeyword) =>
            ["Dispose", "DisposeAsync", "GetAwaiter", "IsCompleted", "GetResult", "OnCompleted", "UnsafeOnCompleted"],
        TupleExpressionSyntax or DeclarationExpressionSyntax or
            ParenthesizedVariableDesignationSyntax => ["Deconstruct"],
        // A positional pattern can also obtain elements through ITuple's indexer.
        PositionalPatternClauseSyntax => ["Deconstruct", IndexerName],
        CollectionExpressionSyntax => ["Add", "GetEnumerator", "Current", "MoveNext", "Dispose", "Count", "Length"],
        InitializerExpressionSyntax initializer when initializer.IsKind(SyntaxKind.CollectionInitializerExpression) => ["Add"],
        RangeExpressionSyntax => ["Length", "Count", "Slice"],
        ListPatternSyntax => ["Length", "Count", "Slice", IndexerName],
        ElementAccessExpressionSyntax or ElementBindingExpressionSyntax or ImplicitElementAccessSyntax => [IndexerName],
        BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalAndExpression) =>
            ["operator &", "operator true", "operator false"],
        BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalOrExpression) =>
            ["operator |", "operator true", "operator false"],
        BinaryExpressionSyntax binary when binary.Kind() is not
            (SyntaxKind.IsExpression or SyntaxKind.AsExpression or SyntaxKind.CoalesceExpression) =>
            [OperatorName(binary.OperatorToken.Text)],
        // The binary fallback can use a conversion when storing the result back into the left operand.
        AssignmentExpressionSyntax assignment when assignment.Kind() is not
            (SyntaxKind.SimpleAssignmentExpression or SyntaxKind.CoalesceAssignmentExpression) =>
            [OperatorName(assignment.OperatorToken.Text), OperatorName(assignment.OperatorToken.Text.TrimEnd('=')),
                ImplicitConversionName, ExplicitConversionName],
        PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.IndexExpression) => ["Length", "Count"],
        PrefixUnaryExpressionSyntax prefix when prefix.Kind() is not
            (SyntaxKind.AddressOfExpression or SyntaxKind.PointerIndirectionExpression) =>
            [OperatorName(prefix.OperatorToken.Text)],
        PostfixUnaryExpressionSyntax postfix when postfix.Kind() is
            SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression =>
            [OperatorName(postfix.OperatorToken.Text)],
        CastExpressionSyntax => [ImplicitConversionName, ExplicitConversionName],
        // Implicit conversions without dedicated syntax reach callers through declaration shapes.
        IfStatementSyntax or WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax { Condition: not null } or
            ConditionalExpressionSyntax or CatchFilterClauseSyntax or WhenClauseSyntax => ["operator true"],
        QueryExpressionSyntax => ["Select", "SelectMany", "Where", "Join", "GroupJoin", "GroupBy",
            "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending", "Cast"],
        InterpolatedStringExpressionSyntax => ["AppendLiteral", "AppendFormatted"],
        _ => []
    };

    private static bool IsCollectionBuilder(string name) => name is "CollectionBuilder" or "CollectionBuilderAttribute";

    internal static string Hash(string text)
    {
        var bytes = new byte[checked(text.Length * sizeof(char))];
        for (var index = 0; index < text.Length; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(index * sizeof(char)), text[index]);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    internal static bool HasUniquePaths(IReadOnlyList<SourceReaderFileFacts> files) =>
        files.All(file => !string.IsNullOrWhiteSpace(file.Path)) &&
        files.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() == files.Count;

    // The only reuse rule: identical text AND no reference to any old/new changed declaration.
    // A null plan means dependency coverage cannot be bounded, so the caller must analyse every file.
    internal static HashSet<string>? Plan(SourceReaderFileFacts[] baseline, SourceReaderFileFacts[] candidate)
    {
        if (!HasUniquePaths(baseline) || !HasUniquePaths(candidate)) return null;
        var old = baseline.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var current = candidate.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var changed = old.Keys.Union(current.Keys, StringComparer.Ordinal).Where(path =>
            !old.TryGetValue(path, out var before) || !current.TryGetValue(path, out var after) ||
            before.ContentHash != after.ContentHash).ToHashSet(StringComparer.Ordinal);
        if (changed.Count == 1 && current.TryGetValue(changed.Single(), out var changedFile))
            current[changedFile.Path] = BoundNewClass(changedFile, baseline, candidate);
        var versions = baseline.Concat(current.Values).Where(file => changed.Contains(file.Path)).ToArray();
        if (versions.Any(file => file.Unbounded)) return null;
        var names = versions.SelectMany(file => file.DeclaredNames).ToHashSet(StringComparer.Ordinal);
        var baseListChanged = false;
        // Inheritance changes can rebind callers through unchanged intermediary declarations.
        // Seed both old and new base names, then follow those intermediaries to their callers.
        foreach (var path in changed)
        {
            var before = old.GetValueOrDefault(path)?.Types ?? [];
            var after = current.GetValueOrDefault(path)?.Types ?? [];
            if (!before.Select(type => (type.Owner, type.BaseList)).OrderBy(type => type.Owner, StringComparer.Ordinal)
                    .SequenceEqual(after.Select(type => (type.Owner, type.BaseList)).OrderBy(type => type.Owner, StringComparer.Ordinal)) &&
                before.Concat(after).Any(type => type.BaseList.Length != 0))
            {
                baseListChanged = true;
                names.UnionWith(before.Concat(after).SelectMany(type => BaseNames(SyntaxFactory.ParseTokens(type.BaseList))));
            }
        }
        if (baseListChanged || names.Any(ImplicitBindingName) ||
            changed.Any(path => !(old.GetValueOrDefault(path)?.TypeDeclarationShapes ?? [])
                .SequenceEqual(current.GetValueOrDefault(path)?.TypeDeclarationShapes ?? [], StringComparer.Ordinal)))
        {
            // A type or constant change can rebind an unchanged intermediary's signature or value. Its callers may
            // spell only the intermediary's members, so follow declaration/reference dependencies
            // to a fixed point before reusing facts. Include both versions, including removed files.
            var pending = baseline.Concat(candidate).ToList();
            bool expanded;
            do
            {
                expanded = false;
                for (var index = pending.Count - 1; index >= 0; index--)
                {
                    var file = pending[index];
                    if (!file.ReferencedNames.Any(names.Contains)) continue;
                    foreach (var name in file.DeclaredNames) expanded |= names.Add(name);
                    pending.RemoveAt(index);
                }
            } while (expanded);
        }
        // Collection expressions select builder methods without spelling their names at the use site.
        // The attribute may live in an unchanged file while its builder is edited elsewhere.
        if (baseline.Concat(candidate).Any(file => file.ReferencedNames.Any(IsCollectionBuilder) &&
                file.ReferencedNames.Any(names.Contains))) return null;
        return candidate.Where(file => changed.Contains(file.Path) ||
            file.ReferencedNames.Any(names.Contains)).Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
    }

    private static SourceReaderFileFacts BoundNewClass(SourceReaderFileFacts file, SourceReaderFileFacts[] baseline,
        SourceReaderFileFacts[] candidate)
    {
        // Limit the bound to a new global class at the end of the changed file. A static, partial,
        // or nested declaration can affect lookup without callers spelling the new class name.
        if (file.Unbounded || file.SyntaxRoot is not CompilationUnitSyntax root ||
            root.Members.LastOrDefault() is not ClassDeclarationSyntax type ||
            type.Modifiers.Any(SyntaxKind.StaticKeyword) || type.Modifiers.Any(SyntaxKind.PartialKeyword) ||
            type.DescendantNodes().Any(node => node is BaseTypeDeclarationSyntax ||
                node is ParameterSyntax parameter && parameter.Modifiers.Any(SyntaxKind.ThisKeyword)))
            return file;

        var owner = ImpactReplayTestSourceIndex.ClassName(type);
        if (baseline.Any(other => other.Types.Any(declaration => declaration.Owner == owner)) ||
            candidate.Sum(other => other.Types.Count(declaration => declaration.Owner == owner)) != 1)
            return file;

        var typeNames = NameVariants(type.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
        var otherFiles = baseline.Concat(candidate.Where(other => other.Path != file.Path)).ToArray();
        if (otherFiles.Any(other => other.ReferencedNames.Any(typeNames.Contains)) ||
            root.DescendantTokens().Any(token => token.IsKind(SyntaxKind.IdentifierToken) &&
                !type.Span.Contains(token.Span) && typeNames.Contains(token.ValueText)))
            return file;

        var outsideClass = root.DescendantNodes(node => node.Span != type.Span)
            .Where(node => node.Span != type.Span).ToArray();
        var names = GetDeclaredNames(outsideClass).ToHashSet(StringComparer.Ordinal);
        // A source base may supply inherited members to the new class. Keep its declarations in reach.
        var baseNames = BaseNames(type.BaseList?.DescendantTokens() ?? []).ToHashSet(StringComparer.Ordinal);
        if (baseNames.Count != 0)
            names.UnionWith(otherFiles.SelectMany(other => other.DeclaredNames).Where(baseNames.Contains));
        return file with
        {
            DeclaredNames = names.ToArray(),
            Types = file.Types.Where(declaration => declaration.Owner != owner).ToArray(),
            TypeDeclarationShapes = GetDeclarationShapes(outsideClass)
        };
    }

    private static IEnumerable<string> BaseNames(IEnumerable<SyntaxToken> tokens) =>
        tokens.Where(token => token.IsKind(SyntaxKind.IdentifierToken)).Select(token => token.ValueText)
            .Where(name => !string.IsNullOrWhiteSpace(name));

    internal static bool WellFormed(SourceReaderFileFacts file)
    {
        static bool Names(string[]? values) => values is not null && values.All(value => !string.IsNullOrWhiteSpace(value));
        return file.ContentHash is { Length: 64 } && file.ContentHash.All(Uri.IsHexDigit) &&
            Names(file.DeclaredNames) && Names(file.ReferencedNames) && Names(file.TypeDeclarationShapes) &&
            Names(file.DeclaredSymbolIds) && file.Types is not null && file.Members is not null &&
            (file.AssemblyScans is not { } scans || scans.IsValid && scans.ContentHash == file.ContentHash) &&
            file.Types.All(type => type is not null && !string.IsNullOrWhiteSpace(type.Id) &&
                !string.IsNullOrWhiteSpace(type.Owner) && type.BaseList is not null) &&
            file.Members.All(member => member is not null && !string.IsNullOrWhiteSpace(member.Id) &&
                !string.IsNullOrWhiteSpace(member.Name) && !string.IsNullOrWhiteSpace(member.Owner) &&
                !string.IsNullOrWhiteSpace(member.TypeId) && member.Ordinal >= 0 && Names(member.Parameters) &&
                Names(member.Targets) && Names(member.InvocationTexts) && member.InvocationTargets is not null &&
                member.InvocationTexts.Length == member.InvocationTargets.Length &&
                member.InvocationTargets.All(Names));
    }

    internal static bool Valid(SourceReaderFileFacts[] files)
    {
        if (!HasUniquePaths(files) || !files.All(WellFormed)) return false;
        var ids = files.SelectMany(file => file.DeclaredSymbolIds).ToHashSet(StringComparer.Ordinal);
        var members = files.SelectMany(file => file.Members).ToArray();
        if (members.Select(member => member.Id).Distinct(StringComparer.Ordinal).Count() != members.Length ||
            ids.Any(id => id.StartsWith("untracked:", StringComparison.Ordinal))) return false;
        return files.SelectMany(file => file.Types).All(type => ids.Contains(type.Id) &&
                (type.BaseId is null || ids.Contains(type.BaseId))) &&
            files.SelectMany(file => file.Members).All(member => ids.Contains(member.Id) && ids.Contains(member.TypeId) &&
                member.Targets.Concat(member.InvocationTargets.SelectMany(targets => targets)).All(ids.Contains));
    }
}
