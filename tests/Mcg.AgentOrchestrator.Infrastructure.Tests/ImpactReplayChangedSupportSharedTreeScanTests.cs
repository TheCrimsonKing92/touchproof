using Mcg.AgentOrchestrator.Infrastructure;
using static ImpactReplayChangedSupportReferenceScanTests;

public sealed class ImpactReplayChangedSupportSharedTreeScanTests
{
    private const string SupportText = "namespace Support; public class Helpers { }";
    private const string AlphaText = "namespace SampleTests; public class AlphaTests { private Support.Helpers? _helper; }";

    [Fact]
    public void Select_ValidChangedSupport_SelectsReferringClass()
    {
        using var fixture = CreateFixture();

        var result = SelectWithAndWithoutIndex(fixture, ["Support.cs"]);

        Assert.Equal(["SampleTests.AlphaTests"], result.Classes);
        Assert.Null(result.AbandonReason);
        Assert.Empty(Assert.Single(result.SupportSelections).Reasons);
    }

    [Fact]
    public void Select_UnparseableSource_ReportsIncompleteInventory()
    {
        using var fixture = CreateFixture();
        fixture.Add("Bad.cs", "public class {");

        var result = SelectWithAndWithoutIndex(fixture, ["Support.cs", "Bad.cs"]);

        AssertIncomplete(result, "Changed test support source could not be parsed: Bad.cs");
        Assert.Equal(["Bad.cs", "Support.cs"], result.SupportSelections.Select(summary => summary.Path));
    }

    [Fact]
    public void Select_MissingChangedSource_ReportsDeletedSeed()
    {
        using var fixture = CreateFixture();
        AddDeletedSource(fixture, "Deleted.cs");

        var result = SelectWithAndWithoutIndex(fixture, ["Deleted.cs"]);

        Assert.Empty(result.Classes);
        Assert.Null(result.AbandonReason);
        var summary = Assert.Single(result.SupportSelections);
        Assert.Equal("Deleted.cs", summary.Path);
        Assert.True(summary.Unresolved);
        Assert.True(summary.FullRunTriggered);
        Assert.Equal(["changed support source is missing or deleted"], summary.Reasons);
    }

    [Fact]
    public void Select_MissingUnchangedSource_ReportsIncompleteInventory()
    {
        using var fixture = CreateFixture();
        AddDeletedSource(fixture, "Ghost.cs");

        var result = SelectWithAndWithoutIndex(fixture, ["Support.cs"]);

        AssertIncomplete(result, "Test support source is missing: Ghost.cs");
        Assert.Equal("Support.cs", Assert.Single(result.SupportSelections).Path);
    }

    [Fact]
    public void Select_CaseVariantIndexedPath_PreservesReportedSourcePath()
    {
        using var fixture = CreateFixture(infrastructure: false);

        var result = SelectWithAndWithoutIndex(fixture, ["Support.cs"], indexedAlphaName: "ALPHATESTS.cs");

        Assert.Null(result.AbandonReason);
        Assert.Equal(["AlphaTests.cs"], result.OtherFiles);
    }

    private static ImpactReplayChangedSupportSelection SelectWithAndWithoutIndex(
        SourceFixture fixture, string[] changed, string indexedAlphaName = "AlphaTests.cs")
    {
        var index = new ImpactReplayTestSourceIndex([
            CSharpSourceFileRootReader.Read(Path.Combine(fixture.Root, "Support.cs")).SyntaxTree,
            CSharpSourceFileRootReader.Read(Path.Combine(fixture.Root, "AlphaTests.cs")).SyntaxTree
                .WithFilePath(Path.Combine(fixture.Root, indexedAlphaName))
        ]);
        var baseline = fixture.Select(changed);
        var indexed = ImpactReplayChangedSupportReferenceScan.Select(fixture.Root, changed, fixture.Projects,
            () => fixture.Runnable, sources: index);

        Assert.Equal(baseline.Classes, indexed.Classes);
        Assert.Equal(baseline.OtherFiles, indexed.OtherFiles);
        Assert.Equal(baseline.AbandonReason, indexed.AbandonReason);
        Assert.Equal(baseline.SupportSelections.Count, indexed.SupportSelections.Count);
        foreach (var (expected, actual) in baseline.SupportSelections.Zip(indexed.SupportSelections))
        {
            Assert.Equal(expected.Path, actual.Path);
            Assert.Equal(expected.Reasons, actual.Reasons);
        }
        return indexed;
    }

    private static SourceFixture CreateFixture(bool infrastructure = true)
    {
        var fixture = new SourceFixture();
        fixture.Add("Support.cs", SupportText, infrastructure);
        fixture.Add("AlphaTests.cs", AlphaText, infrastructure);
        return fixture;
    }

    private static void AddDeletedSource(SourceFixture fixture, string path)
    {
        fixture.Add(path, "public class Deleted { }");
        File.Delete(Path.Combine(fixture.Root, path));
    }

    private static void AssertIncomplete(ImpactReplayChangedSupportSelection result, string reason)
    {
        Assert.Equal(reason, result.AbandonReason);
        Assert.Empty(result.Classes);
        Assert.All(result.SupportSelections, summary =>
        {
            Assert.True(summary.Unresolved);
            Assert.True(summary.FullRunTriggered);
            Assert.Equal([reason], summary.Reasons);
        });
    }
}
