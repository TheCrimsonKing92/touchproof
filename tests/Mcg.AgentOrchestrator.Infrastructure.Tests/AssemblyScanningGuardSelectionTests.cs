using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns a temporary checkout and the repository check only reads candidate sources.
public sealed class AssemblyScanningGuardSelectionTests
{
    private const string Guard = "FixtureTests.WidgetArchitectureTests";
    private const string ScannedProject = "src/Scanned/Scanned.csproj";
    private const string OtherProject = "src/Other/Other.csproj";
    private const string SameProjectChange = "src/Scanned/Unrelated.cs";
    private const string OtherProjectChange = "src/Other/Unrelated.cs";
    private const string TestDirectory = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests";
    private static readonly AcceptanceTestLane[] Lanes =
        [new("Architecture", $"FullyQualifiedName={Guard}")];

    [Fact]
    public void SameProjectChangePublishesScanReason()
    {
        using var fixture = new Repository();

        var record = fixture.Publish(SameProjectChange);

        Assert.Equal("Resolved", record.GetProperty("outcome").GetString());
        Assert.True(record.GetProperty("applied").GetBoolean());
        var lane = Assert.Single(record.GetProperty("lanes").EnumerateArray());
        Assert.Equal("narrowed", lane.GetProperty("disposition").GetString());
        Assert.Equal(Guard, Assert.Single(lane.GetProperty("selectedClasses").EnumerateArray()).GetString());
        AssertScanReason(record, ScannedProject);
    }

    [Fact]
    public void OtherProjectChangeDoesNotSelectGuard()
    {
        using var fixture = new Repository();

        var record = fixture.Publish(OtherProjectChange);

        Assert.Equal("Resolved", record.GetProperty("outcome").GetString());
        Assert.True(record.GetProperty("applied").GetBoolean());
        var lane = Assert.Single(record.GetProperty("lanes").EnumerateArray());
        Assert.Equal("skipped", lane.GetProperty("disposition").GetString());
        Assert.Empty(lane.GetProperty("selectedClasses").EnumerateArray());
        Assert.Equal(Guard, Assert.Single(lane.GetProperty("skippedClasses").EnumerateArray()).GetString());
        Assert.False(record.TryGetProperty("assemblyScanReasons", out _));
        AssertScanReason(fixture.Publish(SameProjectChange), ScannedProject);
    }

