using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record FocusedEvidenceDeclarationFacts(
    ImmutableArray<FocusedEvidenceDeclarationFacts.DeclaredType> Types,
    [property: JsonRequired] bool ContainsDiagnostics,
    [property: JsonRequired] bool ContainsErrorDiagnostics,
    [property: JsonRequired] bool ContainsDirectives,
    ImmutableArray<FocusedEvidenceUsingDirectiveFact> Usings)
{
    internal sealed record DeclaredType(string SimpleName, string QualifiedName,
        [property: JsonRequired] bool IsClassOrNonStructRecord, ImmutableArray<string> TestMethodNames,
        [property: JsonRequired] bool HasBaseList, ImmutableArray<string> MethodAttributeNames,
        FocusedEvidenceTypeShapeFact Shape)
    {
        internal FocusedEvidenceParsedRootCache.DeclaredTypeName Name => new(SimpleName, QualifiedName);
    }

    internal static FocusedEvidenceDeclarationFacts Extract(CompilationUnitSyntax root) =>
        new(root.DescendantNodes().OfType<TypeDeclarationSyntax>().Select(ExtractType).ToImmutableArray(),
            root.ContainsDiagnostics,
            root.SyntaxTree.GetDiagnostics().Any(diagnostic =>
                diagnostic.Severity == DiagnosticSeverity.Error),
            root.ContainsDirectives,
            root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Select(item => new FocusedEvidenceUsingDirectiveFact(item.Name?.ToString(), item.Alias is not null,
                    item.Parent is CompilationUnitSyntax && item.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)))
                .ToImmutableArray());

    private static DeclaredType ExtractType(TypeDeclarationSyntax type)
    {
        var name = FocusedEvidenceParsedRootCache.GetDeclaredTypeName(type);
        var isRecordStruct = type is RecordDeclarationSyntax record && record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword);
        var isClassOrNonStructRecord = type is ClassDeclarationSyntax || type is RecordDeclarationSyntax && !isRecordStruct;
        var testMethodNames = ImmutableArray.CreateBuilder<string>();
        var methodAttributeNames = ImmutableArray.CreateBuilder<string>();
        var methods = ImmutableArray.CreateBuilder<FocusedEvidenceDeclaredMethodFact>();
        foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
        {
            var methodName = method.Identifier.ValueText;
            var attributes = ExtractAttributes(method.AttributeLists);
            methods.Add(new(methodName, attributes));
            methodAttributeNames.AddRange(attributes.Select(attribute => attribute.Name));
            if (attributes.Any(attribute => FocusedEvidenceMethodResolver.IsTestAttribute(attribute.Name)))
                testMethodNames.Add(methodName);
        }
        var shape = new FocusedEvidenceTypeShapeFact(
            type.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.AbstractKeyword)),
            type.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PublicKeyword)),
            type.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.StaticKeyword)),
            type is RecordDeclarationSyntax,
            type.TypeParameterList is not null,
            type is InterfaceDeclarationSyntax or StructDeclarationSyntax || isRecordStruct,
            type.Ancestors().OfType<TypeDeclarationSyntax>().Any(),
            type.BaseList?.Types.Select(baseType => baseType.Type.ToString()).ToImmutableArray() ?? [],
            ExtractAttributes(type.AttributeLists), methods.ToImmutable());
        return new(name.SimpleName, name.QualifiedName, isClassOrNonStructRecord, testMethodNames.ToImmutable(),
            type.BaseList is not null, methodAttributeNames.ToImmutable(), shape);
    }

    private static ImmutableArray<FocusedEvidenceAttributeUsageFact> ExtractAttributes(
        SyntaxList<AttributeListSyntax> lists) =>
        lists.SelectMany(list => list.Attributes).Select(ExtractAttribute).ToImmutableArray();

    private static FocusedEvidenceAttributeUsageFact ExtractAttribute(AttributeSyntax attribute)
    {
        var name = attribute.Name.ToString();
        if (attribute.ArgumentList?.Arguments is not { Count: 2 } arguments ||
            arguments.Any(argument => argument.NameColon is not null || argument.NameEquals is not null ||
                argument.Expression is not LiteralExpressionSyntax literal || !literal.IsKind(SyntaxKind.StringLiteralExpression)))
            return new(name, false, null, null);

        return new(name, true, ((LiteralExpressionSyntax)arguments[0].Expression).Token.ValueText,
            ((LiteralExpressionSyntax)arguments[1].Expression).Token.ValueText);
    }
}
