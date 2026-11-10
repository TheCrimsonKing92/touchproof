using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Test infrastructure: the compilation supplies assembly identity, while target flow is deliberately
// bounded to typeof, this.GetType(), and single-assignment local aliases. No helper is executed.
internal static class ProductionMemberReflectionScanner
{
    internal sealed record Lookup(string Path, string Callable, string Api, string Member,
        string Receiver, string Target, string Classification, string Resolution)
    {
        public string Identity => $"{Path}|{Callable}|{Api}|{Member}|{Receiver}";
        public override string ToString() =>
            $"{Identity}: {Classification}; target={Target}; {Resolution}";
    }

    internal sealed record Exemption(string Path, string Callable, string Api, string Member, string Receiver, string Reason)
    {
        public string Identity => $"{Path}|{Callable}|{Api}|{Member}|{Receiver}";
    }

    internal sealed record Result(IReadOnlyList<Lookup> Lookups, IReadOnlyList<string> Diagnostics);

    internal static Exemption[] ReadExemptions(string json) =>
        JsonSerializer.Deserialize<Exemption[]>(json, new JsonSerializerOptions
        {
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
        }) ?? throw new InvalidDataException("Reflection exemptions must be a JSON array.");

    internal static Result Scan(Compilation compilation, IEnumerable<SyntaxTree> testTrees,
        ISet<string> productionAssemblies, IReadOnlyList<Exemption> exemptions)
    {
        var lookups = new List<Lookup>();
        foreach (var tree in testTrees)
        {
            var model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
            foreach (var call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                ExpressionSyntax receiver;
                string api;
                if (call.Expression is MemberAccessExpressionSyntax member)
                    (receiver, api) = (member.Expression, member.Name.Identifier.ValueText);
                else if (call.Expression is MemberBindingExpressionSyntax binding &&
                         binding.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault(access =>
                             access.WhenNotNull.Span.Contains(binding.Span)) is { } conditional)
                    (receiver, api) = (conditional.Expression, binding.Name.Identifier.ValueText);
                else continue;
                if (api is not ("GetMethod" or "GetProperty") || call.ArgumentList.Arguments.Count == 0) continue;

                var symbol = model.GetSymbolInfo(call).Symbol as IMethodSymbol;
                var receiverType = model.GetTypeInfo(receiver).Type;
                // System.Text.Json and other identically named APIs are outside this policy.
                if (symbol is not null && symbol.ContainingType.ToDisplayString() != "System.Type") continue;
                if (symbol is null && receiverType is not null && receiverType.TypeKind != TypeKind.Error &&
                    receiverType.ToDisplayString() != "System.Type" && receiverType.TypeKind != TypeKind.Dynamic) continue;

                var nameArgument = call.ArgumentList.Arguments.FirstOrDefault(argument =>
                    argument.NameColon?.Name.Identifier.ValueText == "name") ?? call.ArgumentList.Arguments[0];
                var target = Resolve(receiver, model, call, 0);
                var classification = target is null ? "unresolved-target" :
                    productionAssemblies.Contains(target.ContainingAssembly.Identity.Name) ? "production-member" : "excluded-target";
                var constant = model.GetConstantValue(nameArgument.Expression);
                var name = constant is { HasValue: true, Value: string value } ? value :
                    nameArgument.Expression.WithoutTrivia().NormalizeWhitespace().ToFullString();
                lookups.Add(new(tree.FilePath.Replace('\\', '/'), Callable(call), api, name,
                    receiver.WithoutTrivia().NormalizeWhitespace().ToFullString(),
                    target?.ToDisplayString() ?? receiver.WithoutTrivia().NormalizeWhitespace().ToFullString(),
                    classification, target is null
                        ? "Cannot resolve the target within bounded local flow; provide classification evidence or an exact reasoned exemption."
                        : $"Resolved assembly {target.ContainingAssembly.Identity.Name}."));
            }
        }
        var diagnostics = new List<string>();
        var valid = new Dictionary<string, Exemption>(StringComparer.Ordinal);
        foreach (var exemption in exemptions)
        {
            if (exemption is null || string.IsNullOrWhiteSpace(exemption.Path) ||
                !exemption.Path.StartsWith("tests/", StringComparison.Ordinal) || !exemption.Path.EndsWith(".cs", StringComparison.Ordinal) ||
                exemption.Path.Contains('\\') || exemption.Path.Split('/').Any(part => part is ".." or "." or "") ||
                string.IsNullOrWhiteSpace(exemption.Callable) || string.IsNullOrWhiteSpace(exemption.Member) ||
                string.IsNullOrWhiteSpace(exemption.Receiver) ||
                exemption.Api is not ("GetMethod" or "GetProperty") || string.IsNullOrWhiteSpace(exemption.Reason) ||
                new[] { exemption.Path, exemption.Callable, exemption.Member, exemption.Receiver }.Any(part => part.IndexOfAny(['*', '?', '|']) >= 0))
            {
                diagnostics.Add($"Malformed or reasonless reflection exemption: {exemption?.Identity ?? "null"}.");
                continue;
            }
            if (!valid.TryAdd(exemption.Identity, exemption))
                diagnostics.Add($"Duplicate reflection exemption: {exemption.Identity}.");
        }
        foreach (var exemption in valid.Values)
        {
            var matches = lookups.Where(lookup => lookup.Identity == exemption.Identity &&
                lookup.Classification != "excluded-target").ToArray();
            if (matches.Length != 1)
                diagnostics.Add($"{(matches.Length == 0 ? "Stale" : "Ambiguous")} reflection exemption: {exemption.Identity} ({matches.Length} matches).");
        }
        foreach (var lookup in lookups.Where(lookup => lookup.Classification != "excluded-target"))
            if (!valid.ContainsKey(lookup.Identity)) diagnostics.Add(lookup.ToString());
        return new(lookups, diagnostics);
    }

