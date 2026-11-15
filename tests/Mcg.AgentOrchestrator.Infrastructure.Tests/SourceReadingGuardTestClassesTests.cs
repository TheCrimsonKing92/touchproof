using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each fixture owns small temporary source files; no subprocesses.
public sealed class SourceReadingGuardTestClassesTests
{
    private const string WholeTreeSources = """
        public static class VerifiedRepositoryRoot { public static string Find() => "root"; }
        public static class TrackedFiles {
            public static void Read() => Forward();
            private static void Forward() {
                var start = new System.Diagnostics.ProcessStartInfo(System.OperatingSystem.IsWindows() ? "git.exe" : "git");
                foreach (var argument in new[] { "-c", "core.fsmonitor=false", "ls-files", "-z" })
                    start.ArgumentList.Add(argument);
                using var process = new System.Diagnostics.Process { StartInfo = start };
                process.Start();
            }
        }
        namespace Sample {
            public class WholeTreeTests {
                [Fact] public void Works() { VerifiedRepositoryRoot.Find(); TrackedFiles.Read(); }
            }
        }
        """;

    [Fact]
    public void WholeTreeReaderThroughHelperIsAGuardWithoutSnapshotCache()
    {
        using var fixture = new ImpactReplaySelectionFixture();
        fixture.Add("Helpers.cs", WholeTreeSources);

        var guards = Assert.IsType<ImpactReplaySourceGuardReport>(SourceReadingGuardTestClasses.Read(fixture.Sources, fixture.Classes));

        Assert.Equal(new[] { "Sample.WholeTreeTests" }, guards.Classes);
    }

    [Fact]
    public void SingleNamedFileReaderIsNotAGuardWhileWholeTreeReaderIs()
    {
        using var fixture = new ImpactReplaySelectionFixture();
        fixture.Add("Helpers.cs", WholeTreeSources);
        fixture.Add("SingleFile.cs", """
            namespace Sample;
            public class SingleFileTests {
                [Fact] public void Works() {
                    System.IO.File.ReadAllLines(System.IO.Path.Combine(VerifiedRepositoryRoot.Find(), "AGENTS.md"));
                }
            }
            """);

        var guards = Assert.IsType<ImpactReplaySourceGuardReport>(SourceReadingGuardTestClasses.Read(fixture.Sources, fixture.Classes));

        Assert.Contains("Sample.WholeTreeTests", guards.Classes);
        Assert.DoesNotContain("Sample.SingleFileTests", guards.Classes);
    }

    [Fact]
    public void UnrelatedClassIsAbsentAndSnapshotReaderRemains()
    {
        using var fixture = new ImpactReplaySelectionFixture();
        fixture.Add("Helpers.cs", WholeTreeSources);
        fixture.Add("Snapshot.cs", """
            public static class RepositorySourceSnapshotCache { public static object Get(string root) => null; }
            namespace Sample {
                public class SnapshotTests { [Fact] public void Works() { RepositorySourceSnapshotCache.Get("root"); } }
                public class UnrelatedTests { [Fact] public void Works() { var value = 1; } }
            }
            """);

        var guards = Assert.IsType<ImpactReplaySourceGuardReport>(SourceReadingGuardTestClasses.Read(fixture.Sources, fixture.Classes));

        Assert.Contains("Sample.SnapshotTests", guards.Classes);
        Assert.DoesNotContain("Sample.UnrelatedTests", guards.Classes);
        Assert.Equal(new[] { "Sample.SnapshotTests", "Sample.WholeTreeTests" }, guards.Classes);
    }

