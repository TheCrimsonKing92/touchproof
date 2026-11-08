using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Parallel-safe: read-only verified repository inspection; controls use in-memory syntax trees.
public sealed class OwnerConsolePartialDeclarationTests
{
    private const string ConsolePath = "src/Mcg.AgentOrchestrator.App/Console/";
    private static readonly string[] Expected =
    [
        ConsolePath + "OwnerConsoleGateExecutionProjection.cs::OwnerConsoleGateProgress",
        ConsolePath + "OwnerConsoleGateProgress.cs::OwnerConsoleGateProgress"
    ];

    [Fact]
    public void PartialDeclarations_EqualTheTwoExistingRecordParts()
    {
        var root = VerifiedRepositoryRoot.Find();
        var sources = Directory.EnumerateFiles(Path.Combine(root, ConsolePath), "*.cs", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(root, path).Replace('\\', '/'), File.ReadAllText, StringComparer.Ordinal);
        Assert.NotEmpty(sources);
        Assert.Empty(Violations(sources));
    }

    [Theory]
    [InlineData("partial class Extra {}")]
    [InlineData("partial record OwnerConsoleGateProgress;")]
    [InlineData("partial class Extra { partial void Draw(); }")]
    public void ThirdPartialDeclaration_IsRejected(string source)
    {
        var sources = ExistingParts();
        sources.Add("Extra.cs", source);
        Assert.Contains(Violations(sources), finding => finding.StartsWith("unexpected partial: Extra.cs::", StringComparison.Ordinal));
    }

    [Fact]
    public void CommentAndIdentifierContainingPartial_Pass()
    {
        var sources = ExistingParts();
        sources.Add("View.cs", "// partial class Extra {}\nclass View { int _partial; }");
        Assert.Empty(Violations(sources));
    }

    [Fact]
    public void MissingExistingPart_IsRejected()
    {
        var sources = ExistingParts();
        sources.Remove(ConsolePath + "OwnerConsoleGateProgress.cs");
        Assert.Equal(["missing partial: " + ConsolePath + "OwnerConsoleGateProgress.cs::OwnerConsoleGateProgress"], Violations(sources));
    }

    private static Dictionary<string, string> ExistingParts() => new(StringComparer.Ordinal)
    {
        [ConsolePath + "OwnerConsoleGateProgress.cs"] = "partial record OwnerConsoleGateProgress;",
        [ConsolePath + "OwnerConsoleGateExecutionProjection.cs"] = "partial record OwnerConsoleGateProgress;"
    };

    private static IReadOnlyList<string> Violations(IReadOnlyDictionary<string, string> sources)
    {
        var declarations = sources.SelectMany(pair => CSharpSyntaxTree.ParseText(pair.Value).GetRoot().DescendantNodes()
            .OfType<MemberDeclarationSyntax>().Where(member => member.Modifiers.Any(SyntaxKind.PartialKeyword))
            .Select(member => pair.Key + "::" + DeclaredName(member))).ToArray();
        return declarations.Except(Expected, StringComparer.Ordinal).Select(name => "unexpected partial: " + name)
            .Concat(Expected.Except(declarations, StringComparer.Ordinal).Select(name => "missing partial: " + name))
            .Concat(declarations.GroupBy(name => name, StringComparer.Ordinal).Where(group => group.Count() > 1)
                .Select(group => "duplicate partial: " + group.Key)).Order(StringComparer.Ordinal).ToArray();
    }

    private static string DeclaredName(MemberDeclarationSyntax member) => member switch
    {
        BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
        MethodDeclarationSyntax method => method.Identifier.ValueText,
        PropertyDeclarationSyntax property => property.Identifier.ValueText,
        EventDeclarationSyntax @event => @event.Identifier.ValueText,
        ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
        _ => member.Kind().ToString()
    };
}
