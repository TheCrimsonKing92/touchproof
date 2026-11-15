using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

// A reader reaches both a root lookup and a repository-wide enumeration. Neither alone is sufficient.
internal static class WholeTreeSourceReaderDetector
{
    internal static HashSet<ISymbol> Read(Dictionary<ISymbol, HashSet<ISymbol>> edges,
        Dictionary<ISymbol, List<SyntaxNode>> declarations, CSharpCompilation compilation)
    {
        var roots = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var traversals = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var gitCallers = new HashSet<ISymbol>(declarations.Where(pair => pair.Value.Any(node =>
            node.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Any(creation =>
                creation.Type.ToString().Split('.').Last() is "Process" or "ProcessStartInfo") ||
            node.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(call =>
                call.Expression is MemberAccessExpressionSyntax access && Name(access.Expression) == "Process" &&
                access.Name.Identifier.ValueText == "Start"))).Select(pair => pair.Key), SymbolEqualityComparer.Default);
        Propagate(gitCallers, edges);
        foreach (var (member, nodes) in declarations)
        foreach (var node in nodes)
        {
            var calls = node.DescendantNodes().OfType<InvocationExpressionSyntax>().ToArray();
            if (calls.Any(call => call.Expression is MemberAccessExpressionSyntax access &&
                    access.Name.Identifier.ValueText == "Find" && Name(access.Expression) == "VerifiedRepositoryRoot"))
                roots.Add(member);
            if (calls.Any(call => IsWholeDirectoryTraversal(call, node, member, edges)) ||
                gitCallers.Contains(member) && GitThroughHelpers(member, node, new Dictionary<string, ExpressionSyntax>(),
                    new HashSet<ISymbol>(SymbolEqualityComparer.Default), gitCallers, declarations, compilation))
                traversals.Add(member);
        }
        Propagate(roots, edges);
        Propagate(traversals, edges);
        roots.IntersectWith(traversals);
        return roots;
    }

    private static bool GitThroughHelpers(ISymbol symbol, SyntaxNode member,
        Dictionary<string, ExpressionSyntax> bindings, HashSet<ISymbol> active, HashSet<ISymbol> gitCallers,
        Dictionary<ISymbol, List<SyntaxNode>> declarations, CSharpCompilation compilation)
    {
        if (!active.Add(symbol)) return false;
        try
        {
            var calls = member.DescendantNodes().OfType<InvocationExpressionSyntax>().ToArray();
            if (IsGitTraversal(member, calls, bindings)) return true;
            var model = compilation.GetSemanticModel(member.SyntaxTree);
            foreach (var call in calls)
            {
                var info = model.GetSymbolInfo(call);
                foreach (var target in info.Symbol is { } resolved ? new[] { resolved } : info.CandidateSymbols.ToArray())
                {
                    var canonical = target.OriginalDefinition;
                    if (canonical is not IMethodSymbol method || !gitCallers.Contains(canonical) ||
                        !declarations.TryGetValue(canonical, out var bodies)) continue;
                    var arguments = new Dictionary<string, ExpressionSyntax>(StringComparer.Ordinal);
                    for (var index = 0; index < call.ArgumentList.Arguments.Count; index++)
                    {
                        var argument = call.ArgumentList.Arguments[index];
                        var parameter = argument.NameColon is { } named
                            ? method.Parameters.FirstOrDefault(parameter => parameter.Name == named.Name.Identifier.ValueText)
                            : index < method.Parameters.Length ? method.Parameters[index] : null;
                        if (parameter is not null)
                            arguments[parameter.Name] = Resolve(argument.Expression, member, bindings);
                    }
                    if (bodies.Any(body => GitThroughHelpers(canonical, body, arguments, active,
                            gitCallers, declarations, compilation))) return true;
                }
            }
            return false;
        }
        finally { active.Remove(symbol); }
    }

    private static void Propagate(HashSet<ISymbol> readers, Dictionary<ISymbol, HashSet<ISymbol>> edges)
    {
        bool changed;
        do
        {
            changed = false;
            foreach (var (caller, targets) in edges)
                if (!readers.Contains(caller) && targets.Any(readers.Contains)) changed |= readers.Add(caller);
        } while (changed);
    }

    private static bool Reaches(ISymbol from, ISymbol target, Dictionary<ISymbol, HashSet<ISymbol>> edges)
    {
        var pending = new Stack<ISymbol>();
        var visited = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        pending.Push(from);
        while (pending.TryPop(out var current))
        {
            if (SymbolEqualityComparer.Default.Equals(current, target)) return true;
            if (visited.Add(current) && edges.TryGetValue(current, out var calls))
                foreach (var call in calls) pending.Push(call);
        }
        return false;
    }

