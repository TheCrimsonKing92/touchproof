using Mcg.AgentOrchestrator.Core;

public sealed class SourceTypeMemberSpanTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void DirectMembers_KeepNamesAndInclusiveSpans(string newline)
    {
        var source = """
            namespace Fixture;
            internal partial class Envelope
            {
                // Trivia before the attribute is outside the member.
                [Obsolete]
                public int Count { get; } =
                    1;
                public int Size =>
                    Count;
                public void Send()
                {
                }
                [Obsolete]
                public void Send<T>(T item) where T : new() => _ = item;
                internal const int First = 1,
                    Second = 2;
                private int field = new[] { 1, 2 }.Length;
                public event Action? Raised;
                public Envelope() { }
                ~Envelope() { }
                public int this[int i] => i;
                public static int operator +(Envelope a, Envelope b) => 0;
                public static implicit operator int(Envelope value) => 0;
                internal class Nested
                {
                    public int Child => 3;
                }
                internal record Payload(
                    int Code,
                    string Label);
            }
            internal partial record Positional<T>(
                [property: Obsolete] Dictionary<string, T> Items,
                string Name = @"first
            last"
            )
            {
                internal int Length => Name.Length;
            }
            """.ReplaceLineEndings(newline);

        Assert.True(CSharpTestClassScanner.TryReadSourceSymbols(source, out var symbols));

        Assert.Equal(["Envelope", "Positional"], symbols.DeclaredTypes.Select(type => type.Name));
        var envelope = symbols.DeclaredTypes[0];
        Assert.Equal(
            [("Count", 5, 7), ("Size", 8, 9), ("Send", 10, 12), ("Send", 13, 14), ("First", 15, 16),
             ("Second", 15, 16), ("Nested", 24, 27), ("Payload", 28, 30)],
            Spans(envelope));
        Assert.Equal(["Nested", "Payload"], envelope.NestedTypes.Select(type => type.Name));
        Assert.Equal([("Child", 26, 26)], Spans(envelope.NestedTypes[0]));
        Assert.Equal([("Code", 29, 29), ("Label", 30, 30)], Spans(envelope.NestedTypes[1]));
        Assert.Equal([("Items", 33, 33), ("Name", 34, 35), ("Length", 38, 38)], Spans(symbols.DeclaredTypes[1]));
    }

    private static IEnumerable<(string Name, int StartLine, int EndLine)> Spans(
        CSharpTestClassScanner.SourceTypeDeclaration declaration) =>
        declaration.Members.Select(member => (member.Name, member.StartLine, member.EndLine));
}
