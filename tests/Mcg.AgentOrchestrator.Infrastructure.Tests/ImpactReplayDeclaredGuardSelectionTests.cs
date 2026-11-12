using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

// Parallel-safe: parsed source and explicit project ownership only; no I/O or assembly loading.
public sealed class ImpactReplayDeclaredGuardSelectionTests
{
    [Fact]
    public void ReflectionGuardRemainsSelectedDuringStructuralReplay()
    {
        var sources = Sources("""
            [LiveTreeStructural]
            public class ProductionMemberReflectionGuardTests {
                [Fact] public void Scans() {}
            }
            """);
        var selected = Apply(sources, ["src/NewProductionType.cs"], out var report,
            [new AcceptanceTestClassDescriptor("ProductionMemberReflectionGuardTests", null)],
            additionalClasses: ["ProductionMemberReflectionGuardTests"]);

        Assert.Contains("ProductionMemberReflectionGuardTests", selected.TestClassNames);
        Assert.Equal("live-tree-structural", Assert.Single(report.Selections).DependencyKind);
    }

    [Fact]
    public void Apply_UnchangedStructuralGuard_SelectsForUnrelatedTreeChange()
    {
        var sources = Sources("""
            namespace Sample;
            [global::LiveTreeStructuralAttribute]
            public class PreTesterTestSourceGuardClassesTests {
                [Fact] public void Works() { System.IO.Directory.EnumerateFiles("tests"); }
            }
            public class OrdinaryTests { [Fact] public void Works() {} }
            """);

        var selected = Apply(sources, ["src/Unrelated.cs"], out var report);

        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selected.Outcome);
        Assert.Equal(new[] { "Sample.PreTesterTestSourceGuardClassesTests" }, selected.TestClassNames);
        var guard = Assert.Single(report.Selections);
        Assert.Equal("tests/Foreign.Tests/Foreign.Tests.csproj", guard.Project);
        Assert.Equal("tests/Foreign.Tests/Guards.cs", guard.DeclarationPath);
        Assert.Equal("live-tree-structural", guard.DependencyKind);
        Assert.Equal(new[] { "src/Unrelated.cs" }, guard.TriggerPaths);
        Assert.Null(report.DegradationReason);
    }

    [Theory]
    [InlineData("addition", "tests/Foreign.Tests/WorkerBuildScopeToolCacheTests.cs")]
    [InlineData("removal", "tests/Foreign.Tests/RemovedTests.cs")]
    [InlineData("rename", "tests/Foreign.Tests/WorkerBuildScopeToolCacheDigestTests.cs")]
    public void Apply_MembershipChanges_SelectsUnchangedReflectedGuard(string kind, string path)
    {
        // No direct reference to inventory classes: Assembly.GetTypes is the missed dependency.
        var sources = Sources("""
            namespace Sample;
            [TestAssemblyMembership]
            public class IsolatedProcessSpawningRemainderChainTests {
                [Fact] public void Works() { typeof(IsolatedProcessSpawningRemainderChainTests).Assembly.GetTypes(); }
            }
            public class OrdinaryTests { [Fact] public void Works() {} }
            """, extra: kind == "removal" ? [] :
            [CSharpSyntaxTree.ParseText("namespace Sample; public class WorkerBuildScopeToolCacheTests { [Fact] public void Works() {} } " +
                "public class WorkerBuildScopeToolCacheDigestTests { [Fact] public void Works() {} }", path: path)]);
        var changed = kind == "rename" ? new[] { "tests/Foreign.Tests/OldTests.cs", path } : [path];

        string[] inventory = kind == "removal" ? [] :
            ["Sample.WorkerBuildScopeToolCacheDigestTests", "Sample.WorkerBuildScopeToolCacheTests"];
        var selected = Apply(sources, changed, out var report,
            [new("Sample.IsolatedProcessSpawningRemainderChainTests", null), new("Sample.OrdinaryTests", null),
                ..inventory.Select(name => new AcceptanceTestClassDescriptor(name, null))],
            additionalClasses: inventory);

        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selected.Outcome);
        Assert.Equal(inventory.Prepend("Sample.IsolatedProcessSpawningRemainderChainTests"), selected.TestClassNames);
        var guard = Assert.Single(report.Selections);
        Assert.Equal("test-assembly-membership", guard.DependencyKind);
        Assert.Equal(changed.Order(StringComparer.Ordinal), guard.TriggerPaths);
        Assert.Contains("membership", guard.Reason);
        Assert.DoesNotContain("RemovedTests", string.Join(",", selected.TestClassNames));
    }

    [Theory]
    [InlineData("LiveTreeStructural")]
    [InlineData("Sample.LiveTreeStructuralAttribute")]
    [InlineData("global::Sample.TestAssemblyMembershipAttribute")]
    public void Apply_QualifiedMarkers_PreservesQualifiedClassIdentity(string marker)
    {
        var sources = Sources($$"""
            namespace Sample { public class Outer { [{{marker}}] public class Guard { [Fact] public void Works() {} } } }
            namespace Other { public class Guard { [Fact] public void Works() {} } }
            """);
        var selected = Apply(sources, ["src/Input.cs"], out var report,
            [new("Sample.Outer+Guard", null), new("Other.Guard", null)]);
        Assert.Equal(new[] { "Sample.Outer+Guard" }, selected.TestClassNames);
        Assert.Equal("Sample.Outer+Guard", Assert.Single(report.Selections).ClassName);
    }

    [Fact]
    public void Apply_CompleteOrdinaryInventory_DoesNotInventGuards()
    {
        var sources = Sources("namespace Sample; public class OrdinaryTests { [Fact] public void Works() {} }");
        var selected = Apply(sources, ["src/Input.cs"], out var report,
            [new("Sample.OrdinaryTests", null)]);
        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selected.Outcome);
        Assert.Empty(selected.TestClassNames);
        Assert.Empty(report.Selections);
        Assert.Null(report.DegradationReason);
    }

    [Theory]
    [InlineData("missing-owner")]
    [InlineData("ambiguous-owner")]
    [InlineData("missing-class")]
    [InlineData("malformed-declaration")]
    [InlineData("missing-sources")]
    [InlineData("incomplete-sources")]
    public void Apply_UnavailableOrAmbiguousMetadata_DegradesInsteadOfOmitting(string fault)
    {
        var tree = CSharpSyntaxTree.ParseText(fault == "malformed-declaration"
            ? "namespace Sample; [LiveTreeStructural(123)] public class Guard { [Fact] public void Works() {} }"
            : "namespace Sample; [LiveTreeStructural] public class Guard { [Fact] public void Works() {} }",
            path: "tests/Foreign.Tests/Guards.cs");
        var projects = new List<ImpactReplayChangedSourceRouting.Project>();
        if (fault != "missing-owner") projects.Add(new("tests/Foreign.Tests/Foreign.Tests.csproj", false, [tree.FilePath]));
        if (fault == "ambiguous-owner") projects.Add(new("tests/Other.Tests/Other.Tests.csproj", false, [tree.FilePath]));
        var sources = new ImpactReplayTestSourceIndex(fault == "missing-sources" ? Array.Empty<SyntaxTree>() : [tree], projects);
        var selected = Apply(sources, ["src/Input.cs"], out var report,
            fault == "missing-class" ? [] : fault == "incomplete-sources"
                ? [new("Sample.Guard", null), new("Sample.MissingGuard", null)] : [new("Sample.Guard", null)]);

        Assert.Equal(ReverseDependencySelectionOutcome.Unreadable, selected.Outcome);
        Assert.Empty(selected.TestClassNames);
        Assert.False(string.IsNullOrWhiteSpace(report.DegradationReason));
        Assert.Equal(report.DegradationReason, selected.Reason);
    }

    [Theory]
    [InlineData("LiveTreeStructural")]
    [InlineData("TestAssemblyMembership")]
    public void Apply_BuildInputChange_SelectsDeclaredGuard(string marker)
    {
        var selected = Apply(Sources($"namespace Sample; [{marker}] public class Guard {{ [Fact] public void Works() {{}} }}"),
            ["Directory.Build.props"], out var report, [new("Sample.Guard", null)]);
        Assert.Equal(new[] { "Sample.Guard" }, selected.TestClassNames);
        Assert.Equal(new[] { "Directory.Build.props" }, Assert.Single(report.Selections).TriggerPaths);
    }

    [Fact]
    public void Apply_AttributeAlias_SelectsMembershipGuard()
    {
        var sources = Sources("""
            using Inventory = TestAssemblyMembershipAttribute;
            namespace Sample;
            [Inventory] public class Guard { [Fact] public void Works() {} }
            """);
        var selected = Apply(sources, ["tests/Foreign.Tests/Removed.cs"], out var report, [new("Sample.Guard", null)]);
        Assert.Equal(new[] { "Sample.Guard" }, selected.TestClassNames);
        Assert.Equal("test-assembly-membership", Assert.Single(report.Selections).DependencyKind);
    }

    [Fact]
    public void Apply_AmbiguousAlias_DegradesInsteadOfOmitting()
    {
        var sources = Sources("""
            using Inventory = TestAssemblyMembershipAttribute;
            using Inventory = System.ObsoleteAttribute;
            namespace Sample;
            [Inventory] public class Guard { [Fact] public void Works() {} }
            """);
        var selected = Apply(sources, ["src/Input.cs"], out var report, [new("Sample.Guard", null)]);
        Assert.Equal(ReverseDependencySelectionOutcome.Unreadable, selected.Outcome);
        Assert.Contains("alias is ambiguous", report.DegradationReason);
        Assert.Empty(selected.TestClassNames);
    }

    [Fact]
    public void Apply_CrossProjectFullNameCollision_Degrades()
    {
        var first = CSharpSyntaxTree.ParseText("namespace Sample; [LiveTreeStructural] public class Guard { [Fact] public void Works() {} }",
            path: "tests/First.Tests/Guard.cs");
        var second = CSharpSyntaxTree.ParseText("namespace Sample; public class Guard { [Fact] public void Works() {} }",
            path: "tests/Second.Tests/Guard.cs");
        var sources = new ImpactReplayTestSourceIndex(new[] { first, second },
            [new("tests/First.Tests/First.Tests.csproj", false, [first.FilePath]),
             new("tests/Second.Tests/Second.Tests.csproj", false, [second.FilePath])]);
        var selected = Apply(sources, ["src/Input.cs"], out var report, [new("Sample.Guard", null)]);
        Assert.Equal(ReverseDependencySelectionOutcome.Unreadable, selected.Outcome);
        Assert.Contains("ownership is missing or ambiguous", report.DegradationReason);
        Assert.Empty(selected.TestClassNames);
    }

    [Fact]
    public void Apply_OrdinaryDocumentChange_SelectsOnlyTopologyDependency()
    {
        var sources = Sources("""
            namespace Sample;
            [LiveTreeStructural] public class Topology { [Fact] public void Works() {} }
            [TestAssemblyMembership] public class Inventory { [Fact] public void Works() {} }
            public class Ordinary { [Fact] public void Works() {} }
            """);
        var selected = Apply(sources, ["docs/readme.md"], out var report,
            [new("Sample.Topology", null), new("Sample.Inventory", null), new("Sample.Ordinary", null)]);
        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selected.Outcome);
        Assert.Equal(new[] { "Sample.Topology" }, selected.TestClassNames);
        Assert.Equal("live-tree-structural", Assert.Single(report.Selections).DependencyKind);
    }

    internal static ImpactReplayTestSourceIndex Sources(string code, SyntaxTree[]? extra = null)
    {
        SyntaxTree[] trees = [CSharpSyntaxTree.ParseText(code, path: "tests/Foreign.Tests/Guards.cs"), ..extra ?? []];
        return new(trees, [new("tests/Foreign.Tests/Foreign.Tests.csproj", false, trees.Select(t => t.FilePath).ToArray())]);
    }

    internal static ReverseDependencyTestSelection Apply(ImpactReplayTestSourceIndex sources,
        string[] changed, out ImpactReplayDeclaredGuardReport report,
        AcceptanceTestClassDescriptor[]? classes = null, ReverseDependencyTestSelection? original = null,
        IReadOnlyList<string>? additionalClasses = null) =>
        ImpactReplaySelectionAugmentation.Apply(original ?? ReverseDependencyTestSelection.Resolved([]),
            changed, sources, classes ?? [new("Sample.PreTesterTestSourceGuardClassesTests", null), new("Sample.OrdinaryTests", null)],
            null, out report, additionalClasses, []);
}
