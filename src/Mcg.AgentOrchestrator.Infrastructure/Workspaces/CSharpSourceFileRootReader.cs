using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class CSharpSourceFileRootReader
{
    internal static CompilationUnitSyntax Read(string path, Action? onParse = null) =>
        ReadFile(path, onParse).Root;

    internal static FocusedEvidenceParsedRootCache.ParsedFile ReadFile(string path, Action? onParse = null) =>
        ReadFile(FocusedEvidenceParsedRootCache.Shared, path, onParse);

    internal static FocusedEvidenceParsedRootCache.ParsedFile ReadFile(
        FocusedEvidenceParsedRootCache.Store store, string path, Action? onParse = null)
    {
        var parsedForRequest = false;
        var file = store.Get(path, (sourcePath, bytes) =>
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            var tree = CSharpSyntaxTree.ParseText(text);
            FocusedEvidenceParsedRootCache.RecordParse(sourcePath);
            parsedForRequest = true;
            return tree.GetCompilationUnitRoot();
        });

        // Notify the parse-owning request after the shared loads have completed.
        if (parsedForRequest) onParse?.Invoke();
        return file;
    }
}
