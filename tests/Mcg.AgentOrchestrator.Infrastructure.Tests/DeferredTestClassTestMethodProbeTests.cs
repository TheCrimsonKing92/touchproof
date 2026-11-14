using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: source-only probes over a unique temporary project; no processes.
public sealed class DeferredTestClassTestMethodProbeTests
{
    [Theory]
    [InlineData("using Xunit; class ExampleTests { [Fact] void Runs() {} }", true)]
    [InlineData("class ExampleTests { [global::Xunit.Theory] void Runs() {} }", true)]
    [InlineData("using X = Xunit; class ExampleTests { [X.Fact] void Runs() {} }", true)]
    [InlineData("using Check = Xunit.FactAttribute; class ExampleTests { [Check] void Runs() {} }", true)]
    [InlineData("global using Xunit; class ExampleTests { [Fact(Skip=\"later\")] void Runs() {} }", true)]
    [InlineData("using Xunit; class ExampleTests { class Nested { [Fact] void Runs() {} } }", false)]
    [InlineData("using Xunit; class Neighbor { [Fact] void Runs() {} } class ExampleTests {}", false)]
    [InlineData("using Xunit; class Parent { [Fact] void Runs() {} } class ExampleTests : Parent {}", false)]
    [InlineData("using Xunit; class FactAttribute : System.Attribute {} class ExampleTests { [Fact] void Runs() {} }", false)]
    [InlineData("class AlmostFactAttribute : System.Attribute {} class ExampleTests { [AlmostFact] void Runs() {} }", false)]
    [InlineData("using Xunit; class ExampleTests { string Text = \"[Fact] void Runs() {}\"; }", false)]
    public void DeclaredMethodsRequireXunitAttributeIdentity(string source, bool expected)
    {
        using var fixture = new Fixture();
        fixture.Write("ExampleTests.cs", source);
        Assert.Equal(expected, new DeferredTestClassTestMethodProbe().DeclaresTestMethod(fixture.Root, "ExampleTests"));
    }

    [Theory]
    [InlineData("Xunit.FactAttribute")]
    [InlineData("global::Xunit.TheoryAttribute")]
    public void ResolvesTransitiveDerivedAttributesAndPartialDeclarations(string rootAttribute)
    {
        using var fixture = new Fixture();
        fixture.Write("ExampleTests.cs", "partial class ExampleTests {}");
        fixture.Write("OtherPart.cs", "using Check = Custom.SecondAttribute; partial class ExampleTests { [Check] void Runs() {} }");
        fixture.Write("Attributes.cs", "namespace Custom { class FirstAttribute : " + rootAttribute +
            " {} class SecondAttribute : FirstAttribute {} }");
        Assert.True(new DeferredTestClassTestMethodProbe().DeclaresTestMethod(fixture.Root, "ExampleTests"));
    }

    [Fact]
    public void CyclicAttributesAndUnparseablePartsCannotProveATest()
    {
        using var fixture = new Fixture();
        fixture.Write("ExampleTests.cs", "partial class ExampleTests { [First] void Runs() {} }");
        fixture.Write("Attributes.cs", "class FirstAttribute : SecondAttribute {} class SecondAttribute : FirstAttribute {}");
        fixture.Write("Broken.cs", "partial class ExampleTests { [Xunit.Fact] void Runs(");
        Assert.False(new DeferredTestClassTestMethodProbe().DeclaresTestMethod(fixture.Root, "ExampleTests"));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("test-method-probe-").FullName;
        internal void Write(string name, string source) => File.WriteAllText(Path.Combine(Root, name), source);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
