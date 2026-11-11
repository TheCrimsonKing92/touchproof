using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Reflection;

// Parallel-safe: private candidate trees, injected topology, no process launches.
public sealed class PreTesterCoupledClassSelectionsTests
{
    [Fact]
    public void RenamedCoreTypeSelectsConsumersAsCoupledImpact()
    {
        using var fixture = new Fixture();
        fixture.Write(Fixture.OldSource, "public class OldContract {}");
        fixture.Test("FirstConsumerTests", "OldContract");
        fixture.Test("SecondConsumerTests", "OldContract");
        fixture.Test("UnrelatedTests");
        Assert.Equal(2, fixture.Select(Fixture.OldSource).Selections.Count);
        File.Delete(Path.Combine(fixture.Root, Fixture.OldSource));
        fixture.Write(Fixture.Source, "public class NewContract {}");
        fixture.Test("FirstConsumerTests", "NewContract");
        fixture.Test("SecondConsumerTests", "NewContract");

        var result = fixture.Select(Fixture.OldSource, Fixture.Source);

        Assert.Equal(["FirstConsumerTests", "SecondConsumerTests"], result.Selections.Select(item => item.TestClass).Order());
        Assert.All(result.Selections, item => Assert.Equal("Infrastructure.Tests", item.TestProject));
        Assert.All(result.Provenance, item => Assert.Equal("coupled-impact", item.Kind));
        Assert.All(result.Selections, item => Assert.Contains(result.Provenance, reason =>
            reason.Class == item.TestClass && reason.Path == Fixture.Source));
        Assert.DoesNotContain(result.Selections, item => item.TestClass == "UnrelatedTests");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DegradationRetainsOnlyPlannerNamedCoupledClasses(bool broad)
    {
        using var fixture = new Fixture();
        fixture.Write(Fixture.Source, "public class NewContract {}");
        fixture.Write("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Consumers.cs",
            string.Join("\n", Enumerable.Range(0, ReverseDependencyTestImpactReader.MaximumSelectedTestClasses + 1)
                .Select(index => $"public class Consumer{index}Tests {{ [Fact] public void Works() {{ _ = new NewContract(); }} }}")));
        fixture.Test("TreeReaderTests", prefix: "[LiveTreeStructural]");
        fixture.Write("Directory.Build.props", "<Project />");
        var paths = broad ? new[] { "Directory.Build.props" } : [Fixture.Source];
        var plan = ManifestScopedImpactPlan.Plan(paths, fixture.Root, fixture.Settings.DeclaredDotnetTestChecks);
        if (broad) Assert.True(plan.RequiresBroadVerification);
        else Assert.Equal(ReverseDependencyDegradationKind.SelectedTestClassBound, plan.ReverseDependencyDegradation?.Kind);
        var lines = new List<string>();

        var result = PreTesterCoupledClassSelections.Select(fixture.Root, paths, fixture.Settings, lines.Add);

        var expected = plan.Checks.SelectMany(check => check.TestClassSelections ?? []).Order().ToArray();
        Assert.Equal(expected, result.Provenance.Where(item => item.Kind == "coupled-impact")
            .Select(item => item.Class).Distinct().Order().ToArray());
        var line = Assert.Single(lines.Where(line => line.Contains("kind=coupled-impact-degraded", StringComparison.Ordinal)));
        Assert.Contains(broad ? "RequiresBroadVerification" : "SelectedTestClassBound", line, StringComparison.Ordinal);
        Assert.Equal("TreeReaderTests", Assert.Single(result.Provenance.Where(item => item.Kind == "live-tree-structural")).Class);
    }

    [Fact]
    public void AddedTestClassSelectionEqualsPlannerPlusExistingGuards()
    {
        using var fixture = new Fixture();
        fixture.Test("ExistingTests");
        fixture.Test("CallerFilePathRootSourceGuardTests");
        fixture.Test("StructuralTests", prefix: "[LiveTreeStructural]");
        const string existing = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ExistingTests.cs";
        const string added = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/AddedTests.cs";
        var before = PreTesterGuardSelections.Create(new(), fixture.Root, [existing], "fixture-base",
            _ => { }, fixture.Settings, structuralPathReader: (_, _) => []).Selections;
        fixture.Test("AddedTests");
        string[] paths = [existing, added];
        var plan = ManifestScopedImpactPlan.Plan(paths, fixture.Root, fixture.Settings.DeclaredDotnetTestChecks);
        var planner = plan.Checks.SelectMany(check => check.TestClassSelections ?? [])
            .Select(name => "Infrastructure.Tests:" + name);
        var existingGuards = PreTesterAlwaysRunGuardTestClasses.Select(fixture.Root)
            .Concat(PreTesterTestSourceGuardClasses.Select(fixture.Root, paths))
            .Concat(new PreTesterChangedFileGuardClasses().Select(fixture.Root, paths, "fixture-base").Selections)
            .Concat(PreTesterCliSurfaceSelections.Select(fixture.Root, paths, fixture.Settings).Selections);

        var after = PreTesterGuardSelections.Create(new(), fixture.Root, paths, "fixture-base",
            _ => { }, fixture.Settings, structuralPathReader: (_, _) => [added]);
        var augmented = after.Augment(new([], []), fixture.Settings, null, ConductorDriver.FormatFindingEvidenceSelection);

        Assert.Contains("Infrastructure.Tests:AddedTests", planner);
        var expected = DeveloperDeferredTestSelectionNormalization.Normalize(new(existingGuards.ToArray(), []), fixture.Settings)
            .Selections.Select(ConductorDriver.FormatFindingEvidenceSelection).Concat(planner)
            .Append("Infrastructure.Tests:StructuralTests")
            .Distinct().Order().ToArray();
        Assert.Equal(expected, after.NormalizeWithNotRun(fixture.Settings).Selections
            .Select(ConductorDriver.FormatFindingEvidenceSelection).Distinct().Order().ToArray());
        Assert.Equal(expected, augmented.Selections.Select(ConductorDriver.FormatFindingEvidenceSelection).Order().ToArray());
        Assert.All(before, selection => Assert.Contains(selection, after.Selections));
        Assert.Empty(augmented.NotRun);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedProjectTestChangeRetainsPlannerSelectionInItsOwningProject(bool namespaced)
    {
        using var fixture = new Fixture();
        const string project = PreTesterCliSurfaceSelections.Project;
        const string changed = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/NestedConsumerTests.cs";
        fixture.Write(project, "<Project />");
        fixture.Write(changed, (namespaced ? "namespace Fixture; " : "") +
            "public class NestedConsumerTests { [Fact] public void Works() {} }");
        fixture.Write("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/UnrelatedTests.cs",
            "public class UnrelatedTests { [Fact] public void Works() {} }");
        fixture.Test("UmbrellaTests");
        var settings = AcceptanceGateEngineSettings.Parse("""
            { "checks": [
              { "name": "cli", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj" }
            ] }
            """);
        var plan = ManifestScopedImpactPlan.Plan([changed], fixture.Root, settings.DeclaredDotnetTestChecks);
        var check = Assert.Single(plan.Checks);
        Assert.Contains(project, check.Command);
        Assert.Equal(["NestedConsumerTests"], check.TestClassSelections);
        var lines = new List<string>();

        var result = PreTesterCoupledClassSelections.Select(fixture.Root, [changed], settings, lines.Add, []);

        Assert.Equal(new FindingEvidenceSelection("Infrastructure.Cli.Tests", "NestedConsumerTests"),
            Assert.Single(result.Selections));
        var reason = Assert.Single(result.Provenance);
        Assert.Equal("NestedConsumerTests", reason.Class);
        Assert.Equal("coupled-impact", reason.Kind);
        Assert.Equal(changed, reason.Path);
        Assert.DoesNotContain(lines, line => line.Contains("kind=coupled-impact-unresolved", StringComparison.Ordinal));
    }

    [Fact]
    public void UnresolvedPlannerHintsDoNotBecomeExecutionObligations()
    {
        using var fixture = new Fixture();
        var path = "src/Mcg.AgentOrchestrator.App/Cli/Handler.cs";
        fixture.Write(path, "public class Driver {}");
        var lines = new List<string>();
        var result = PreTesterCoupledClassSelections.Select(fixture.Root, [path], fixture.Settings, lines.Add);
        Assert.Empty(result.Selections);
        Assert.Empty(result.Provenance);
        Assert.Contains(lines, line => line.Contains("kind=coupled-impact-unresolved", StringComparison.Ordinal));
    }

    [Fact]
    public void TopologyParserTreatsRenamesAsDeleteAndAddAndPreservesSpaces()
    {
        Assert.Equal(["src/Old Name.cs", "tests/New Name.cs"], PreTesterCoupledClassSelections.ParseStructuralPaths(
            "D\0src/Old Name.cs\0A\0tests/New Name.cs\0M\0src/Edited.cs\0"));
        Assert.Throws<InvalidDataException>(() => PreTesterCoupledClassSelections.ParseStructuralPaths("A\0"));
    }

    [Theory]
    [InlineData("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/AddedTests.cs", true)]
    [InlineData("src/Mcg.AgentOrchestrator.Core/Removed.cs", true)]
    [InlineData("src/Mcg.AgentOrchestrator.Core/Edited.cs", false)]
    [InlineData("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/EditedTests.cs", false)]
    [InlineData(Fixture.Project, false)]
    [InlineData("Mcg.AgentOrchestrator.sln", false)]
    [InlineData("Mcg.AgentOrchestrator.slnx", false)]
    [InlineData("Directory.Build.props", false)]
    [InlineData("Directory.Build.targets", false)]
    [InlineData("config/acceptance-manifest.json", false)]
    public void StructuralChangesSelectEveryMarkedClass(string changed, bool topologyChanged)
    {
        using var fixture = new Fixture();
        fixture.Test("TreeReaderTests", prefix: "[LiveTreeStructural]");
        fixture.Test("OtherTreeReaderTests", prefix: "[global::LiveTreeStructuralAttribute]");
        fixture.Test("UnmarkedTests");
        fixture.Write("tests/Mcg.AgentOrchestrator.Core.Tests/CoreTreeReaderTests.cs",
            "[LiveTreeStructuralAttribute] public class CoreTreeReaderTests {}");
        var lines = new List<string>();

        var result = PreTesterCoupledClassSelections.Select(fixture.Root, [changed], fixture.Settings, lines.Add,
            topologyChanged ? [changed] : []);

        var structural = result.Provenance.Where(item => item.Kind == "live-tree-structural").ToArray();
        var ordinaryEdit = !topologyChanged && changed.EndsWith(".cs", StringComparison.Ordinal);
        if (ordinaryEdit)
        {
            Assert.Empty(structural);
            return;
        }
        Assert.Equal(["CoreTreeReaderTests", "OtherTreeReaderTests", "TreeReaderTests"],
            structural.Select(item => item.Class).Order().ToArray());
        Assert.All(structural, item => Assert.Equal(changed, item.Path));
        Assert.Contains(new FindingEvidenceSelection("Core.Tests", "CoreTreeReaderTests"), result.Selections);
        Assert.DoesNotContain(structural, item => item.Class == "UnmarkedTests");
    }

    [Fact]
    public void MissingTopologySelectsStructuralClassesAndReportsUnavailable()
    {
        using var fixture = new Fixture();
        fixture.Test("TreeReaderTests", prefix: "[LiveTreeStructural]");
        var lines = new List<string>();
        var result = PreTesterCoupledClassSelections.Select(fixture.Root, [Fixture.Source], null, lines.Add);
        Assert.Equal("TreeReaderTests", Assert.Single(result.Selections).TestClass);
        Assert.Equal("live-tree-structural", Assert.Single(result.Provenance).Kind);
        Assert.Contains("kind=live-tree-structural-unavailable", Assert.Single(lines), StringComparison.Ordinal);
    }

    [Fact]
    public void DiscoveryReadsClassAttributesRatherThanNamesOrComments()
    {
        using var fixture = new Fixture();
        fixture.Test("ArbitraryTests", prefix: "[global::LiveTreeStructural]");
        fixture.Test("LiveTreeStructuralTests", prefix: "// [LiveTreeStructural]\n");
        fixture.Test("StringTests", prefix: "[Other(\"LiveTreeStructural\")]");
        fixture.Write("tests/Mcg.AgentOrchestrator.Core.Tests/QualifiedTests.cs",
            "[Support.LiveTreeStructuralAttribute] public class QualifiedTests {}");

        var marked = PreTesterLiveTreeStructuralSelections.DiscoverMarkedClasses(fixture.Root);

        Assert.Equal(["ArbitraryTests", "QualifiedTests"], marked.Select(item => item.TestClass).Order().ToArray());
        fixture.Write("tests/Mcg.AgentOrchestrator.Core.Tests/BrokenTests.cs", "[LiveTreeStructural] public class {");
        Assert.Throws<InvalidDataException>(() => PreTesterLiveTreeStructuralSelections.DiscoverMarkedClasses(fixture.Root));
    }

    [Fact]
    public void MarkedClassDiscoveryMatchesReflectedAttributeHolders()
    {
        var root = VerifiedRepositoryRoot.Find();
        var current = typeof(PreTesterCoupledClassSelectionsTests).Assembly;
        var configuration = current.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var reflected = new List<FindingEvidenceSelection>();
        // Include every project mentioning the marker, even if an alias hides an application from discovery.
        foreach (var directory in Directory.EnumerateDirectories(Path.Combine(root, "tests")))
        {
            var project = Directory.EnumerateFiles(directory, "*.csproj").SingleOrDefault();
            if (project is null || !DeveloperDeferredTestSelections.EnumerateSourceFiles(directory)
                    .Any(path => File.ReadAllText(path).Contains("LiveTreeStructural", StringComparison.Ordinal))) continue;
            var name = Path.GetFileNameWithoutExtension(project);
            var assembly = current;
            if (name != current.GetName().Name)
            {
                var location = TestBuildOutputLocator.Locate(name, configuration, requiredFileName: name + ".dll");
                Assert.True(location.Directory is not null, "Missing parity assembly: " + string.Join(", ", location.TriedPaths));
                assembly = Assembly.LoadFrom(Path.Combine(location.Directory!, name + ".dll"));
            }
            reflected.AddRange(assembly.GetTypes().Where(type => type.CustomAttributes.Any(attribute =>
                    attribute.AttributeType.FullName == typeof(LiveTreeStructuralAttribute).FullName))
                .Select(type => new FindingEvidenceSelection(Path.GetFileName(directory), type.Name)));
        }
        Assert.NotEmpty(reflected);
        Assert.Equal(reflected.Select(ConductorDriver.FormatFindingEvidenceSelection).Order().ToArray(),
            PreTesterLiveTreeStructuralSelections.DiscoverMarkedClasses(root)
                .Select(ConductorDriver.FormatFindingEvidenceSelection).Order().ToArray());
    }

    internal sealed class Fixture : IDisposable
    {
        internal const string Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
        internal const string OldSource = "src/Mcg.AgentOrchestrator.Core/OldContract.cs";
        internal const string Source = "src/Mcg.AgentOrchestrator.Core/NewContract.cs";
        internal string Root { get; }
        internal AcceptanceGateEngineSettings Settings { get; } = AcceptanceGateEngineSettings.Parse("""
            { "checks": [
              { "name": "infra", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj" },
              { "name": "core", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj" }
            ] }
            """);
        internal Fixture(string? root = null)
        {
            Root = root ?? ConductorDriverTests.CreateTempDirectory();
            Write(".git", "gitdir: fixture");
            Write("Mcg.AgentOrchestrator.sln", "");
            Write("src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj", "<Project />");
            Write(Project, """
                <Project><ItemGroup><ProjectReference Include="../../src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj" /></ItemGroup></Project>
                """);
            Write("tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", "<Project />");
        }
        internal void Test(string name, string? dependency = null, string prefix = "") =>
            Write("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/" + name + ".cs", prefix + " public class " + name +
                " { [Fact] public void Works() { " + (dependency is null ? "" : "_ = new " + dependency + "();") + " } }");
        internal void Write(string path, string text)
        {
            var target = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, text);
        }
        internal (IReadOnlyList<FindingEvidenceSelection> Selections,
            IReadOnlyList<PreTesterChangedFileGuardClasses.Provenance> Provenance) Select(params string[] paths) =>
            PreTesterCoupledClassSelections.Select(Root, paths, Settings, _ => { });
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
