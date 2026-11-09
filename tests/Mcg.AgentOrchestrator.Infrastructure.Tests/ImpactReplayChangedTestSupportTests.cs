// Parallel-safe: each fixture owns its repository and detached checkouts.
public sealed class ImpactReplayChangedTestSupportTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ChangedTestsAndHelper_SelectDeclarationsAndDirectReferencers(int sourceCount)
    {
        using var fixture = new ImpactReplayFixture(changedSourceCount: sourceCount, changedFooTests: true,
            initialTestFiles: new Dictionary<string, string>
            {
                ["BetaTests.cs"] = """
                    using HelperAlias = Support.Helpers;
                    namespace SampleTests;
                    public class BetaTests { [Xunit.Fact] public void Works() { var helper = new HelperAlias(); } }
                    """,
                ["Support.cs"] = "namespace Support; public class Helpers { public int Value => 1; }"
            }, changedTestFiles: new Dictionary<string, string>
            {
                ["Support.cs"] = "namespace Support; public class Helpers { public int Value => 2; }"
            });
        fixture.AddAttempt("changed-support");
        var (report, _) = fixture.Run();
        var attempt = Assert.Single(report.Attempts);
        Assert.Equal("Resolved", attempt.SelectionOutcome);
        Assert.Null(attempt.DegradationKind);
        Assert.Contains("FooTests", attempt.SelectedTestClasses);
        Assert.Contains("BetaTests", attempt.SelectedTestClasses);
        Assert.Equal(sourceCount, attempt.ChangedSourceFileCount);
        Assert.Contains("Beta lane", attempt.WouldRunLanes);
        Assert.Contains("Foo lane", attempt.WouldRunLanes);
        if (sourceCount > 0) Assert.Contains("AlphaTests", attempt.SelectedTestClasses);
        else Assert.DoesNotContain("AlphaTests", attempt.SelectedTestClasses);
    }

    [Theory]
    [InlineData("public class Helpers { }", "public partial class BetaTests : Support.Helpers { }")]
    [InlineData("public class Helpers<T> { }", "public partial class BetaTests { private Support.Helpers<int> value; }")]
    [InlineData("public class HelpersAttribute : System.Attribute { }", "[Support.Helpers] public partial class BetaTests { }")]
    public void DirectReferences_InheritanceGenericsAttributesAndPartialsSelectClass(string helper, string reference)
    {
        using var fixture = new ImpactReplayFixture(changedSourceCount: 0,
            initialTestFiles: new Dictionary<string, string>
            {
                ["BetaTests.cs"] = "namespace SampleTests; public partial class BetaTests { [Xunit.Fact] public void Works() { } }",
                ["BetaPart.cs"] = "namespace SampleTests; " + reference,
                ["Support.cs"] = "namespace Support; " + helper
            }, changedTestFiles: new Dictionary<string, string>
            {
                ["Support.cs"] = "// Changed helper\nnamespace Support; " + helper
            });
        fixture.AddAttempt("reference-shape");
        var attempt = Assert.Single(fixture.Run().Report.Attempts);
        Assert.Equal("Resolved", attempt.SelectionOutcome);
        Assert.Equal(new[] { "BetaTests" }, attempt.SelectedTestClasses);
        Assert.Contains("Beta lane", attempt.WouldRunLanes);
    }

    [Fact]
    public void EqualShortTypeNames_OnlyReferencersOfChangedTypeAreSelected()
    {
        using var fixture = new ImpactReplayFixture(changedSourceCount: 0,
            initialTestFiles: new Dictionary<string, string>
            {
                ["AlphaTests.cs"] = "namespace SampleTests; public class AlphaTests { [Xunit.Fact] public void Works() { var x = new Other.Helpers(); } }",
                ["BetaTests.cs"] = "namespace SampleTests; public class BetaTests { [Xunit.Fact] public void Works() { var x = new Support.Helpers(); } }",
                ["Other.cs"] = "namespace Other; public class Helpers { }",
                ["Support.cs"] = "namespace Support; public class Helpers { }"
            }, changedTestFiles: new Dictionary<string, string>
            {
                ["Support.cs"] = "namespace Support; public class Helpers { public int Value => 2; }"
            });
        fixture.AddAttempt("type-collision");
        var attempt = Assert.Single(fixture.Run().Report.Attempts);
        Assert.Equal("Resolved", attempt.SelectionOutcome);
        Assert.Equal(new[] { "BetaTests" }, attempt.SelectedTestClasses);
        Assert.DoesNotContain("Alpha lane", attempt.WouldRunLanes);
    }

    [Fact]
    public void LinkedSupportSource_UsesTestProjectMembershipOutsideTestsDirectory()
    {
        using var fixture = new ImpactReplayFixture(changedSourceCount: 0,
            initialRepositoryFiles: new Dictionary<string, string>
            {
                ["shared/Helpers.cs"] = "namespace Support; public class Helpers { }",
                ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"] = """
                    <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
                      <ProjectReference Include="../../src/Sample/Sample.csproj" />
                      <Compile Include="../../shared/Helpers.cs" Link="Helpers.cs" />
                    </ItemGroup></Project>
                    """
            }, initialTestFiles: new Dictionary<string, string>
            {
                ["BetaTests.cs"] = "namespace SampleTests; public class BetaTests { [Xunit.Fact] public void Works() { var x = new Support.Helpers(); } }"
            }, changedRepositoryFiles: new Dictionary<string, string>
            {
                ["shared/Helpers.cs"] = "namespace Support; public class Helpers { public int Value => 2; }"
            });
        fixture.AddAttempt("linked-helper");
        var attempt = Assert.Single(fixture.Run().Report.Attempts);
        Assert.Equal("Resolved", attempt.SelectionOutcome);
        Assert.Equal(0, attempt.ChangedSourceFileCount);
        Assert.Equal(new[] { "BetaTests" }, attempt.SelectedTestClasses);
    }

    [Fact]
    public void HelperWithoutReferencer_AbandonsWithSupportKindAndPath()
    {
        using var fixture = new ImpactReplayFixture(changedTestFiles: new Dictionary<string, string>
        {
            ["UnusedSupport.cs"] = "namespace Support; public class Helpers { }"
        });
        fixture.AddAttempt("unused-support");
        var attempt = Assert.Single(fixture.Run().Report.Attempts);
        Assert.Equal("Abandoned", attempt.SelectionOutcome);
        Assert.Equal("ChangedTestSupportUnresolved", attempt.DegradationKind);
        Assert.Contains("UnusedSupport.cs", attempt.SelectionReason);
        Assert.Empty(attempt.SkippedLanes);
        Assert.False(attempt.MissCheckComplete);
    }
}
