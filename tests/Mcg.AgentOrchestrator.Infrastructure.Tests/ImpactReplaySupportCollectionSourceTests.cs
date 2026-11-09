// Parallel-safe: each fixture owns its repository, receipts and detached checkouts.
public sealed class ImpactReplaySupportCollectionSourceTests
{
    private const string OtherProject = "tests/Sample.Other.Tests";
    private const string SupportManifest = """
        {
          "version": 1,
          "engine": {
            "laneTestSourceRoots": ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests", "tests/Sample.TestSupport"],
            "infrastructureTestLanes": [
              { "name": "Alpha lane", "filter": "FullyQualifiedName~AlphaTests", "estimatedSerialSeconds": 11 },
              { "name": "Beta lane", "filter": "FullyQualifiedName~BetaTests", "estimatedSerialSeconds": 23.5 },
              { "name": "Chaos gate", "filter": "FullyQualifiedName~FloorTests", "estimatedSerialSeconds": 7 }
            ]
          },
          "checks": [{ "name": "Infrastructure tests", "type": "dotnet-test",
            "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj" }]
        }
        """;

    [Fact]
    public void SupportProjectCollectionConstant_ReplaysAndMapsChangedClass()
    {
        const string test = """
            namespace SampleTests;
            [Xunit.Collection(SupportCollections.Shared)]
            public class BetaTests { [Xunit.Fact] public void Works() { } }
            """;
        var files = SupportFiles();
        files["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"] = """
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
              <ProjectReference Include="../../src/Sample/Sample.csproj" />
              <ProjectReference Include="../Sample.TestSupport/Sample.TestSupport.csproj" />
            </ItemGroup></Project>
            """;
        using var fixture = new ImpactReplayFixture(changedSourceCount: 0,
            initialRepositoryFiles: files,
            initialTestFiles: new Dictionary<string, string> { ["BetaTests.cs"] = test },
            changedTestFiles: new Dictionary<string, string> { ["BetaTests.cs"] = "// Changed test\n" + test });
        fixture.AddAttempt("support-collection");

        var (report, _) = fixture.Run();

        Assert.Empty(report.UnreplayableAttempts);
        var attempt = Assert.Single(report.Attempts);
        Assert.Equal("Resolved", attempt.SelectionOutcome);
        Assert.Null(attempt.DegradationKind);
        Assert.Equal(new[] { "BetaTests" }, attempt.SelectedTestClasses);
        Assert.Contains("Beta lane", attempt.WouldRunLanes);
        Assert.Empty(report.UnmappedTestClasses!.Occurrences);
    }

    [Fact]
    public void AdditionalProject_SupportCollectionConstant_KeepsChangedClassRunnable()
    {
        const string test = """
            namespace SampleOther;
            [Xunit.Collection(SupportCollections.Shared)]
            public class OtherTests { [Xunit.Fact] public void Works() { } }
            """;
        var files = SupportFiles();
        files[OtherProject + "/Sample.Other.Tests.csproj"] = """
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
              <ProjectReference Include="../Sample.TestSupport/Sample.TestSupport.csproj" />
            </ItemGroup></Project>
            """;
        files[OtherProject + "/OtherTests.cs"] = test;
        using var fixture = new ImpactReplayFixture(changedSourceCount: 0,
            initialRepositoryFiles: files,
            changedRepositoryFiles: new Dictionary<string, string>
            {
                [OtherProject + "/OtherTests.cs"] = "// Changed test\n" + test
            });
        fixture.AddAttempt("other-support-collection");

        var (report, _) = fixture.Run();

        var attempt = Assert.Single(report.Attempts);
        Assert.Equal("Resolved", attempt.SelectionOutcome);
        Assert.Null(attempt.DegradationKind);
        Assert.Equal(new[] { OtherProject + "/OtherTests.cs" }, attempt.OutOfInfrastructureTestFiles);
        Assert.Empty(report.UnmappedTestClasses!.Occurrences);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingConstants_ExcludePartialAndNestedClasses_KeepAttempt(bool sibling)
    {
        const string broken = """
            namespace SampleOther;
            [Xunit.Collection(MissingCollections.Gone)]
            public partial class BrokenTests { [Xunit.Fact] public void Works() { } }
            public class Container
            {
                [Xunit.Collection(MissingCollections.AlsoGone)]
                public class SecondBrokenTests { [Xunit.Fact] public void Works() { } }
            }
            """;
        var test = broken + (sibling
            ? "\npublic class SiblingTests { [Xunit.Fact] public void Works() { } }" : "");
        using var fixture = new ImpactReplayFixture(changedSourceCount: 0,
            initialRepositoryFiles: new Dictionary<string, string>
            {
                [OtherProject + "/Sample.Other.Tests.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\" />",
                [OtherProject + "/OtherTests.cs"] = test,
                [OtherProject + "/BrokenPart.cs"] = """
                    namespace SampleOther;
                    [Xunit.CollectionAttribute(MissingCollections.Gone)]
                    public partial class BrokenTests { }
                    """,
                // These same-named constants in an unrelated test project cannot resolve this project.
                ["tests/Unrelated.Tests/Unrelated.Tests.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\" />",
                ["tests/Unrelated.Tests/Constants.cs"] = """
                    public static class MissingCollections
                    {
                        public const string Gone = "Unrelated";
                        public const string AlsoGone = "Unrelated";
                    }
                    """
            }, changedRepositoryFiles: new Dictionary<string, string>
            {
                [OtherProject + "/OtherTests.cs"] = "// Changed test\n" + test
            });
        fixture.AddAttempt("missing-collection");

        var (report, _) = fixture.Run();

        Assert.Empty(report.UnreplayableAttempts);
        var attempt = Assert.Single(report.Attempts);
        Assert.Equal(sibling ? "Resolved" : "Abandoned", attempt.SelectionOutcome);
        Assert.Equal(sibling ? null : "ChangedTestSupportUnresolved", attempt.DegradationKind);
        Assert.Empty(attempt.SelectedTestClasses);
        var occurrences = report.UnmappedTestClasses!.Occurrences;
        Assert.Equal(2, occurrences.Count);
        var partial = Assert.Single(occurrences, item => item.Class == "BrokenTests");
        Assert.Equal("missing-collection", partial.AttemptId);
        Assert.Equal("unresolved-collection", partial.Reason);
        Assert.Equal(new[] { "SampleOther.BrokenTests" }, partial.FullNames);
        var nested = Assert.Single(occurrences, item => item.Class == "SecondBrokenTests");
        Assert.Equal("unresolved-collection", nested.Reason);
        Assert.Equal(new[] { "SampleOther.Container+SecondBrokenTests" }, nested.FullNames);
        if (sibling)
        {
            Assert.Equal(new[] { OtherProject + "/OtherTests.cs" }, attempt.OutOfInfrastructureTestFiles);
            Assert.DoesNotContain("Beta lane", attempt.WouldRunLanes);
        }
        else Assert.Empty(attempt.OutOfInfrastructureTestFiles!);
    }

    private static Dictionary<string, string> SupportFiles() => new()
    {
        ["config/acceptance-manifest.json"] = SupportManifest,
        ["tests/Sample.TestSupport/Sample.TestSupport.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\" />",
        ["tests/Sample.TestSupport/SupportCollections.cs"] = """
            public static class SupportCollections { public const string Shared = "Shared"; }
            """
    };
}
