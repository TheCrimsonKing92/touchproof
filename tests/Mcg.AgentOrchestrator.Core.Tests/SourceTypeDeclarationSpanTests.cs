using Mcg.AgentOrchestrator.Core;

public sealed class SourceTypeDeclarationSpanTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void AttributesAndModifiers_SpanIncludesDeclarationOnly(string newline)
    {
        var source = string.Join(newline, new[]
        {
            "// leading trivia", "namespace Fixture;", "[Obsolete]", "public partial",
            "class First", "{", "    class Nested { }", "}", "", "record Second(int Value);"
        });

        Assert.True(CSharpTestClassScanner.TryReadSourceSymbols(source, out var symbols));

        Assert.Equal(["First", "Second"], symbols.DeclaredTypes.Select(type => type.Name));
        var first = symbols.DeclaredTypes[0];
        Assert.Equal((3, 8), (first.StartLine, first.EndLine));
        Assert.True(first.IsPartial);
        Assert.Equal((10, 10), (symbols.DeclaredTypes[1].StartLine, symbols.DeclaredTypes[1].EndLine));
    }

    [Fact]
    public void SkippedMultilineText_PreservesFollowingDeclarationLines()
    {
        const string source = "/* comment\ncontinued */\nclass Before { string Text = @\"one\ntwo\"; }\n\nclass After\n{\n}\n";

        Assert.True(CSharpTestClassScanner.TryReadSourceSymbols(source, out var symbols));

        var before = Assert.Single(symbols.DeclaredTypes, type => type.Name == "Before");
        var after = Assert.Single(symbols.DeclaredTypes, type => type.Name == "After");
        Assert.Equal((3, 4), (before.StartLine, before.EndLine));
        Assert.Equal((6, 8), (after.StartLine, after.EndLine));
    }

    [Fact]
    public void AttributeWithArrayArgument_StartsAtOpeningBracket()
    {
        const string source = "[Example(new[] {\n1, 2\n})]\ninternal sealed class Subject { }";

        Assert.True(CSharpTestClassScanner.TryReadSourceSymbols(source, out var symbols));

        var type = Assert.Single(symbols.DeclaredTypes);
        Assert.Equal((1, 4), (type.StartLine, type.EndLine));
    }
}
