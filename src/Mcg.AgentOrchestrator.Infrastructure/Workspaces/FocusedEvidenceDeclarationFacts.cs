using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record FocusedEvidenceDeclarationFacts(ImmutableArray<FocusedEvidenceDeclarationFacts.DeclaredType> Types)
{
    internal sealed record DeclaredType(string SimpleName, string QualifiedName,
        [property: JsonRequired] bool IsClassOrNonStructRecord, ImmutableArray<string> TestMethodNames)
    {
        internal FocusedEvidenceParsedRootCache.DeclaredTypeName Name => new(SimpleName, QualifiedName);
    }

    internal static FocusedEvidenceDeclarationFacts Extract(CompilationUnitSyntax root) =>
        new(root.DescendantNodes().OfType<TypeDeclarationSyntax>().Select(ExtractType).ToImmutableArray());

    private static DeclaredType ExtractType(TypeDeclarationSyntax type)
    {
        var name = FocusedEvidenceParsedRootCache.GetDeclaredTypeName(type);
        var isClassOrNonStructRecord = type is ClassDeclarationSyntax ||
            type is RecordDeclarationSyntax record && record.ClassOrStructKeyword.RawKind != (int)SyntaxKind.StructKeyword;
        var testMethodNames = type.Members.OfType<MethodDeclarationSyntax>()
            .Where(method => method.AttributeLists.SelectMany(list => list.Attributes)
                .Any(attribute => FocusedEvidenceMethodResolver.IsTestAttribute(attribute.Name.ToString())))
            .Select(method => method.Identifier.ValueText).ToImmutableArray();
        return new(name.SimpleName, name.QualifiedName, isClassOrNonStructRecord, testMethodNames);
    }
}