    private static bool IsWholeDirectoryTraversal(InvocationExpressionSyntax call, SyntaxNode member,
        ISymbol symbol, Dictionary<ISymbol, HashSet<ISymbol>> edges)
    {
        if (call.Expression is not MemberAccessExpressionSyntax access || Name(access.Expression) != "Directory" ||
            access.Name.Identifier.ValueText is not ("EnumerateFiles" or "GetFiles" or
                "EnumerateFileSystemEntries" or "GetFileSystemEntries" or "EnumerateDirectories" or "GetDirectories")) return false;
        var arguments = call.ArgumentList.Arguments;
        if (arguments.Count == 0 || IsConfined(Resolve(arguments[0].Expression, member))) return false;
        if (arguments.Skip(1).Any(argument =>
            Resolve(argument.Expression, member).DescendantNodesAndSelf().Any(node =>
                node is MemberAccessExpressionSyntax option && Name(option.Expression) == "SearchOption" &&
                    option.Name.Identifier.ValueText == "AllDirectories" ||
                node is AssignmentExpressionSyntax assignment && Name(assignment.Left) == "RecurseSubdirectories" &&
                    Resolve(assignment.Right, member).IsKind(SyntaxKind.TrueLiteralExpression)))) return true;

        // Top-level file enumeration qualifies only when directory iteration calls back into this helper graph.
        return member.DescendantNodes().OfType<ForEachStatementSyntax>().Any(loop =>
            Resolve(loop.Expression, member) is InvocationExpressionSyntax directories &&
            directories.Expression is MemberAccessExpressionSyntax directoryAccess &&
            Name(directoryAccess.Expression) == "Directory" &&
            directoryAccess.Name.Identifier.ValueText is "EnumerateDirectories" or "GetDirectories" &&
            loop.Statement.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(recursiveCall =>
                edges.TryGetValue(symbol, out var targets) && targets.Any(target =>
                    target.Name == Name(recursiveCall.Expression) && Reaches(target, symbol, edges)) &&
                recursiveCall.ArgumentList.Arguments.Any(argument =>
                    argument.Expression is IdentifierNameSyntax identifier && identifier.Identifier.ValueText == loop.Identifier.ValueText)));
    }

