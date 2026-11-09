using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// One checkout owns these trees; path mapping and guard discovery share them.
internal sealed class ImpactReplayTestSourceIndex
{
    internal IReadOnlyList<SyntaxTree> Trees { get; }
    internal ImpactReplayTestSourceIndex(IEnumerable<string> files)
    {
        Trees = files.Distinct(StringComparer.Ordinal).Select(file =>
            CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file)).ToArray();
        foreach (var tree in Trees)
            if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
                throw new InvalidDataException($"Cannot parse test source '{tree.FilePath}'.");
    }

    internal static string ClassName(ClassDeclarationSyntax declaration)
    {
        var space = string.Join(".", declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse().Select(node => node.Name.ToString()));
        var name = string.Join("+", declaration.AncestorsAndSelf().OfType<ClassDeclarationSyntax>()
            .Reverse().Select(node => node.Identifier.ValueText));
        return space.Length == 0 ? name : space + "." + name;
    }
}
