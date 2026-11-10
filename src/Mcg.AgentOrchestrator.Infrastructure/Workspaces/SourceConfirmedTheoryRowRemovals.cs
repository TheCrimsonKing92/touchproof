using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

// A bounded source proof for literal string path lists expanded into original/upper-backslash
// MemberData rows. Never executes test code. Other producers retain the existing coverage floor.
internal static class SourceConfirmedTheoryRowRemovals
{
    internal static IReadOnlyDictionary<string, IReadOnlyList<int>> Resolve(
        TestDiscoverySnapshot? main, TestDiscoverySnapshot candidate,
        string? mainRoot, string candidateRoot, IReadOnlyList<string> declarations,
        IReadOnlyList<string>? mainDiscoveryArguments, IReadOnlyList<string> candidateDiscoveryArguments)
    {
        var result = new Dictionary<string, IReadOnlyList<int>>(StringComparer.OrdinalIgnoreCase);
        if (main?.Rows is not { } mainRows || candidate.Rows is not { } candidateRows ||
            string.IsNullOrWhiteSpace(mainRoot) || !UniqueRows(mainRows) || !UniqueRows(candidateRows))
            return result;

        foreach (var declaration in declarations.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var selected = mainRows.Select((row, index) => (Row: row, Index: index))
                .Where(item => Declares(declaration, item.Row)).ToArray();
            if (selected.Length == 0) continue;
            var first = selected[0].Row;
            var survivors = candidateRows.Where(row => row.ClassName == first.ClassName &&
                row.MethodName == first.MethodName && row.AssemblyName == first.AssemblyName).ToArray();
            if (selected.Any(item => !SameMethod(first, item.Row)) ||
                survivors.Any(row => !SameMethod(first, row)) || !UsablePath(first.SourceFile)) continue;

            var before = ReadSource(mainRoot, first.SourceFile!, first.ClassName!, first.MethodName!);
            var after = ReadSource(candidateRoot, first.SourceFile!, first.ClassName!, first.MethodName!);
            if (before is null || after is null || before.Method != after.Method ||
                before.DisplayName != after.DisplayName || before.Parameter != after.Parameter ||
                before.Arguments.Distinct(StringComparer.Ordinal).Count() != before.Arguments.Length ||
                after.Arguments.Distinct(StringComparer.Ordinal).Count() != after.Arguments.Length ||
                after.Arguments.Except(before.Arguments, StringComparer.Ordinal).Any()) continue;

            var removedArguments = before.Arguments.Except(after.Arguments, StringComparer.Ordinal).ToArray();
            if (removedArguments.Length == 0) continue;
            var mainMethodUid = XunitStringTheoryIdentity.MethodUid(mainDiscoveryArguments, mainRoot,
                first.AssemblyName!, first.ClassName!, first.MethodName!);
            var candidateMethodUid = XunitStringTheoryIdentity.MethodUid(candidateDiscoveryArguments, candidateRoot,
                first.AssemblyName!, first.ClassName!, first.MethodName!);
            if (mainMethodUid is null || candidateMethodUid is null ||
                !MatchesSource(before, selected.Select(item => item.Row).ToArray(), mainMethodUid) ||
                !MatchesSource(after, survivors, candidateMethodUid)) continue;
            // Bind full source arguments to the framework's UIDs in EACH generation. Assembly paths
            // can change UIDs between generations, so opaque cross-generation set subtraction is unsafe.
            var removedUids = removedArguments.Select(argument => XunitStringTheoryIdentity.RowUid(mainMethodUid, argument))
                .ToHashSet(StringComparer.Ordinal);
            var removed = selected.Where(item => removedUids.Contains(item.Row.Uid!)).ToArray();
            if (removed.Length != removedArguments.Length) continue;
            result[declaration] = removed.Select(item => item.Index).ToArray();
        }
        return result;
    }

    private static bool UniqueRows(IReadOnlyList<TestDiscoveryRow> rows) =>
        rows.All(row => !string.IsNullOrWhiteSpace(row.Uid)) &&
        rows.Select(row => row.Uid).Distinct(StringComparer.Ordinal).Count() == rows.Count;