    private static bool IsConfined(ExpressionSyntax expression) =>
        expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } call &&
        Name(access.Expression) == "Path" && access.Name.Identifier.ValueText is "Combine" or "Join" &&
        call.ArgumentList.Arguments.Skip(1).Any(argument => argument.Expression is LiteralExpressionSyntax literal &&
            literal.IsKind(SyntaxKind.StringLiteralExpression) && literal.Token.ValueText is not ("" or "."));

    private static bool IsGitTraversal(SyntaxNode member, InvocationExpressionSyntax[] calls,
        Dictionary<string, ExpressionSyntax> bindings)
    {
        // Require a process invocation; prose and unused command strings are not evidence of execution.
        if (calls.Any(call => call.Expression is MemberAccessExpressionSyntax access &&
            Name(access.Expression) == "Process" && access.Name.Identifier.ValueText == "Start" &&
            call.ArgumentList.Arguments.Count >= 2 &&
            IsGit(Resolve(call.ArgumentList.Arguments[0].Expression, member, bindings)) &&
            IsWholeGitCommand(Tokens(call.ArgumentList.Arguments[1].Expression, member, bindings)))) return true;

        foreach (var creation in member.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
                     .Where(creation => creation.Type.ToString().Split('.').Last() == "ProcessStartInfo"))
        {
            var variable = creation.Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault();
            if (variable is null) continue;
            var name = variable.Identifier.ValueText;
            var assignments = creation.Initializer?.Expressions.OfType<AssignmentExpressionSyntax>().ToArray() ?? [];
            var file = creation.ArgumentList?.Arguments.FirstOrDefault()?.Expression ??
                assignments.FirstOrDefault(assignment => Name(assignment.Left) == "FileName")?.Right;
            file ??= member.DescendantNodes().OfType<AssignmentExpressionSyntax>().FirstOrDefault(assignment =>
                assignment.Left.ToString() == name + ".FileName")?.Right;
            if (file is null || !IsGit(Resolve(file, member, bindings)) || !IsStarted(name, member, calls)) continue;
            var tokens = new List<string>();
            if (creation.ArgumentList?.Arguments.Count >= 2)
                tokens.AddRange(Tokens(creation.ArgumentList.Arguments[1].Expression, member, bindings));
            foreach (var assignment in assignments.Concat(member.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                         .Where(assignment => assignment.Left.ToString().StartsWith(name + ".", StringComparison.Ordinal))))
                if (Name(assignment.Left) is "Arguments" or "ArgumentList") tokens.AddRange(Tokens(assignment.Right, member, bindings));
            foreach (var call in calls.Where(call => call.Expression.ToString() == name + ".ArgumentList.Add"))
            {
                foreach (var argument in call.ArgumentList.Arguments)
                {
                    var loop = call.Ancestors().OfType<ForEachStatementSyntax>().FirstOrDefault();
                    tokens.AddRange(Tokens(loop is not null && argument.Expression.ToString() == loop.Identifier.ValueText
                        ? loop.Expression : argument.Expression, member, bindings));
                }
            }
            if (IsWholeGitCommand(tokens)) return true;
        }
        return false;
    }

    private static bool IsStarted(string info, SyntaxNode member, InvocationExpressionSyntax[] calls)
    {
        if (calls.Any(call => call.Expression is MemberAccessExpressionSyntax access &&
            Name(access.Expression) == "Process" && access.Name.Identifier.ValueText == "Start" &&
            call.ArgumentList.Arguments.Any(argument => argument.Expression.ToString() == info))) return true;
        return member.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Any(creation =>
            creation.Type.ToString().Split('.').Last() == "Process" &&
            creation.Initializer?.Expressions.OfType<AssignmentExpressionSyntax>().Any(assignment =>
                Name(assignment.Left) == "StartInfo" && assignment.Right.ToString() == info) == true &&
            creation.Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault() is { } process &&
            calls.Any(call => call.Expression.ToString() == process.Identifier.ValueText + ".Start"));
    }

    private static bool IsGit(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax literal => literal.Token.ValueText is "git" or "git.exe",
        ConditionalExpressionSyntax conditional => IsGit(conditional.WhenTrue) && IsGit(conditional.WhenFalse),
        _ => false
    };

    private static bool IsWholeGitCommand(IEnumerable<string> tokens)
    {
        var arguments = tokens.ToArray();
        var command = 0;
        while (command < arguments.Length && arguments[command].StartsWith('-'))
            command += arguments[command] is "-c" or "-C" ? 2 : 1;
        if (command >= arguments.Length || arguments[command] != "ls-files") return false;
        var pathspecs = false;
        foreach (var argument in arguments.Skip(command + 1))
        {
            if (argument == "--") { pathspecs = true; continue; }
            if (!pathspecs && argument.StartsWith('-')) continue;
            // A repository-wide filename filter still scans the tree; a named file or subtree does not.
            var pattern = argument.StartsWith("**/", StringComparison.Ordinal) ? argument[3..] : argument;
            if (pattern.Contains('/') || pattern.Contains('\\') || !pattern.Contains('*') && !pattern.Contains('?')) return false;
        }
        return true;
    }

    private static IEnumerable<string> Tokens(ExpressionSyntax expression, SyntaxNode member,
        Dictionary<string, ExpressionSyntax> bindings)
    {
        expression = Resolve(expression, member, bindings);
        if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            return literal.Token.ValueText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        // Only inspect literal collections, never arbitrary calls or diagnostic interpolations.
        if (expression is ArrayCreationExpressionSyntax or ImplicitArrayCreationExpressionSyntax or
            CollectionExpressionSyntax or InitializerExpressionSyntax)
            return expression.DescendantNodes().OfType<LiteralExpressionSyntax>()
                .Where(literal => literal.IsKind(SyntaxKind.StringLiteralExpression)).Select(literal => literal.Token.ValueText);
        return [];
    }

    private static ExpressionSyntax Resolve(ExpressionSyntax expression, SyntaxNode member,
        Dictionary<string, ExpressionSyntax>? bindings = null)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (expression is IdentifierNameSyntax identifier && visited.Add(identifier.Identifier.ValueText))
        {
            if (bindings?.TryGetValue(identifier.Identifier.ValueText, out var bound) == true)
                return bound;
            var value = member.DescendantNodes().OfType<VariableDeclaratorSyntax>().FirstOrDefault(variable =>
                variable.Identifier.ValueText == identifier.Identifier.ValueText)?.Initializer?.Value;
            if (value is null) break;
            expression = value;
        }
        return expression;
    }

    private static string Name(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        SimpleNameSyntax name => name.Identifier.ValueText,
        _ => ""
    };
}
