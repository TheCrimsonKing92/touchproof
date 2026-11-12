using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static ImpactReplayDeclaredGuardSelectionTests;

// Parallel-safe: all inputs and artifacts stay in memory.
public sealed class ImpactReplayGuardCompatibilityTests
{
    private const string Guard = "namespace Sample; [LiveTreeStructural] public class Guard { [Fact] public void Works() {} }";

    [Fact]
    public void Apply_DeclaredGuards_PreservesCompleteSelection()
    {
        var original = ReverseDependencyTestSelection.Resolved(["Sample.Existing"],
            headroom: new(null, 8000, 1, null));
        var selected = Apply(Sources(Guard), ["src/Input.cs"], out var report,
            [new("Sample.Guard", null)], original);
        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selected.Outcome);
        Assert.Null(selected.DegradationKind);
        Assert.Equal(new[] { "Sample.Existing", "Sample.Guard" }, selected.TestClassNames);
        Assert.Equal(2, selected.Headroom!.SelectedTestClassCount);
        Assert.Single(report.Selections);
    }

    [Fact]
    public void Apply_DeduplicatesQualifiedAliasesAndAdditionalSelections()
    {
        var original = ReverseDependencyTestSelection.Resolved(["Guard"],
            headroom: new(null, 8000, 1, null));
        var selected = Apply(Sources(Guard), ["src/Input.cs"], out _,
            [new("Sample.Guard", null)], original, ["Sample.Guard"]);
        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selected.Outcome);
        Assert.Contains("Sample.Guard", selected.TestClassNames);
        Assert.Equal(1, selected.Headroom!.SelectedTestClassCount);
    }

    [Fact]
    public void Apply_ChangedTestClasses_PreservesCompleteSelection()
    {
        var original = ReverseDependencyTestSelection.Resolved([],
            headroom: new(null, 8000, 0, null));
        var selected = Apply(Sources(Guard), ["src/Input.cs"], out _,
            [new("Sample.Guard", null)], original, ["Sample.Added"]);
        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selected.Outcome);
        Assert.Null(selected.DegradationKind);
        Assert.Equal(new[] { "Sample.Added", "Sample.Guard" }, selected.TestClassNames);
        Assert.Equal(2, selected.Headroom!.SelectedTestClassCount);
    }

    [Theory]
    [InlineData(999)]
    [InlineData(1000)]
    public void Apply_LargeSelection_IncludesEveryClassAndGuard(int existing)
    {
        var original = ReverseDependencyTestSelection.Resolved(Enumerable.Range(0, existing).Select(index => $"Sample.Existing{index}"));
        var selected = Apply(Sources(Guard), ["src/Input.cs"], out _, [new("Sample.Guard", null)], original);
        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selected.Outcome);
        Assert.Null(selected.DegradationKind);
        Assert.Equal(original.TestClassNames.Append("Sample.Guard").Order(StringComparer.Ordinal), selected.TestClassNames);
    }

    [Fact]
    public void Apply_PreExistingDegradation_IsRetained()
    {
        var original = ReverseDependencyTestSelection.Abandoned(ReverseDependencyDegradationKind.ChangedSourceFileCount, "existing limit");
        Assert.Same(original, Apply(Sources(Guard), ["src/Input.cs"], out _, [new("Sample.Guard", null)], original));
    }

    [Fact]
    public void Apply_UnmappedScript_StillAbandonsWithDeclaredGuard()
    {
        var selected = Apply(Sources(Guard), ["src/Input.cs", "scripts/unmapped.ps1"], out _, [new("Sample.Guard", null)]);
        Assert.Equal(ReverseDependencyDegradationKind.ChangedFileUnmapped, selected.DegradationKind);
        Assert.Empty(selected.TestClassNames);
    }

    [Theory]
    [InlineData("scripts/run.sh")]
    [InlineData("tests/Foreign.Tests/Fixtures/data.json")]
    public void Apply_MappedScriptOrFixture_PreservesBothSelections(string path)
    {
        var code = Guard + $" public class MappingTests {{ [Fact] public void Works() {{ var path = \"{path}\"; }} }}";
        var selected = Apply(Sources(code), ["src/Input.cs", path], out _,
            [new("Sample.Guard", null), new("Sample.MappingTests", null)]);
        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selected.Outcome);
        Assert.Equal(new[] { "Sample.Guard", "Sample.MappingTests" }, selected.TestClassNames);
    }

    [Fact]
    public void Apply_DeclaredGuardReport_SerializesAndRendersOwnershipAndReasons()
    {
        var selected = Apply(Sources(Guard), ["src/Input.cs"], out var guards, [new("Sample.Guard", null)]);
        Assert.Contains("Sample.Guard", selected.TestClassNames);
        var attempt = new ImpactReplayAttemptRecord("case", "goal", DateTimeOffset.UnixEpoch, "base", "candidate",
            "Resolved", null, null, ["src/Input.cs"], [], ["floor"], ["floor"], ["floor"], [], 0, [], [], true, [],
            DeclaredGuards: guards);
        var report = new ImpactReplayReport("override", ["floor"], [attempt], [], [], []);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var guard = Assert.Single(json.RootElement.GetProperty("attempts")[0].GetProperty("declaredGuards").GetProperty("selections").EnumerateArray());
        Assert.Equal("Sample.Guard", guard.GetProperty("className").GetString());
        Assert.Equal("tests/Foreign.Tests/Foreign.Tests.csproj", guard.GetProperty("project").GetString());
        Assert.Equal("tests/Foreign.Tests/Guards.cs", guard.GetProperty("declarationPath").GetString());
        var markdown = ImpactReplayReportWriter.Render(report);
        foreach (var value in new[] { "case", "Sample.Guard", "tests/Foreign.Tests/Foreign.Tests.csproj",
                     "tests/Foreign.Tests/Guards.cs", "live-tree-structural", "src/Input.cs", guards.Selections[0].Reason })
            Assert.Contains(value, markdown);
        Assert.Equal(new[] { "floor" }, attempt.WouldRunLanes);
    }
}
