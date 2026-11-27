using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record FocusedEvidenceDeclarationFacts(
    ImmutableArray<FocusedEvidenceDeclarationFacts.DeclaredType> Types,
    [property: JsonRequired] bool ContainsDiagnostics,
    [property: JsonRequired] bool ContainsDirectives)
{
    internal sealed record DeclaredType(string SimpleName, string QualifiedName,
        [property: JsonRequired] bool IsClassOrNonStructRecord, ImmutableArray<string> TestMethodNames,
        [property: JsonRequired] bool HasBaseList, ImmutableArray<string> MethodAttributeNames)
    {
        internal FocusedEvidenceParsedRootCache.DeclaredTypeName Name => new(SimpleName, QualifiedName);
    }

    internal static FocusedEvidenceDeclarationFacts Extract(CompilationUnitSyntax root) =>
        new(root.DescendantNodes().OfType<TypeDeclarationSyntax>().Select(ExtractType).ToImmutableArray(),
            root.ContainsDiagnostics, root.ContainsDirectives);

    private static DeclaredType ExtractType(TypeDeclarationSyntax type)
    {
        var name = FocusedEvidenceParsedRootCache.GetDeclaredTypeName(type);
        var isClassOrNonStructRecord = type is ClassDeclarationSyntax ||
            type is RecordDeclarationSyntax record && record.ClassOrStructKeyword.RawKind != (int)SyntaxKind.StructKeyword;
        var testMethodNames = ImmutableArray.CreateBuilder<string>();
        var methodAttributeNames = ImmutableArray.CreateBuilder<string>();
        foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
        {
            var isTest = false;
            foreach (var attribute in method.AttributeLists.SelectMany(list => list.Attributes))
            {
                var attributeName = attribute.Name.ToString();
                methodAttributeNames.Add(attributeName);
                isTest = isTest || FocusedEvidenceMethodResolver.IsTestAttribute(attributeName);
            }
            if (isTest)
                testMethodNames.Add(method.Identifier.ValueText);
        }
        return new(name.SimpleName, name.QualifiedName, isClassOrNonStructRecord, testMethodNames.ToImmutable(),
            type.BaseList is not null, methodAttributeNames.ToImmutable());
    }
}
