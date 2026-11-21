using static ImpactReplayChangedSupportReferenceScanTests;

// Each case owns the source files it scans.
public sealed class ImpactReplayChangedSupportClosureUnionTests
{
    [Theory]
    [InlineData("fixture")]
    [InlineData("runnable-helper")]
    [InlineData("base-class")]
    public void DirectAndHelperReferrers_BothSelected(string shape)
    {
        using var fixture = new SourceFixture();
        fixture.Add("Support.cs", "namespace Support; public class Helpers { }");
        fixture.Add("AlphaTests.cs", "namespace SampleTests; public class AlphaTests { Support.Helpers value; }");
        var (helper, beta, direct, closure) = shape switch
        {
            "runnable-helper" => (
                "public class BothTests { public static Support.Helpers Create() => new(); }",
                "public class BetaTests { object Value() => BothTests.Create(); }", 2, 3),
            "base-class" => (
                "public abstract class HelperFixture { Support.Helpers value; }",
                "public class BetaTests : HelperFixture { }", 1, 2),
            _ => (
                "public class HelperFixture { Support.Helpers value; }",
                "public class BetaTests { HelperFixture value; }", 1, 2)
        };
        fixture.Add("Helper.cs", "namespace SampleTests; " + helper);
        fixture.Add("BetaTests.cs", "namespace SampleTests; " + beta);

        var result = fixture.Select(["Support.cs"]);

        Assert.Null(result.AbandonReason);
        Assert.Equal(shape == "runnable-helper" ? new[] { "SampleTests.AlphaTests", "SampleTests.BetaTests", "SampleTests.BothTests" }
            : new[] { "SampleTests.AlphaTests", "SampleTests.BetaTests" }, result.Classes);
        var summary = Assert.Single(result.SupportSelections);
        Assert.Equal(direct, summary.DirectMatchCount);
        Assert.Equal(closure, summary.ClosureCount);
        Assert.True(summary.Complete);
        Assert.False(summary.FullRunTriggered);
    }

    [Fact]
    public void LeafTestClassChange_NarrowsToThatClass()
    {
        using var fixture = new SourceFixture();
        foreach (var name in new[] { "LeafTests", "AlphaTests", "BetaTests", "BothTests", "OtherTests" })
            fixture.Add(name + ".cs", $"namespace SampleTests; public class {name} {{ }}");
        fixture.Runnable.Add("SampleTests.LeafTests");

        var result = fixture.Select(["LeafTests.cs"], threshold: 1);

        Assert.Equal("SampleTests.LeafTests", Assert.Single(result.Classes));
        Assert.Null(result.AbandonReason);
        var summary = Assert.Single(result.SupportSelections);
        Assert.Equal(1, summary.DirectMatchCount);
        Assert.Equal(1, summary.ClosureCount);
        Assert.True(summary.Complete);
        Assert.False(summary.FullRunTriggered);
    }
}
