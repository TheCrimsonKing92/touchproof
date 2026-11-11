using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: fixtures own their files/cache; live replays only read repository source.
public sealed class PreTesterGuardHelperAndScanTests
{
    [Theory]
    [InlineData("README.md", true, false)]
    [InlineData("docs/other.md", false, false)]
    [InlineData("README.md", true, true)]
    [InlineData("docs/other.md", false, true)]
    public void SameClassRead_ResolvesParameterAndLocal(string path, bool selected, bool local)
    {
        using var fixture = new SourceFixture();
        fixture.Declare($$"""
            [Fact] public void Reads() { ReadLines("README.md"); }
            private string ReadLines(string documentPath) {
                var root = FindRoot();
                {{(local ? "var path = Path.Combine(root, documentPath);" : "")}}
                return File.ReadAllText({{(local ? "path" : "Path.Combine(root, documentPath)")}});
            }
            """);

        fixture.AssertSelection(path, selected ? "helper-read" : null);
    }

    [Theory]
    [InlineData("ReadLines(documentPath: path)")]
    [InlineData("this.ReadLines(path)")]
    [InlineData("GuardTests.ReadLines(path)")]
    [InlineData("ReadLines<string>(path)")]
    public void HelperCallForms_ResolveArgumentsInCallingMethod(string call)
    {
        using var fixture = new SourceFixture();
        fixture.Declare($$"""
            [Fact] public void Reads() { var path = "README.md"; {{call}}; }
            public string ReadLines(string documentPath) => File.ReadAllText(documentPath);
            public string ReadLines<T>(string documentPath) => File.ReadAllText(documentPath);
            """);

        fixture.AssertSelection("README.md", "helper-read");
    }

    [Theory]
    [InlineData("Outer(\"README.md\")", "")]
    [InlineData("Other.ReadLines(\"README.md\")", "")]
    [InlineData("ReadLines(\"README.md\")", "private class Nested { string ReadLines(string p) => File.ReadAllText(p); }")]
    [InlineData("Recurse(\"README.md\")", "")]
    public void NonLocalOrSecondLevelCalls_AreNotFollowed(string call, string extra)
    {
        using var fixture = new SourceFixture();
        fixture.Declare($$"""
            [Fact] public void Reads() { {{call}}; }
            private void Outer(string p) { Inner(p); }
            private string Inner(string p) => File.ReadAllText(Path.Combine(root, p));
            private void Recurse(string p) { Recurse(p); }
            {{extra}}
            """);

        fixture.AssertSelection("README.md", null);
    }

    [Fact]
    public void UnresolvedHelperArgument_DoesNotBorrowOtherLiterals()
    {
        using var fixture = new SourceFixture();
        fixture.Declare("""
            [Fact] public void Reads() { var unrelated = "README.md"; ReadLines(Compute()); }
            private string ReadLines(string path) => File.ReadAllText(Path.Combine(root, path));
            """);

        fixture.AssertSelection("README.md", null);
    }

    [Fact]
    public void DirectLiteralRead_ReportsReadKind()
    {
        using var fixture = new SourceFixture();
        fixture.Declare("""
            [Fact] public void Reads() { File.ReadAllText("README.md"); }
            """);

        fixture.AssertSelection("README.md", "read");
    }

    [Theory]
    [InlineData("Directory.GetFiles", "src/A/B/C.cs", true)]
    [InlineData("Directory.GetFiles", "src/A/B/C.txt", false)]
    [InlineData("Directory.GetFiles", "tests/X.cs", false)]
    [InlineData("Directory.GetFiles", "srcx/C.cs", false)]
    [InlineData("Directory.GetFiles", "other/src/C.cs", false)]
    [InlineData("Directory.EnumerateFiles", "src/C.cs", true)]
    [InlineData("System.IO.Directory.GetFiles", "src/A/B/C.cs", true)]
    [InlineData("global::System.IO.Directory.EnumerateFiles", "./SRC\\A\\C.CS", true)]
    public void RecursiveScan_MatchesPrefixAndPattern(string api, string path, bool selected)
    {
        using var fixture = new SourceFixture();
        fixture.Declare($$"""
            [Fact] public void Scans() {
                var root = FindRoot();
                {{api}}(Path.Combine(root, "src"), "*.cs", System.IO.SearchOption.AllDirectories);
            }
            """);

        fixture.AssertSelection(path, selected ? "scan" : null);
    }

