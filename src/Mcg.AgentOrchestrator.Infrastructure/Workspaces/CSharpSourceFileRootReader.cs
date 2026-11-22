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
        FocusedEvidenceParsedRootCache.Store store, string path, Action? onParse = null) =>
        store.Get(path, static (sourcePath, bytes) =>
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            var tree = CSharpSyntaxTree.ParseText(text);
            FocusedEvidenceParsedRootCache.RecordParse(sourcePath);
            return tree.GetCompilationUnitRoot();
        }, onParse);
}