    [Fact]
    public void WorkerUsageSourceSelectsArchitectureGuard()
    {
        const string path = "src/Mcg.AgentOrchestrator.App/Orchestration/WorkerUsageSampleSource.cs";
        const string project = "src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj";
        const string guard = "OrchestrationCliCommandViewArchitectureTests";

        var input = AcceptanceImpactSelectionReader.ReadLive(VerifiedRepositoryRoot.Find(), [path]);

        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, input.Selection.Outcome);
        Assert.Contains(guard, input.Selection.TestClassNames);
        Assert.Contains(Assert.IsAssignableFrom<IReadOnlyList<AssemblyScanningGuardTestClasses.SelectionReason>>(
            input.AssemblyScanReasons), reason => reason.ClassName == guard &&
            reason.Kind == "assembly-scan" && reason.ScannedProject == project);
    }

    [Fact]
    public void CachedCallSiteTracksATypeMovedToAnotherProject()
    {
        using var fixture = new Repository();
        var cache = new SourceReaderAnalysisCache(Path.Combine(fixture.Root, ".scratch", "source-reader"));
        using var scope = SourceReaderAnalysisCache.Use(cache);
        AssertScanReason(fixture.Publish(SameProjectChange), ScannedProject);

        fixture.Move("src/Scanned/Widget.cs", "src/Other/Widget.cs");

        AssertScanReason(fixture.Publish(OtherProjectChange), OtherProject);
        Assert.False(fixture.Publish(SameProjectChange).TryGetProperty("assemblyScanReasons", out _));
        Assert.Equal(1, cache.AnalysisCount);
    }

    [Fact]
    public void SourceMovedOutOfTheScannedProjectStillSelectsGuard()
    {
        using var fixture = new Repository();
        const string destination = "src/Other/Moved.cs";
        fixture.CommitBaseline();
        fixture.Move(SameProjectChange, destination);
        fixture.Git("add", "-A");

        var record = fixture.Publish(destination);

        Assert.Equal("Resolved", record.GetProperty("outcome").GetString());
        AssertScanReason(record, ScannedProject);
    }

    private static void AssertScanReason(JsonElement record, string project)
    {
        var reason = Assert.Single(record.GetProperty("assemblyScanReasons").EnumerateArray());
        Assert.Equal(Guard, reason.GetProperty("className").GetString());
        Assert.Equal("assembly-scan", reason.GetProperty("kind").GetString());
        Assert.Equal(project, reason.GetProperty("scannedProject").GetString());
    }

    private sealed class Repository : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"mcg-assembly-scan-{Guid.NewGuid():N}");
        private string? _integrationBranch;

        internal Repository()
        {
            Write(ScannedProject, """<Project Sdk="Microsoft.NET.Sdk" />""");
            Write(OtherProject, """<Project Sdk="Microsoft.NET.Sdk" />""");
            Write("src/Scanned/Widget.cs", "namespace Scanned; public class Widget { }");
            Write("src/Other/OtherWidget.cs", "namespace Other; public class Widget { }");
            Write(SameProjectChange, "namespace Scanned; public class Unrelated { }");
            Write(OtherProjectChange, "namespace Other; public class OtherUnrelated { }");
            Write(TestDirectory + "/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <ProjectReference Include="../../src/Scanned/Scanned.csproj" />
                    <ProjectReference Include="../../src/Other/Other.csproj" />
                  </ItemGroup>
                </Project>
                """);
            Write("config/acceptance-manifest.json", """
                {
                  "engine": { "laneTestSourceRoots": ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests"] },
                  "checks": [{ "name": "Infrastructure tests", "type": "dotnet-test",
                    "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj" }]
                }
                """);
            Write(TestDirectory + "/DependencyIndexCache.cs", """
                using System.Reflection;
                internal static class DependencyIndexCache
                {
                    public static object Get(params Assembly[] assemblies) => new object();
                }
                """);
            Write(TestDirectory + "/WidgetArchitectureTests.cs", """
                using Widget = Scanned.Widget;
                namespace FixtureTests;
                public partial class WidgetArchitectureTests
                {
                    private static object Index => DependencyIndexCache.Get(typeof(Widget).Assembly);
                    [Fact] public void ChecksAssembly() { _ = Index; }
                }
                """);
            Write(TestDirectory + "/WidgetArchitectureTests.Secondary.cs", """
                namespace FixtureTests;
                public partial class WidgetArchitectureTests
                {
                    private static object SecondaryIndex => DependencyIndexCache.Get(typeof(global::Scanned.Widget).Assembly);
                }
                """);
        }

        internal JsonElement Publish(params string[] changes)
        {
            var session = new AcceptanceSelectedExecutionSession(AcceptanceSelectedExecutionMode.Enforce, Root, changes,
                integrationBranch: _integrationBranch);
            var observation = AcceptanceSelectedExecutionFixture.Observe(session, Lanes);
            using var document = JsonDocument.Parse(observation.ToJson());
            return document.RootElement.GetProperty("selectedExecution").Clone();
        }

        internal void Move(string from, string to) => File.Move(Path.Combine(Root, from), Path.Combine(Root, to));

        internal void CommitBaseline()
        {
            Git("init", "--quiet", "--template=");
            Git("symbolic-ref", "HEAD", "refs/heads/baseline");
            Git("add", ".");
            Git("commit", "-m", "Fixture snapshot");
            _integrationBranch = "baseline";
        }

        internal void Git(params string[] arguments)
        {
            var result = GitCli.Run(Root, ["-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
                "-c", "core.autocrlf=false", "-c", "commit.gpgsign=false", "-c", "core.hooksPath=.no-hooks", .. arguments]);
            Assert.True(result.Succeeded, result.Output);
        }

        private void Write(string path, string text)
        {
            var destination = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, text);
        }

        public void Dispose()
        {
            foreach (var path in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
    }
}