    [Theory]
    [InlineData("\"*.cs\"", "src/C.cs", true)]
    [InlineData("\"*.cs\"", "src/A/C.cs", false)]
    [InlineData("\"?.cs\"", "src/C.cs", true)]
    [InlineData("\"?.cs\"", "src/CC.cs", false)]
    [InlineData("\"?.cs\"", "src/.cs", false)]
    [InlineData("\"*.cs\"", "src/C.cs.bak", false)]
    [InlineData("\"C[1].cs\"", "src/C[1].cs", true)]
    [InlineData("DynamicPattern()", "src/C.txt", true)]
    public void TopOnlyScan_RestrictsDepthAndMatchesWholeFilename(string pattern, string path, bool selected)
    {
        using var fixture = new SourceFixture();
        fixture.Declare($$"""
            [Fact] public void Scans() {
                Directory.EnumerateFiles(Path.Combine(root, "src"), {{pattern}});
            }
            """);

        fixture.AssertSelection(path, selected ? "scan" : null);
    }

    [Fact]
    public void ScanDefaults_UseWildcardAndTopDirectory()
    {
        using var fixture = new SourceFixture();
        fixture.Declare("""
            [Fact] public void Scans() { Directory.GetFiles(Path.Combine(root, "src")); }
            """);

        fixture.AssertSelection("src/C.txt", "scan");
        fixture.AssertSelection("src/A/C.txt", null);
    }

    [Theory]
    [InlineData("src/A/C.cs", true)]
    [InlineData("src/A/C.txt", false)]
    [InlineData("srcx/C.cs", false)]
    public void HelperScan_BindsNamedDirectoryPatternAndOption(string path, bool selected)
    {
        using var fixture = new SourceFixture();
        fixture.Declare("""
            [Fact] public void Scans() {
                Scan(option: SearchOption.AllDirectories, pattern: "*.cs", directory: "src");
            }
            private void Scan(string directory, string pattern, SearchOption option) {
                var path = Path.Combine(root, directory);
                System.IO.Directory.EnumerateFiles(searchOption: option, path: path, searchPattern: pattern);
            }
            """);

        fixture.AssertSelection(path, selected ? "scan" : null);
    }

    [Fact]
    public void CompatibleOverloads_CollectEveryResolvableGuard()
    {
        using var fixture = new SourceFixture();
        fixture.Declare("""
            [Fact] public void Reads() { Read("README.md"); }
            private string Read(string p) => File.ReadAllText(p);
            private string Read(object p) => File.ReadAllText("other.md");
            """);

        fixture.AssertSelection("README.md", "helper-read");
        fixture.AssertSelection("other.md", "helper-read");
    }

    [Fact]
    public void MultipleMatches_UseChangedOrderThenKindPriorityPerRequest()
    {
        using var fixture = new SourceFixture();
        fixture.Declare("""
            [Fact] public void Reads() {
                Directory.GetFiles(Path.Combine(root, "src"));
                Read("src/C.cs");
                File.ReadAllText("src/C.cs");
            }
            private string Read(string p) => File.ReadAllText(p);
            """);

        var first = fixture.Select(["src/B.cs", "src/C.cs"]);
        Assert.Equal(new PreTesterChangedFileGuardClasses.Provenance("GuardTests", "scan", "src/B.cs"),
            Assert.Single(first.Provenance));
        var second = fixture.Select(["src/C.cs", "src/B.cs"]);
        Assert.Equal(new PreTesterChangedFileGuardClasses.Provenance("GuardTests", "read", "src/C.cs"),
            Assert.Single(second.Provenance));
        Assert.Single(first.Selection.Selections);
        Assert.Single(second.Selection.Selections);
    }

    [Theory]
    [InlineData("src/Mcg.AgentOrchestrator.App/Cli/CliTimingRecorder.cs", "EnvironmentVariableCensusTests", "scan")]
    [InlineData("README.md", "ReadmeFrontPageContractTests", "helper-read")]
    public void LiveRepository_ReplaysHistoricalMiss(string path, string guard, string kind)
    {
        var result = new PreTesterChangedFileGuardClasses().SelectWithProvenance(
            VerifiedRepositoryRoot.Find(), [path], "live-source");

        Assert.Contains(result.Selection.Selections, selection => selection.TestClass == guard);
        Assert.Equal(new PreTesterChangedFileGuardClasses.Provenance(guard, kind, path),
            Assert.Single(result.Provenance.Where(item => item.Class == guard)));
    }