    private static bool Declares(string declaration, TestDiscoveryRow row) =>
        row.ClassName is not null && row.MethodName is not null &&
        ((row.ClassName + "." + row.MethodName).Equals(declaration, StringComparison.Ordinal) ||
            (row.ClassName + "." + row.MethodName).EndsWith("." + declaration, StringComparison.Ordinal));

    private static bool SameMethod(TestDiscoveryRow left, TestDiscoveryRow right) =>
        left.AssemblyName is not null && left.AssemblyName == right.AssemblyName &&
        left.ClassName == right.ClassName && left.MethodName == right.MethodName &&
        left.SourceFile == right.SourceFile && left.IsSingleStringArgument && right.IsSingleStringArgument;

    private static bool UsablePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path) &&
        !path.Replace('\\', '/').Split('/').Any(part => part is ".." or ".");

    private static bool MatchesSource(SourceRows source, TestDiscoveryRow[] rows, string methodUid)
    {
        if (rows.Length != source.Arguments.Length) return false;
        var byUid = rows.ToDictionary(row => row.Uid!, StringComparer.Ordinal);
        foreach (var argument in source.Arguments)
        {
            if (!byUid.TryGetValue(XunitStringTheoryIdentity.RowUid(methodUid, argument), out var row)) return false;
            var escaped = argument.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var prefix = source.DisplayName + "(" + source.Parameter + ": \"";
            var name = row.DisplayName;
            if (!(name == prefix + escaped + "\")" ||
                (name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith("\"···)", StringComparison.Ordinal) &&
                    name.Length > prefix.Length + 5 && escaped.StartsWith(
                        name[prefix.Length..^5], StringComparison.Ordinal)))) return false;
        }
        return true;
    }

    private sealed record SourceRows(string Method, string DisplayName, string Parameter, string[] Arguments);

    private static SourceRows? ReadSource(string root, string file, string className, string methodName)
    {
        try
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, file)));
            if (tree.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)) return null;
            var types = tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>().Where(type =>
                string.Join(".", type.AncestorsAndSelf().Reverse().Select(node => node switch
                {
                    BaseNamespaceDeclarationSyntax ns => ns.Name.ToString(),
                    TypeDeclarationSyntax containing => containing.Identifier.ValueText,
                    _ => null
                }).Where(name => name is not null)) == className).ToArray();
            if (types.Length != 1) return null;
            var methods = types[0].Members.OfType<MethodDeclarationSyntax>()
                .Where(method => method.Identifier.ValueText == methodName).ToArray();
            if (methods.Length != 1 || methods[0].ParameterList.Parameters.Count != 1 ||
                methods[0].ParameterList.Parameters[0].Type?.ToString() != "string") return null;
            var theory = methods[0];
            var attributes = theory.AttributeLists.SelectMany(list => list.Attributes).ToArray();
            var theories = attributes.Where(attribute => AttributeName(attribute) is "Theory" or "TheoryAttribute").ToArray();
            var data = attributes.Where(attribute => AttributeName(attribute) is "MemberData" or "MemberDataAttribute").ToArray();
            if (attributes.Length != 2 || theories.Length != 1 || data.Length != 1 ||
                data[0].ArgumentList?.Arguments.Count != 1 ||
                data[0].ArgumentList!.Arguments[0].Expression is not InvocationExpressionSyntax memberName ||
                memberName.Expression.ToString() != "nameof" || memberName.ArgumentList.Arguments.Count != 1 ||
                memberName.ArgumentList.Arguments[0].Expression is not IdentifierNameSyntax producerName) return null;
            var producers = types[0].Members.OfType<MethodDeclarationSyntax>()
                .Where(method => method.Identifier.ValueText == producerName.Identifier.ValueText).ToArray();
            if (producers.Length != 1 || !producers[0].Modifiers.Any(SyntaxKind.StaticKeyword) ||
                producers[0].ParameterList.Parameters.Count != 0 || producers[0].Body is not { } body ||
                body.Statements.Count is < 2 or > 3 || body.Statements[^1] is not ForEachStatementSyntax loop ||
                loop.Statement is not BlockSyntax loopBody || loopBody.Statements.Count != 2) return null;
            var variable = loop.Identifier.ValueText;
            var expectedLoop = SyntaxFactory.ParseStatement($$"""
                foreach (var {{variable}} in paths)
                {
                    yield return [{{variable}}];
                    yield return [{{variable}}.Replace('/', '\\').ToUpperInvariant()];
                }
                """) as ForEachStatementSyntax;
            if (!loopBody.IsEquivalentTo(expectedLoop!.Statement)) return null;
            var locals = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var statement in body.Statements.Take(body.Statements.Count - 1))
            {
                if (statement is not LocalDeclarationStatementSyntax local || local.Declaration.Variables.Count != 1 ||
                    local.Declaration.Variables[0] is not { Initializer.Value: { } initializer } value ||
                    !TryStrings(initializer, locals, out var strings)) return null;
                if (!locals.TryAdd(value.Identifier.ValueText, strings)) return null;
            }
            if (!TryStrings(loop.Expression, locals, out var paths)) return null;
            var displayName = className + "." + methodName;
            foreach (var argument in theories[0].ArgumentList?.Arguments ?? [])
            {
                if (argument.NameEquals?.Name.Identifier.ValueText != "DisplayName" ||
                    argument.Expression is not LiteralExpressionSyntax literal ||
                    !literal.IsKind(SyntaxKind.StringLiteralExpression)) return null;
                displayName = literal.Token.ValueText;
            }
            return new SourceRows(theory.NormalizeWhitespace().ToFullString(), displayName,
                theory.ParameterList.Parameters[0].Identifier.ValueText,
                paths.SelectMany(path => new[] { path, path.Replace('/', '\\').ToUpperInvariant() }).ToArray());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string AttributeName(AttributeSyntax attribute) => attribute.Name.ToString().Split('.').Last();

    // Deliberately closed grammar: literal arrays, prior locals, prefix Select, and Concat.
    private static bool TryStrings(ExpressionSyntax expression, Dictionary<string, string[]> locals, out string[] strings)
    {
        strings = [];
        if (expression is IdentifierNameSyntax name)
        {
            if (!locals.TryGetValue(name.Identifier.ValueText, out var values)) return false;
            strings = values;
            return true;
        }
        IEnumerable<ExpressionSyntax>? elements = expression switch
        {
            CollectionExpressionSyntax collection when collection.Elements.All(item => item is ExpressionElementSyntax) =>
                collection.Elements.Cast<ExpressionElementSyntax>().Select(item => item.Expression),
            ImplicitArrayCreationExpressionSyntax array => array.Initializer.Expressions,
            ArrayCreationExpressionSyntax { Initializer: { } initializer } => initializer.Expressions,
            _ => null
        };
        if (elements is not null)
        {
            var values = elements.ToArray();
            if (values.Any(value => value is not LiteralExpressionSyntax literal || !literal.IsKind(SyntaxKind.StringLiteralExpression))) return false;
            strings = values.Cast<LiteralExpressionSyntax>().Select(value => value.Token.ValueText).ToArray();
            return true;
        }
        if (expression is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } invocation ||
            invocation.ArgumentList.Arguments.Count != 1 || !TryStrings(access.Expression, locals, out var input)) return false;
        var argument = invocation.ArgumentList.Arguments[0].Expression;
        if (access.Name.Identifier.ValueText == "Concat" && TryStrings(argument, locals, out var tail))
        {
            strings = [.. input, .. tail];
            return true;
        }
        if (access.Name.Identifier.ValueText == "Select" && argument is SimpleLambdaExpressionSyntax lambda &&
            lambda.Body is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.AddExpression) &&
            binary.Left is LiteralExpressionSyntax prefix && prefix.IsKind(SyntaxKind.StringLiteralExpression) &&
            binary.Right is IdentifierNameSyntax parameter && parameter.Identifier.ValueText == lambda.Parameter.Identifier.ValueText)
        {
            strings = input.Select(value => prefix.Token.ValueText + value).ToArray();
            return true;
        }
        return false;
    }
}
