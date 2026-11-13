using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

// One checkout owns these trees; path mapping and guard discovery share them.
internal sealed class ImpactReplayTestSourceIndex
{
    internal IReadOnlyList<SyntaxTree> Trees { get; }
    internal IReadOnlyList<ImpactReplayChangedSourceRouting.Project>? Projects { get; }
    internal string? RepositoryRoot { get; }
    internal ImpactReplayTestSourceIndex(IEnumerable<string> files,
        IReadOnlyList<ImpactReplayChangedSourceRouting.Project>? projects = null, string? repositoryRoot = null)
        : this(files.Distinct(StringComparer.Ordinal).Select(file =>
            CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file)), projects, repositoryRoot)
    {
    }

    // The same parser inventory can be supplied in memory without filesystem or assembly discovery.
    internal ImpactReplayTestSourceIndex(IEnumerable<SyntaxTree> trees,
        IReadOnlyList<ImpactReplayChangedSourceRouting.Project>? projects = null, string? repositoryRoot = null)
    {
        Trees = trees.ToArray();
        Projects = projects;
        RepositoryRoot = repositoryRoot;
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
