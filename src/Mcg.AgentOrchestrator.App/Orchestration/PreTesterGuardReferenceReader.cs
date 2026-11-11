using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Mcg.AgentOrchestrator.App.Orchestration.PreTesterChangedFileGuardClasses;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Syntax references belong to a source revision; matching paths belong to each selection request.
internal static class PreTesterGuardReferenceReader
{
    internal sealed record GuardReference(string Class, string Kind, string[] Path,
        string Pattern = "*", bool Recursive = false);
    private sealed record Binding(ExpressionSyntax Expression, MethodDeclarationSyntax Caller);

    internal static GuardReference[] Read(string file)
    {
        var source = File.ReadAllText(file);
        if (!source.Contains("ReadAllText", StringComparison.Ordinal) &&
            !source.Contains("GetFiles", StringComparison.Ordinal) &&
            !source.Contains("EnumerateFiles", StringComparison.Ordinal)) return [];
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var references = new List<GuardReference>();
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(IsTest))
        {
            var declaringClass = method.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            if (declaringClass is null) continue;
            Collect(method, declaringClass.Identifier.ValueText, null, references);
            foreach (var call in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = HelperName(call, declaringClass.Identifier.ValueText);
                if (name is null) continue;
                foreach (var helper in declaringClass.Members.OfType<MethodDeclarationSyntax>()
                             .Where(candidate => candidate.Identifier.ValueText == name && candidate != method))
                {
                    var bindings = Bind(call, helper, method);
                    if (bindings is not null)
                        // Collect reads/scans only: calls within this helper are never followed.
                        Collect(helper, declaringClass.Identifier.ValueText, bindings, references);
                }
            }
        }
        return references.ToArray();
    }

    private static void Collect(MethodDeclarationSyntax method, string testClass,
        IReadOnlyDictionary<string, Binding>? bindings, List<GuardReference> references)
    {
        foreach (var call in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var read = IsCall(call, "File.ReadAllText", "System.IO.File.ReadAllText");
            var snapshotScan = IsSnapshotScan(call, method);
            var scan = snapshotScan || IsCall(call, "Directory.GetFiles", "System.IO.Directory.GetFiles",
                "Directory.EnumerateFiles", "System.IO.Directory.EnumerateFiles");
            if ((!read && !scan) || call.ArgumentList.Arguments.Count == 0) continue;
            var path = scan ? Argument(call, 0, "path") : call.ArgumentList.Arguments[0].Expression;
            if (path is null) continue;
            var literals = LiteralSequence(path, method, 0, bindings).ToArray();
            var first = Array.FindIndex(literals, literal => literal is not null);
            if (first < 0 || literals.Skip(first).Any(literal => literal is null)) continue;
            var segments = Normalize(string.Join('/', literals.Skip(first))).Split('/');
            if (segments.Length == 0 || segments.Any(segment => segment is "" or "..")) continue;
            if (read)
                references.Add(new(testClass, bindings is null ? "read" : "helper-read", segments));
            else
            {
                var patternExpression = Argument(call, 1, "searchPattern");
                var pattern = snapshotScan ? "*.cs" : patternExpression is null ? "*" :
                    Scalar(patternExpression, method, bindings) is LiteralExpressionSyntax literal &&
                    literal.IsKind(SyntaxKind.StringLiteralExpression) ? literal.Token.ValueText : "*";
                if (pattern.Contains('/') || pattern.Contains('\\')) continue;
                var option = Argument(call, 2, "searchOption");
                var optionName = option is null ? "" : Scalar(option, method, bindings).ToString()
                    .Replace("global::", "", StringComparison.Ordinal);
                references.Add(new(testClass, "scan", segments, pattern,
                    snapshotScan || optionName is "SearchOption.AllDirectories" or "System.IO.SearchOption.AllDirectories"));
            }
        }
    }

    private static bool IsSnapshotScan(InvocationExpressionSyntax call, MethodDeclarationSyntax method)
    {
        if (call.Expression is not MemberAccessExpressionSyntax
            { Expression: IdentifierNameSyntax receiver, Name.Identifier.ValueText: "EnumerateFiles" }) return false;
        var locals = method.DescendantNodes().OfType<VariableDeclaratorSyntax>().Where(variable =>
            variable.Identifier.ValueText == receiver.Identifier.ValueText && variable.SpanStart < call.SpanStart).ToArray();
        return !Reassigned(method, receiver.Identifier.ValueText) && locals is [{ Initializer.Value: InvocationExpressionSyntax get }] &&
            IsCall(get, "RepositorySourceSnapshotCache.Get");
    }

    private static string? HelperName(InvocationExpressionSyntax call, string className) => call.Expression switch
    {
        SimpleNameSyntax name => name.Identifier.ValueText,
        MemberAccessExpressionSyntax member when member.Expression is ThisExpressionSyntax ||
            member.Expression is IdentifierNameSyntax identifier && identifier.Identifier.ValueText == className
            => member.Name.Identifier.ValueText,
        _ => null
    };

    private static Dictionary<string, Binding>? Bind(InvocationExpressionSyntax call,
        MethodDeclarationSyntax helper, MethodDeclarationSyntax caller)
    {
        var parameters = helper.ParameterList.Parameters;
        if (call.ArgumentList.Arguments.Count > parameters.Count ||
            parameters.Any(parameter => parameter.Modifiers.Any(SyntaxKind.ParamsKeyword))) return null;
        var bindings = new Dictionary<string, Binding>(StringComparer.Ordinal);
        var position = 0;
        foreach (var argument in call.ArgumentList.Arguments)
        {
            var name = argument.NameColon?.Name.Identifier.ValueText ?? parameters[position].Identifier.ValueText;
            if (!parameters.Any(parameter => parameter.Identifier.ValueText == name) ||
                !bindings.TryAdd(name, new(argument.Expression, caller))) return null;
            position++;
        }
        foreach (var parameter in parameters)
        {
            if (bindings.ContainsKey(parameter.Identifier.ValueText)) continue;
            if (parameter.Default is null) return null;
            bindings.Add(parameter.Identifier.ValueText, new(parameter.Default.Value, helper));
        }
        return bindings;
    }

    private static ExpressionSyntax? Argument(InvocationExpressionSyntax call, int position, string name)
    {
        var named = call.ArgumentList.Arguments.FirstOrDefault(argument =>
            argument.NameColon?.Name.Identifier.ValueText == name);
        if (named is not null) return named.Expression;
        return position < call.ArgumentList.Arguments.Count &&
               call.ArgumentList.Arguments[position].NameColon is null
            ? call.ArgumentList.Arguments[position].Expression : null;
    }

    private static bool IsTest(MethodDeclarationSyntax method) => method.AttributeLists
        .SelectMany(list => list.Attributes).Any(attribute =>
        {
            var name = attribute.Name.ToString().Split('.').Last();
            if (name.EndsWith("Attribute", StringComparison.Ordinal)) name = name[..^9];
            return name.EndsWith("Fact", StringComparison.Ordinal) || name.EndsWith("Theory", StringComparison.Ordinal);
        });

    private static bool IsCall(InvocationExpressionSyntax call, params string[] names) =>
        names.Contains(call.Expression.ToString().Replace("global::", "", StringComparison.Ordinal), StringComparer.Ordinal);

    // Patterns and options use the same call-site and local bindings as paths, but stay scalar.
    private static ExpressionSyntax Scalar(ExpressionSyntax expression, MethodDeclarationSyntax method,
        IReadOnlyDictionary<string, Binding>? bindings, int depth = 0)
    {
        if (depth > 16) return expression;
        if (expression is ParenthesizedExpressionSyntax parenthesized)
            return Scalar(parenthesized.Expression, method, bindings, depth + 1);
        if (expression is IdentifierNameSyntax identifier)
        {
            var name = identifier.Identifier.ValueText;
            if (Reassigned(method, name)) return expression;
            var variables = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Where(variable => variable.Identifier.ValueText == name).ToArray();
            if (variables is [{ Initializer: not null } variable] && variable.SpanStart < expression.SpanStart)
                return Scalar(variable.Initializer.Value, method, bindings, depth + 1);
            if (bindings is not null && bindings.TryGetValue(name, out var binding))
                return Scalar(binding.Expression, binding.Caller, null, depth + 1);
        }
        return expression;
    }

    private static bool Reassigned(MethodDeclarationSyntax method, string name) =>
        method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Any(assignment => assignment.Left is IdentifierNameSyntax target && target.Identifier.ValueText == name);

    private static IEnumerable<string?> LiteralSequence(ExpressionSyntax expression,
        MethodDeclarationSyntax method, int depth, IReadOnlyDictionary<string, Binding>? bindings = null)
    {
        if (depth > 16) return [null];
        if (expression is ParenthesizedExpressionSyntax parenthesized)
            return LiteralSequence(parenthesized.Expression, method, depth + 1, bindings);
        if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            return [literal.Token.ValueText];
        if (expression is InvocationExpressionSyntax combine && IsCall(combine, "Path.Combine", "System.IO.Path.Combine"))
            return combine.ArgumentList.Arguments.SelectMany(argument =>
                LiteralSequence(argument.Expression, method, depth + 1, bindings));
        if (expression is IdentifierNameSyntax identifier)
        {
            var variables = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Where(variable => variable.Identifier.ValueText == identifier.Identifier.ValueText).ToArray();
            var reassigned = Reassigned(method, identifier.Identifier.ValueText);
            if (!reassigned && variables is [{ Initializer: not null } variable] && variable.SpanStart < expression.SpanStart)
                return LiteralSequence(variable.Initializer.Value, method, depth + 1, bindings);
            if (!reassigned && bindings is not null && bindings.TryGetValue(identifier.Identifier.ValueText, out var binding))
                return LiteralSequence(binding.Expression, binding.Caller, depth + 1);
        }
        return [null];
    }
}