    [Theory]
    [InlineData("System.IO.Directory.EnumerateFiles(root, \"*.cs\", System.IO.SearchOption.AllDirectories)")]
    [InlineData("System.IO.Directory.GetFiles(root, \"*.cs\", new System.IO.EnumerationOptions { RecurseSubdirectories = true })")]
    [InlineData("System.IO.Directory.EnumerateFileSystemEntries(root, \"*\", System.IO.SearchOption.AllDirectories)")]
    [InlineData("System.IO.Directory.GetFileSystemEntries(root, \"*\", System.IO.SearchOption.AllDirectories)")]
    public void RecursiveDirectoryEnumerationIsAGuard(string enumeration)
    {
        using var fixture = new ImpactReplaySelectionFixture();
        fixture.Add("Helpers.cs", WholeTreeSources);
        fixture.Add("DirectoryTests.cs", $$"""
            namespace Sample;
            public class DirectoryTests {
                [Fact] public void Works() { var root = VerifiedRepositoryRoot.Find(); var files = {{enumeration}}; }
            }
            """);

        var guards = Assert.IsType<ImpactReplaySourceGuardReport>(SourceReadingGuardTestClasses.Read(fixture.Sources, fixture.Classes));

        Assert.Contains("Sample.DirectoryTests", guards.Classes);
    }

    [Theory]
    [InlineData("System.IO.Directory.EnumerateFiles(root)")]
    [InlineData("System.IO.Directory.GetFiles(root, \"*.cs\", System.IO.SearchOption.TopDirectoryOnly)")]
    [InlineData("System.IO.Directory.GetFiles(root, \"*.cs\", new System.IO.EnumerationOptions { RecurseSubdirectories = false })")]
    [InlineData("System.IO.Directory.GetFiles(System.IO.Path.Combine(root, \"src\"), \"*\", System.IO.SearchOption.AllDirectories)")]
    [InlineData("System.IO.Directory.GetFiles(subtree, \"*\", System.IO.SearchOption.AllDirectories)")]
    public void TopLevelAndConfinedDirectoryEnumerationAreNotGuards(string enumeration)
    {
        using var fixture = new ImpactReplaySelectionFixture();
        fixture.Add("Helpers.cs", WholeTreeSources);
        fixture.Add("DirectoryTests.cs", $$"""
            namespace Sample;
            public class DirectoryTests {
                [Fact] public void Works() {
                    var root = VerifiedRepositoryRoot.Find();
                    var subtree = System.IO.Path.Combine(root, "src");
                    var files = {{enumeration}};
                }
            }
            """);

        var guards = Assert.IsType<ImpactReplaySourceGuardReport>(SourceReadingGuardTestClasses.Read(fixture.Sources, fixture.Classes));

        Assert.DoesNotContain("Sample.DirectoryTests", guards.Classes);
        Assert.Contains("Sample.WholeTreeTests", guards.Classes);
    }

    [Fact]
    public void RecursiveHelpersAndInheritedTestsReachBothOperations()
    {
        using var fixture = new ImpactReplaySelectionFixture();
        fixture.Add("Helpers.cs", WholeTreeSources);
        fixture.Add("Recursive.cs", """
            namespace Sample;
            public abstract class BaseTest {
                [Fact] public void Works() { Walk(VerifiedRepositoryRoot.Find()); }
                private static void Walk(string root) {
                    System.IO.Directory.EnumerateFiles(root);
                    foreach (var child in System.IO.Directory.EnumerateDirectories(root)) Forward(child);
                }
                private static void Forward(string root) { Walk(root); }
            }
            public class InheritedTests : BaseTest { }
            public class RootOnlyTests { [Fact] public void Works() { VerifiedRepositoryRoot.Find(); } }
            public class EnumerationOnlyTests {
                [Fact] public void Works() { System.IO.Directory.GetFiles("root", "*", System.IO.SearchOption.AllDirectories); }
            }
            """);

        var guards = Assert.IsType<ImpactReplaySourceGuardReport>(SourceReadingGuardTestClasses.Read(fixture.Sources, fixture.Classes));

        Assert.Contains("Sample.InheritedTests", guards.Classes);
        Assert.DoesNotContain("Sample.RootOnlyTests", guards.Classes);
        Assert.DoesNotContain("Sample.EnumerationOnlyTests", guards.Classes);
    }

