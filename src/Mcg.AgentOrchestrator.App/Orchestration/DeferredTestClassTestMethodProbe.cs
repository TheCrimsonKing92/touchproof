using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A probe belongs to one deferred-name resolution, never to process-wide candidate state.
// The ancestry resolver checks directly declared methods and resolves source identities,
// including aliases and partial declarations; it does not count inherited test methods.
internal sealed class DeferredTestClassTestMethodProbe
{
    private readonly Dictionary<string, HashSet<string>> _testClasses = new(StringComparer.OrdinalIgnoreCase);

    internal bool DeclaresTestMethod(string projectDirectory, string className)
    {
        if (!_testClasses.TryGetValue(projectDirectory, out var classes))
        {
            var files = DeveloperDeferredTestSelections.EnumerateSourceFiles(projectDirectory).ToArray();
            // Reject the common helper case without parsing the whole project. A partial
            // declaration needs the index because another file may own its test method.
            var ownDeclarations = files.Where(path => Path.GetFileName(path)
                    .Equals(className + ".cs", StringComparison.OrdinalIgnoreCase))
                .Select(Read).OfType<CompilationUnitSyntax>()
                .SelectMany(root => root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                .Where(type => type.Identifier.ValueText == className).ToArray();
            if (ownDeclarations.Length == 0 || ownDeclarations.All(type =>
                    !type.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PartialKeyword)) &&
                    !type.Members.OfType<MethodDeclarationSyntax>().Any(method => method.AttributeLists.Count > 0)))
                return false;
            classes = AcceptanceTestClassSourceScannerFactAncestry.Resolve(files.Select(Read)
                .OfType<CompilationUnitSyntax>().Select(AcceptanceTestClassSourceScannerFactAncestry.Extract));
            _testClasses.Add(projectDirectory, classes);
        }
        return classes.Any(name => name.Split(['.', '+'])[^1] == className);
    }

    private static CompilationUnitSyntax? Read(string path)
    {
        try
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetCompilationUnitRoot();
            return root.ContainsDiagnostics ? null : root;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
