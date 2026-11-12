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
    public void Apply_DeclaredGuardsOverflowEffectiveCap_AbandonsWithoutTruncation()
    {
        var original = ReverseDependencyTestSelection.Resolved(["Sample.Existing"],
            headroom: new(null, 8000, 1, 1, null));
        var selected = Apply(Sources(Guard), ["src/Input.cs"], out var report,
            [new("Sample.Guard", null)], original);
        Assert.Equal(ReverseDependencySelectionOutcome.Abandoned, selected.Outcome);
        Assert.Equal(ReverseDependencyDegradationKind.SelectedTestClassBound, selected.DegradationKind);
        Assert.Empty(selected.TestClassNames);
        Assert.Equal(2, selected.Headroom!.SelectedTestClassCount);
        Assert.Single(report.Selections);
    }

    [Fact]
    public void Apply_AtCap_DeduplicatesQualifiedAliasesAndAdditionalSelections()
    {
        var original = ReverseDependencyTestSelection.Resolved(["Guard"],
            headroom: new(null, 8000, 1, 1, null));
        var selected = Apply(Sources(Guard), ["src/Input.cs"], out _,
            [new("Sample.Guard", null)], original, ["Sample.Guard"]);
        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selected.Outcome);
        Assert.Contains("Sample.Guard", selected.TestClassNames);
        Assert.Equal(1, selected.Headroom!.SelectedTestClassCount);
    }

    [Fact]
    public void Apply_ChangedTestClassesOverflowCap_Abandons()
    {
        var original = ReverseDependencyTestSelection.Resolved([],
            headroom: new(null, 8000, 0, 1, null));
        var selected = Apply(Sources(Guard), ["src/Input.cs"], out _,
            [new("Sample.Guard", null)], original, ["Sample.Added"]);
        Assert.Equal(ReverseDependencyDegradationKind.SelectedTestClassBound, selected.DegradationKind);
        Assert.Empty(selected.TestClassNames);
    }

    [Theory]
    [InlineData(999, "Resolved")]
    [InlineData(1000, "Abandoned")]
    public void Apply_DefaultCap_IncludesGuardsInDistinctCount(int existing, string expected)
    {
        var original = ReverseDependencyTestSelection.Resolved(Enumerable.Range(0, existing).Select(index => $"Sample.Existing{index}"));
        var selected = Apply(Sources(Guard), ["src/Input.cs"], out _, [new("Sample.Guard", null)], original);
        Assert.Equal(expected, selected.Outcome.ToString());
        if (selected.Outcome == ReverseDependencySelectionOutcome.Abandoned)
        {
            Assert.Equal(ReverseDependencyDegradationKind.SelectedTestClassBound, selected.DegradationKind);
            Assert.Empty(selected.TestClassNames);
            Assert.Contains("1001 test classes", selected.Reason);
            Assert.Contains("1000-test-class bound", selected.Reason);
        }
        else Assert.Equal(1000, selected.TestClassNames.Count);
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
