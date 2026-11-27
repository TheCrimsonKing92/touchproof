using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceImpactSelectionParseOnceTests
{
    private const string TestRoot = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests";
    private const string Project = TestRoot + "/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
    private const string Support = TestRoot + "/Support.cs";
    private const string Alpha = TestRoot + "/AlphaTests.cs";
    private const string Beta = TestRoot + "/BetaTests.cs";
    private const string AlphaWithReference = "namespace SampleTests; public class AlphaTests { [Fact] public void Works() { _ = new Support.Helpers(); } }";
    private const string AlphaWithoutReference = "namespace SampleTests; public class AlphaTests { [Fact] public void Works() { } }";

    [Fact]
    public void ReadLive_ChangedSupport_ParsesEachTestSourceOnce()
    {
        using var fixture = new GitWorkspaceLaunchFixture();
        var checkout = CreateCheckout(fixture, AlphaWithReference);
        using var parses = FocusedEvidenceParsedRootCache.Observe(checkout);

        var result = AcceptanceImpactSelectionReader.ReadLive(checkout, [Support]);

        Assert.Equal(["SampleTests.AlphaTests"], result.SupportProjectClasses![Project]);
        Assert.Equal(1, Assert.Single(result.SupportSelections!).DirectMatchCount);
        foreach (var path in new[] { Support, Alpha, Beta })
            Assert.Equal(1L, parses.CountFor(Path.Combine(checkout, path)));
        Assert.Equal(3L, parses.Total);
    }

    [Fact]
    public void Read_SuppliedIndex_UsesIndexedReferencesForChangedSupport()
    {
        using var fixture = new GitWorkspaceLaunchFixture();
        var checkout = CreateCheckout(fixture, AlphaWithReference);
        var routing = new ImpactReplayChangedSourceRouting(checkout);
        var paths = LaneTestSourceRootResolver.SourceFiles(checkout, [TestRoot]);
        var original = new ImpactReplayTestSourceIndex(paths, routing.TestProjects, checkout);
        WriteSource(checkout, Alpha, AlphaWithoutReference);
        var current = new ImpactReplayTestSourceIndex(paths, routing.TestProjects, checkout);
        var classes = AcceptanceTestClassSourceScanner.Scan(checkout);

        Assert.Equal(["SampleTests.AlphaTests"], Read(original).Classes);
        var withoutReference = Read(current);
        Assert.Null(withoutReference.AbandonReason);
        Assert.Empty(withoutReference.Classes);

        ImpactReplayChangedSupportSelection Read(ImpactReplayTestSourceIndex index) =>
            AcceptanceImpactSelectionReader.Read(checkout, [Support], routing, index, classes,
                guards: null, testProjectDirectories: [], bypassCache: false).ChangedTests.Support;
    }

    private static string CreateCheckout(GitWorkspaceLaunchFixture fixture, string alphaSource)
    {
        var checkout = fixture.CreateRepository();
        Directory.CreateDirectory(Path.Combine(checkout, TestRoot));
        Directory.CreateDirectory(Path.Combine(checkout, "config"));
        File.WriteAllText(Path.Combine(checkout, "Mcg.AgentOrchestrator.sln"), "");
        File.WriteAllText(Path.Combine(checkout, Project), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(checkout, "config/acceptance-manifest.json"), $$"""
            { "engine": { "laneTestSourceRoots": ["{{TestRoot}}"] },
              "checks": [{ "name": "infra", "type": "dotnet-test", "project": "{{Project}}" }] }
            """);
        WriteSource(checkout, Support, "namespace Support; public class Helpers { }");
        WriteSource(checkout, Alpha, alphaSource);
        WriteSource(checkout, Beta, "namespace SampleTests; public class BetaTests { [Fact] public void Works() { } }");
        return checkout;
    }

    private static void WriteSource(string checkout, string path, string source) =>
        File.WriteAllText(Path.Combine(checkout, path), $"{source}\n// {Guid.NewGuid():N}\n");
}
