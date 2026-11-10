using Mcg.AgentOrchestrator.App.Orchestration;
using Xunit;

// Parallel-safe: source-only selection with private filesystem fixtures; no test host.
public sealed class ExperimentReplaySelectionTests
{
    [Fact]
    public void ChangedTest_ArmsRemainDistinctAndUnionIncludesSafetyFloor()
    {
        using var fixture = new ExperimentReplayTestData();
        CreateProject(fixture.Root);
        var changed = new[] { "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ChangedTests.cs" };
        var a = ExperimentReplaySelectionReader.Select(fixture.Root, changed, "A");
        var b = ExperimentReplaySelectionReader.Select(fixture.Root, changed, "B");
        Assert.Equal(new[] { "Examples.ChangedTests" }, a.Classes);
        Assert.Contains("Examples.ChangedTests", b.Classes);
        Assert.Contains("Examples.FloorTests", b.Classes);
        Assert.Equal("resolved", a.Outcome);
        Assert.Equal("resolved", b.Outcome);
    }

    [Fact]
    public void Union_OutsideLaneRoot_SelectsImpactAndDeclaredGuards()
    {
        using var fixture = new ExperimentReplayTestData();
        CreateProject(fixture.Root);
        // CreateProject declares only Infrastructure.Tests as a lane source root.
        var project = Path.Combine(fixture.Root, "tests", "Mcg.AgentOrchestrator.Core.Tests");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "Mcg.AgentOrchestrator.Core.Tests.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(project, "TopologyTests.cs"),
            "namespace Examples; [LiveTreeStructural] public class TopologyTests { [Xunit.Fact] public void Test() {} }");
        File.WriteAllText(Path.Combine(project, "MembershipTests.cs"),
            "namespace Examples; [TestAssemblyMembership] public class MembershipTests { [Xunit.Fact] public void Test() {} }");
        File.WriteAllText(Path.Combine(project, "AssetTests.cs"),
            "namespace Examples; public class AssetTests { [Xunit.Fact] public void Test() { System.IO.File.ReadAllText(\"Fixtures/input.txt\"); } }");
        File.WriteAllText(Path.Combine(project, "UnrelatedTests.cs"),
            "namespace Examples; public class UnrelatedTests { [Xunit.Fact] public void Test() {} }");

        var changed = new[] { "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ChangedTests.cs", "Fixtures/input.txt" };
        var a = ExperimentReplaySelectionReader.Select(fixture.Root, changed, "A");
        var b = ExperimentReplaySelectionReader.Select(fixture.Root, changed, "B");

        Assert.Equal(new[] { "Examples.ChangedTests" }, a.Classes);
        Assert.Equal("resolved", b.Outcome);
        Assert.Equal(new[] { "Examples.AssetTests", "Examples.ChangedTests", "Examples.FloorTests",
            "Examples.MembershipTests", "Examples.TopologyTests" }, b.Classes);
        Assert.DoesNotContain("Examples.UnrelatedTests", b.Classes);
        Assert.Empty(b.Diagnostics);
        Assert.Contains("tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", b.Projects);
    }

    [Fact]
    public void DeletedClass_UsesConservativeInventoryInsteadOfEmptySuccess()
    {
        using var fixture = new ExperimentReplayTestData();
        CreateProject(fixture.Root);
        var result = ExperimentReplaySelectionReader.Select(fixture.Root, ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/DeletedTests.cs"], "A");
        Assert.Equal("fallback", result.Outcome);
        Assert.Equal(2, result.Classes.Count);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void CompileRemove_ExcludedFixture_IsAbsentFromRunnableInventory()
    {
        using var fixture = new ExperimentReplayTestData();
        CreateProject(fixture.Root);
        var project = Path.Combine(fixture.Root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        File.WriteAllText(Path.Combine(project, "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Remove=\"ExcludedTests.cs\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(project, "ExcludedTests.cs"), "namespace Examples; public class ExcludedTests { [Xunit.Fact] public void Test() {} }");
        var selection = ExperimentReplaySelectionReader.Select(fixture.Root, [], "A");
        Assert.Equal("fallback", selection.Outcome);
        Assert.Equal(new[] { "Examples.ChangedTests", "Examples.FloorTests" }, selection.Classes);
    }

    private static void CreateProject(string root)
    {
        var project = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(project, "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(project, "ChangedTests.cs"), "namespace Examples; public class ChangedTests { [Xunit.Fact] public void Test() {} }");
        File.WriteAllText(Path.Combine(project, "FloorTests.cs"), "namespace Examples; public class FloorTests { [Xunit.Fact] public void Test() {} }");
        File.WriteAllText(Path.Combine(root, "config", "acceptance-manifest.json"), """
            {"version":1,"engine":{"laneTestSourceRoots":["tests/Mcg.AgentOrchestrator.Infrastructure.Tests"],
            "infrastructureTestLanes":[{"name":"Chaos gate","filter":"FullyQualifiedName~FloorTests"}]},
            "checks":[{"name":"tests","type":"dotnet-test","project":"tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"}]}
            """);
    }
}