    [Fact]
    public void Create_ReportsOneProgressLinePerChangedFileGuard()
    {
        var lines = new List<string>();
        var result = PreTesterGuardSelections.Create(new(), VerifiedRepositoryRoot.Find(),
            ["README.md", "README.md"], "live-source", lines.Add);

        Assert.Contains(result.Selections, selection => selection.TestClass == "ReadmeFrontPageContractTests");
        Assert.Equal("PRE_TESTER_GUARD_SELECTED class=ReadmeFrontPageContractTests kind=helper-read path=README.md",
            Assert.Single(lines.Where(line => line.StartsWith(
                "PRE_TESTER_GUARD_SELECTED class=ReadmeFrontPageContractTests ", StringComparison.Ordinal))));
        Assert.Equal(lines.Count, lines.Distinct(StringComparer.Ordinal).Count());
        var expected = new PreTesterChangedFileGuardClasses().SelectWithProvenance(
            VerifiedRepositoryRoot.Find(), ["README.md"], "live-source");
        Assert.Equal(expected.Selection.Selections.Count, lines.Count);
        foreach (var selection in expected.Selection.Selections)
            Assert.Single(lines.Where(line => line.StartsWith(
                $"PRE_TESTER_GUARD_SELECTED class={selection.TestClass} ", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("src/A/B.cs", true)]
    [InlineData("tests/A/B.cs", true)]
    [InlineData("src/A/B.txt", false)]
    [InlineData("other/A.cs", false)]
    public void SnapshotScanUsesRecursiveCsharpDependency(string path, bool selected)
    {
        using var fixture = new SourceFixture();
        fixture.Declare("""
            [Fact] public void Scans() {
                var snapshot = RepositorySourceSnapshotCache.Get(root);
                snapshot.EnumerateFiles(Path.Combine(root, "src"));
                snapshot.EnumerateFiles(Path.Combine(root, "tests"));
            }
            """);
        fixture.AssertSelection(path, selected ? "scan" : null);
    }

    [Fact]
    public void UnrelatedEnumerateFilesDoesNotBecomeSnapshotDependency()
    {
        using var fixture = new SourceFixture();
        fixture.Declare("""
            [Fact] public void Scans() {
                var snapshot = Other.Get(root);
                snapshot.EnumerateFiles(Path.Combine(root, "src"));
            }
            """);
        fixture.AssertSelection("src/A.cs", null);
    }

    [Theory]
    [InlineData("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/TesterRoleFocusNoWorkerTestExecutionTests.cs", true)]
    [InlineData("config/production-member-reflection-exemptions.json", true)]
    [InlineData("src/Mcg.AgentOrchestrator.Execution/Workers/WorkerArtifactWriter.cs", true)]
    [InlineData("config/unrelated.json", false)]
    public void ReflectionGuardTracksEveryRealInput(string path, bool selected)
    {
        var result = new PreTesterChangedFileGuardClasses().SelectWithProvenance(
            VerifiedRepositoryRoot.Find(), [path], "reflection-inputs");
        Assert.Equal(selected, result.Selection.Selections.Any(selection =>
            selection.TestClass == nameof(ProductionMemberReflectionGuardTests)));
    }

    private sealed class SourceFixture : IDisposable
    {
        private readonly string _root = ConductorDriverTests.CreateTempDirectory();
        private readonly PreTesterChangedFileGuardClasses _selector = new();
        private string Project => Path.Combine(_root, "tests", "Example.Tests");

        public SourceFixture()
        {
            Directory.CreateDirectory(Project);
            File.WriteAllText(Path.Combine(Project, "Example.Tests.csproj"), "<Project />");
        }

        public void Declare(string methods) => File.WriteAllText(Path.Combine(Project, "GuardTests.cs"),
            "public class GuardTests { " + methods + " }");

        public (DeveloperDeferredTestSelection Selection, IReadOnlyList<PreTesterChangedFileGuardClasses.Provenance> Provenance)
            Select(IReadOnlyList<string> paths) => _selector.SelectWithProvenance(_root, paths, "main-1");

        public void AssertSelection(string path, string? kind)
        {
            var result = Select([path]);
            Assert.Empty(result.Selection.NotRun);
            if (kind is null)
            {
                Assert.Empty(result.Selection.Selections);
                Assert.Empty(result.Provenance);
                return;
            }
            Assert.Equal(new FindingEvidenceSelection("Example.Tests", "GuardTests"),
                Assert.Single(result.Selection.Selections));
            Assert.Equal(new PreTesterChangedFileGuardClasses.Provenance("GuardTests", kind, path),
                Assert.Single(result.Provenance));
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
