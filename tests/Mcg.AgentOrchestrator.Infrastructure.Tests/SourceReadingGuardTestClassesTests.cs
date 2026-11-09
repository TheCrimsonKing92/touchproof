using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each fixture owns small temporary source files; no subprocesses.
public sealed class SourceReadingGuardTestClassesTests
{
    [Fact]
    public void UnrelatedChangeAlwaysSelectsDirectTransitiveAndInheritedSnapshotReaders()
    {
        using var fixture = new ImpactReplaySelectionFixture();
        fixture.Add("Snapshot.cs", """
            public static class RepositorySourceSnapshotCache { public static RepositorySourceSnapshot Get(string root) => new RepositorySourceSnapshot(); }
            public class RepositorySourceSnapshot {
                public string GetText(string path) => path;
                public string[] GetLines(string path) => null;
                public string[] EnumerateFiles(string path) => null;
                public object GetSyntaxTree(string path) => null;
            }
            public static class Wrappers {
                public static string Read() => RepositorySourceSnapshotCache.Get("arbitrary root").GetText("any path");
                public static string Forward() => Read();
            }
            public abstract class BaseHelper { protected void ReadInherited() { Wrappers.Forward(); } }
            public abstract class BaseTest { [Fact] public void InheritedTest() { Wrappers.Read(); } }
            """);
        fixture.Add("Tests.cs", """
            namespace Sample;
            public class DirectTests { [Fact] public void Works() { RepositorySourceSnapshotCache.Get("any"); } }
            public class WrapperTests { [Fact] public void Works() { Wrappers.Forward(); } }
            public class InheritedTests : BaseHelper { [Fact] public void Works() { ReadInherited(); } }
            public class SnapshotTests { [Fact] public void Works(RepositorySourceSnapshot snapshot) { snapshot.GetLines("any"); snapshot.EnumerateFiles("any"); snapshot.GetSyntaxTree("any"); } }
            public class InheritedTestMethods : BaseTest { }
            public class FieldTests { private RepositorySourceSnapshot snapshot = RepositorySourceSnapshotCache.Get("any"); [Fact] public void Works() {} }
            public class OrdinaryTests { [Fact] public void Works() { var text = "src tests RepositorySourceSnapshotCache.Get"; } }
            public class DirectFileTests { [Fact] public void Works() { System.IO.File.ReadAllText("src/file.cs"); } }
            public class FilterTests { [Fact] public void Works() { SourceGuardFileFilter.Allows("src"); } }
            public class UnrelatedReceiverTests { [Fact] public void Works(Unrelated reader) { reader.GetText("src"); } }
            public class UnusedInheritedHelperTests : BaseHelper { [Fact] public void Works() {} }
            public class Unrelated { public void GetText(string path) {} }
            """);

        var sources = fixture.Sources;
        var guards = Assert.IsType<ImpactReplaySourceGuardReport>(SourceReadingGuardTestClasses.Read(sources, fixture.Classes));
        var selected = ImpactReplaySelectionAugmentation.Apply(ReverseDependencyTestSelection.Resolved(["Sample.ChangedTests"]),
            ["src/Unrelated.cs"], sources, fixture.Classes, guards);

        Assert.Equal(new[] { "Sample.DirectTests", "Sample.FieldTests", "Sample.InheritedTestMethods", "Sample.InheritedTests", "Sample.SnapshotTests", "Sample.WrapperTests" }, guards.Classes);
        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selected.Outcome);
        Assert.Equal(guards.Classes.Append("Sample.ChangedTests").Order(StringComparer.Ordinal), selected.TestClassNames);
        Assert.Equal(6, guards.Count);
        var text = new System.Text.StringBuilder();
        guards.AppendTo(text);
        foreach (var name in guards.Classes) Assert.Contains(name, text.ToString());
        foreach (var name in guards.HelperSymbols) Assert.Contains(name, text.ToString());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(guards));
        Assert.Equal(6, json.RootElement.GetProperty("Count").GetInt32());
    }

    [Fact]
    public void AbsentHelpersOmitReportAndPresentHelpersReportAnExplicitEmptySet()
    {
        using var fixture = new ImpactReplaySelectionFixture();
        fixture.Add("Tests.cs", "public class OrdinaryTests { [Fact] public void Works() {} }");
        Assert.Null(SourceReadingGuardTestClasses.Read(fixture.Sources, fixture.Classes));
        fixture.Add("Cache.cs", "public static class RepositorySourceSnapshotCache { public static object Get(string root) => null; }");
        var guards = Assert.IsType<ImpactReplaySourceGuardReport>(SourceReadingGuardTestClasses.Read(fixture.Sources, fixture.Classes));
        Assert.Empty(guards.Classes);
        var text = new System.Text.StringBuilder();
        guards.AppendTo(text);
        Assert.Contains("Always-selected guard classes: 0", text.ToString());
        Assert.Contains("None.", text.ToString());
    }
}
