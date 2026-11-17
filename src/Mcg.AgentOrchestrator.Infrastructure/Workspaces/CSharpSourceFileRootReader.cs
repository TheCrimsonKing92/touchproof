using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class CSharpSourceFileRootReader
{
    internal static CompilationUnitSyntax Read(string path, Action? onParse = null) =>
        ReadFile(path, onParse).Root;

    internal static FocusedEvidenceParsedRootCache.ParsedFile ReadFile(string path, Action? onParse = null) =>
        FocusedEvidenceParsedRootCache.Get(path, sourcePath =>
        {
            var text = File.ReadAllText(sourcePath);
            FocusedEvidenceParsedRootCache.RecordParse(sourcePath);
            onParse?.Invoke();
            return CSharpSyntaxTree.ParseText(text, path: sourcePath).GetCompilationUnitRoot();
        });
}