    [Theory]
    [InlineData("System.Diagnostics.Process.Start(\"git\", \"ls-files -z\");", true)]
    [InlineData("System.Diagnostics.Process.Start(\"git\", \"ls-files -- *.cs\");", true)]
    [InlineData("var command = \"ls-files -z\"; System.Diagnostics.Process.Start(\"git.exe\", command);", true)]
    [InlineData("var start = new System.Diagnostics.ProcessStartInfo(\"git\", \"ls-files --eol -z\"); System.Diagnostics.Process.Start(start);", true)]
    [InlineData("var start = new System.Diagnostics.ProcessStartInfo { FileName = \"git\", ArgumentList = { \"ls-files\", \"-z\" } }; System.Diagnostics.Process.Start(start);", true)]
    [InlineData("var text = \"git ls-files\";", false)]
    [InlineData("System.Console.WriteLine(\"git ls-files\");", false)]
    [InlineData("var start = new System.Diagnostics.ProcessStartInfo(\"git\", \"ls-files\");", false)]
    [InlineData("System.Diagnostics.Process.Start(\"echo\", \"git ls-files\");", false)]
    [InlineData("System.Diagnostics.Process.Start(\"git\", \"show ls-files\");", false)]
    [InlineData("System.Diagnostics.Process.Start(\"git\", \"ls-files -- src/one.cs\");", false)]
    [InlineData("System.Diagnostics.Process.Start(\"git\", \"ls-files src\");", false)]
    public void GitTraversalRequiresAnInvocationAndNoPathRestriction(string body, bool expected)
    {
        using var fixture = new ImpactReplaySelectionFixture();
        fixture.Add("Helpers.cs", WholeTreeSources);
        fixture.Add("GitTests.cs", $$"""
            namespace Sample;
            public class GitTests { [Fact] public void Works() { VerifiedRepositoryRoot.Find(); {{body}} } }
            """);

        var guards = Assert.IsType<ImpactReplaySourceGuardReport>(SourceReadingGuardTestClasses.Read(fixture.Sources, fixture.Classes));

        Assert.Equal(expected, guards.Classes.Contains("Sample.GitTests"));
        Assert.Contains("Sample.WholeTreeTests", guards.Classes);
    }

    [Fact]
    public void GitCommandArgumentsFlowThroughResolvableHelpersWithoutMarkingOtherCommands()
    {
        using var fixture = new ImpactReplaySelectionFixture();
        fixture.Add("Helpers.cs", WholeTreeSources);
        // Keep synthetic launches out of the raw-source direct-git-launch guard.
        const string gitExecutable = "git";
        fixture.Add("CommandHelpers.cs", $$"""
            namespace Sample;
            public static class Commands {
                public static void Forward(string command) { Run(command); }
                private static void Run(string command) { System.Diagnostics.Process.Start("{{gitExecutable}}", command); }
                public static void RunList(string[] arguments) {
                    var start = new System.Diagnostics.ProcessStartInfo("{{gitExecutable}}");
                    foreach (var argument in arguments) start.ArgumentList.Add(argument);
                    System.Diagnostics.Process.Start(start);
                }
            }
            public class StringCommandTests {
                [Fact] public void Works() { VerifiedRepositoryRoot.Find(); Commands.Forward("ls-files -z"); }
            }
            public class ArgumentListTests {
                [Fact] public void Works() { VerifiedRepositoryRoot.Find(); Commands.RunList(["ls-files", "-z"]); }
            }
            public class OtherCommandTests {
                [Fact] public void Works() { VerifiedRepositoryRoot.Find(); Commands.Forward("status"); }
            }
            """);

        var guards = Assert.IsType<ImpactReplaySourceGuardReport>(SourceReadingGuardTestClasses.Read(fixture.Sources, fixture.Classes));

        Assert.Contains("Sample.StringCommandTests", guards.Classes);
        Assert.Contains("Sample.ArgumentListTests", guards.Classes);
        Assert.DoesNotContain("Sample.OtherCommandTests", guards.Classes);
    }

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
            ["src/Unrelated.cs"], sources, fixture.Classes, guards, []);

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