    private static string Callable(SyntaxNode call)
    {
        // Signature distinguishes overloads; local functions retain their enclosing method identity.
        var method = call.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
        var type = string.Join('.', call.Ancestors().OfType<TypeDeclarationSyntax>().Reverse()
            .Select(declaration => declaration.Identifier.ValueText));
        var member = method switch
        {
            MethodDeclarationSyntax declaration => declaration.Identifier.ValueText,
            ConstructorDeclarationSyntax => ".ctor",
            _ => call.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault() switch
            {
                PropertyDeclarationSyntax property => property.Identifier.ValueText,
                FieldDeclarationSyntax field => field.Declaration.Variables.First().Identifier.ValueText,
                _ => "<initializer>"
            }
        };
        var parameters = method is null ? "" : string.Join(',', method.ParameterList.Parameters.Select(parameter =>
            parameter.Type?.WithoutTrivia().NormalizeWhitespace().ToFullString()));
        return $"{type}.{member}({parameters})";
    }

    private static ITypeSymbol? Resolve(ExpressionSyntax expression, SemanticModel model,
        InvocationExpressionSyntax call, int depth)
    {
        if (depth > 16) return null;
        if (expression is ParenthesizedExpressionSyntax parentheses) return Resolve(parentheses.Expression, model, call, depth + 1);
        if (expression is PostfixUnaryExpressionSyntax suppress && suppress.IsKind(SyntaxKind.SuppressNullableWarningExpression))
            return Resolve(suppress.Operand, model, call, depth + 1);
        if (expression is TypeOfExpressionSyntax typeOf)
        {
            var type = model.GetTypeInfo(typeOf.Type).Type;
            return type is null || type.TypeKind == TypeKind.Error ? null : type;
        }
        if (expression is InvocationExpressionSyntax getType && getType.ArgumentList.Arguments.Count == 0 &&
            (getType.Expression is IdentifierNameSyntax { Identifier.ValueText: "GetType" } ||
             getType.Expression is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name.Identifier.ValueText: "GetType" }))
            return model.GetEnclosingSymbol(call.SpanStart)?.ContainingType;
        if (expression is IdentifierNameSyntax && model.GetSymbolInfo(expression).Symbol is ILocalSymbol local &&
            local.DeclaringSyntaxReferences is [var reference] && reference.GetSyntax() is VariableDeclaratorSyntax { Initializer: not null } variable &&
            variable.SpanStart < expression.SpanStart)
        {
            var scope = variable.Ancestors().FirstOrDefault(node => node is BaseMethodDeclarationSyntax or AnonymousFunctionExpressionSyntax);
            if (scope is null || scope.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(assignment =>
                    SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Left).Symbol, local)) ||
                scope.DescendantNodes().OfType<ArgumentSyntax>().Any(argument => argument.RefKindKeyword.RawKind != 0 &&
                    SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(argument.Expression).Symbol, local))) return null;
            return Resolve(variable.Initializer.Value, model, call, depth + 1);
        }
        return null;
    }

    internal static Result ScanRepository(RepositorySourceSnapshot snapshot, IEnumerable<string> testFiles,
        IReadOnlyList<Exemption> exemptions)
    {
        // Candidate assemblies supply symbol/assembly identity, not runtime target execution. TPA
        // includes framework references; output references include project and third-party libraries.
        var referencePaths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var references = referencePaths.Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        // Exclusions describe content inside this repository, not its parent checkout location.
        var files = testFiles.Where(path => !SourceGuardFileFilter.WorkspaceAndReports.Excludes(
            Path.GetRelativePath(snapshot.Root, path))).ToArray();
        var trees = files.Select(path => snapshot.GetSyntaxTree(path).WithFilePath(
            Path.GetRelativePath(snapshot.Root, path).Replace('\\', '/'))).ToArray();
        var globals = CSharpSyntaxTree.ParseText("""
            global using System;
            global using System.IO;
            global using System.Linq;
            global using System.Collections.Generic;
            global using System.Threading;
            global using System.Threading.Tasks;
            global using Xunit;
            """);
        // Preserve the existing friend-assembly contract so inferred return types from internal
        // production APIs bind just as they do in the test project (including non-reflection APIs).
        var compilation = CSharpCompilation.Create(typeof(ProductionMemberReflectionScanner).Assembly.GetName().Name!,
            trees.Append(globals), references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        // Names come from actual production projects, never a namespace spelling heuristic.
        var assemblies = Directory.EnumerateFiles(Path.Combine(snapshot.Root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(path => !SourceGuardFileFilter.WorkspaceAndReports.Excludes(Path.GetRelativePath(snapshot.Root, path)))
            .Select(path => System.Xml.Linq.XDocument.Load(path).Descendants().FirstOrDefault(element =>
                element.Name.LocalName == "AssemblyName")?.Value ?? Path.GetFileNameWithoutExtension(path))
            .ToHashSet(StringComparer.Ordinal);
        if (assemblies.Count == 0 || trees.Length == 0) throw new InvalidDataException("Reflection guard requires production projects and test sources.");
        return Scan(compilation, trees, assemblies, exemptions);
    }
}
