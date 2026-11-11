using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: temporary files belong to each test; no git, builds, gates or processes run.
public sealed class ManifestTestCheckKindScopeTests
{
    private const string CrosswalkManifest = """
        {"checks":[{"name":"crosswalk-test .","type":"crosswalk-test","project":".","arguments":["id-one"]}]}
        """;
    private const string MixedManifest = """
        {"checks":[
          {"name":"unit","type":"dotnet-test","project":"tests/Unit.csproj"},
          {"name":"crosswalk-test .","type":"crosswalk-test","project":".","arguments":["id-one"]}]}
        """;

    [Fact]
    public void Scope_CrosswalkOnly_DerivesManifestIdentityAndOwnCommand()
    {
        using var fixture = new ManifestFixture(CrosswalkManifest);
        var actual = GoalTargetImpactPlanScope.Scope(Plan(), fixture.Root, fixture.Path);

        var check = Assert.Single(actual.Checks);
        Assert.Equal("crosswalk-test .", check.Name);
        Assert.Equal(new[] { "crosswalk-test", ".", "id-one" }, check.Command);
        Assert.DoesNotContain(actual.Checks, item => item.Command.Contains("dotnet"));
        Assert.Contains("crosswalk-test", actual.Summary);
        Assert.DoesNotContain("dotnet-test projects", actual.Summary);
        Assert.True(actual.RequiresBuild);
    }

    [Fact]
    public void Scope_MixedManifest_DerivesBothKindsInManifestOrder()
    {
        using var fixture = new ManifestFixture(MixedManifest);
        var actual = GoalTargetImpactPlanScope.Scope(Plan(), fixture.Root, fixture.Path);

        Assert.Collection(actual.Checks,
            check =>
            {
                Assert.Equal("unit", check.Name);
                Assert.Equal(new[] { "dotnet", "test", "--project", "tests/Unit.csproj" }, check.Command);
            },
            check =>
            {
                Assert.Equal("crosswalk-test .", check.Name);
                Assert.Equal(new[] { "crosswalk-test", ".", "id-one" }, check.Command);
            });
        Assert.Contains("dotnet-test, crosswalk-test", actual.Summary);
    }

    [Fact]
    public void Filters_MixedManifest_PreserveSupportedChecksAndInstances()
    {
        using var fixture = new ManifestFixture(MixedManifest);
        var dotnet = new RepositoryTestImpactCheck("selected unit",
            ["dotnet", "test", "--project", "tests/Unit.csproj", "--filter", "Example"], "focused");
        var crosswalk = new RepositoryTestImpactCheck("selected crosswalk",
            ["crosswalk-test", ".", "id-one"], "focused");
        var plan = Plan() with
        {
            Checks = [crosswalk, new("excluded", ["crosswalk-test", "other"], "other project"),
                dotnet, new("command", ["command", "run", "tests/Unit.csproj"], "not a test")]
        };
        var declarations = JsonSerializer.Deserialize<AcceptanceManifestCheck[]>(
            """
            [{"Name":"unit","Type":"dotnet-test","Project":"tests/Unit.csproj"},
             {"Name":"crosswalk-test .","Type":"crosswalk-test","Project":"."}]
            """)!;

        var results = new[]
        {
            GoalTargetImpactPlanScope.Scope(plan, fixture.Root, fixture.Path),
            WorkerTestImpactPlanSelection.ScopeToManifest(plan, fixture.Root, fixture.Path),
            ManifestScopedImpactPlan.Scope(plan, declarations)
        };
        foreach (var actual in results)
        {
            Assert.Collection(actual.Checks,
                check => Assert.Same(crosswalk, check), check => Assert.Same(dotnet, check));
            Assert.Equal(plan.RequiresBuild, actual.RequiresBuild);
            Assert.Equal(plan.RequiresBroadVerification, actual.RequiresBroadVerification);
        }
        Assert.Contains("dotnet-test, crosswalk-test", results[1].Summary);
    }

    [Theory]
    [InlineData("command")]
    [InlineData("junit-command")]
    [InlineData("future-test")]
    public void Scope_NonTestKind_DerivesNothingAndReportsKind(string kind)
    {
        using var fixture = new ManifestFixture($$"""
            {"checks":[{"name":"other","type":"{{kind}}","project":"."}]}
            """);
        var plan = Plan() with { Checks = [new("existing", ["crosswalk-test", "."], "existing")] };
        foreach (var actual in new[]
        {
            GoalTargetImpactPlanScope.Scope(plan, fixture.Root, fixture.Path),
            WorkerTestImpactPlanSelection.ScopeToManifest(plan, fixture.Root, fixture.Path)
        })
        {
            Assert.Empty(actual.Checks);
            Assert.Equal(plan.RequiresBuild, actual.RequiresBuild);
            Assert.Contains("declares no test checks", actual.Summary);
            Assert.Contains("no project checks derived", actual.Summary);
            Assert.Contains($"declared kinds not derived: {kind}", actual.Summary);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\".\"")]
    [InlineData("\"sub/project\"")]
    public void Reader_CrosswalkProject_PreservesProjectAndArguments(string projectJson)
    {
        using var fixture = new ManifestFixture($$"""
            {"checks":[{"name":"declared","type":"CROSSWALK-TEST","project":{{projectJson}},"arguments":["one","two"]}]}
            """);
        var set = Assert.IsType<ManifestDeclaredTestCheckSet>(WorkerManifestTestCheckReader.Read(fixture.Path));
        var check = Assert.Single(set.Checks);
        Assert.Equal("declared", check.Name);
        Assert.Equal("crosswalk-test", check.Kind);
        Assert.Equal(JsonSerializer.Deserialize<string?>(projectJson), check.Project);
        Assert.Equal(new[] { "one", "two" }, check.Arguments);
        Assert.Empty(WorkerManifestTestProjectReader.ReadChecks(fixture.Path)!);
    }

    [Fact]
    public void Reader_AbsentProjectAndArguments_DerivesRootWithDefaultName()
    {
        using var fixture = new ManifestFixture("""{"checks":[{"type":"crosswalk-test"}]}""");
        var plan = GoalTargetImpactPlanScope.Scope(Plan(), fixture.Root, fixture.Path);
        var check = Assert.Single(plan.Checks);
        Assert.Equal("crosswalk-test .", check.Name);
        Assert.Equal(new[] { "crosswalk-test", "." }, check.Command);
    }

    [Fact]
    public void Scope_DotnetArguments_PreservesDeclaredArguments()
    {
        using var fixture = new ManifestFixture("""
            {"checks":[{"name":"unit","type":"dotnet-test","project":"unit.csproj","arguments":["--filter","Example"]}]}
            """);
        var check = Assert.Single(GoalTargetImpactPlanScope.Scope(Plan(), fixture.Root, fixture.Path).Checks);
        Assert.Equal(new[] { "dotnet", "test", "--project", "unit.csproj", "--filter", "Example" }, check.Command);
    }

    [Fact]
    public void ScopeToManifest_CrosswalkWithoutSelectedCheck_DoesNotInventFallback()
    {
        using var fixture = new ManifestFixture(CrosswalkManifest);
        var actual = WorkerTestImpactPlanSelection.ScopeToManifest(Plan(), fixture.Root, fixture.Path);
        Assert.Empty(actual.Checks);
        Assert.Contains("checks scoped to crosswalk-test checks", actual.Summary);
        Assert.DoesNotContain("dotnet-test projects", actual.Summary);
    }

    [Fact]
    public void Filters_NoBuild_PreserveNonExecutableEvidence()
    {
        using var fixture = new ManifestFixture(CrosswalkManifest);
        var evidence = new RepositoryTestImpactCheck("evidence", [], "no build required");
        var plan = Plan() with { RequiresBuild = false, Checks = [evidence] };
        var worker = WorkerTestImpactPlanSelection.ScopeToManifest(plan, fixture.Root, fixture.Path);
        var gate = ManifestScopedImpactPlan.Scope(plan,
            [new() { Name = "crosswalk", Type = "crosswalk-test", Project = "." }]);
        Assert.Same(evidence, Assert.Single(worker.Checks));
        Assert.Same(plan, gate);
        Assert.False(worker.RequiresBuild);
    }

    [Theory]
    [InlineData("7", "[]")]
    [InlineData("\".\"", "7")]
    [InlineData("\".\"", "[null]")]
    public void Reader_MalformedCheck_SkipsIt(string project, string arguments)
    {
        using var fixture = new ManifestFixture($$"""
            {"checks":[{"type":"crosswalk-test","project":{{project}},"arguments":{{arguments}}}]}
            """);
        Assert.Empty(Assert.IsType<ManifestDeclaredTestCheckSet>(
            WorkerManifestTestCheckReader.Read(fixture.Path)).Checks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reader_UnavailableManifest_ReturnsNull(bool malformed)
    {
        using var fixture = new ManifestFixture("{");
        if (!malformed)
            File.Delete(fixture.Path);
        Assert.Null(WorkerManifestTestCheckReader.Read(fixture.Path));
    }

    [Fact]
    public void HomeTarget_CrosswalkManifest_PreservesPlannerAndSkipsResolver()
    {
        using var fixture = new ManifestFixture(CrosswalkManifest);
        File.WriteAllText(System.IO.Path.Combine(fixture.Root, ".git"), "gitdir: isolated-fixture");
        File.WriteAllText(System.IO.Path.Combine(fixture.Root, "go.mod"), "module example.com/widget\n");
        string[] files = ["widget.go"];
        var summary = RepositoryChangeClassifier.Classify(files);
        var expected = RepositoryTestImpactPlanner.Plan(summary, fixture.Root);
        var home = new WorkerTargetHome(true, _ => throw new InvalidOperationException("must not resolve"));

        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(
            GoalTargetImpactPlanScope.Plan(summary, fixture.Root, home)));
        Assert.Equal(JsonSerializer.Serialize(RepositoryTestImpactPlanner.Plan(files, fixture.Root)),
            JsonSerializer.Serialize(WorkerTestImpactPlanSelection.For(files, fixture.Root, home)));
    }

    private static RepositoryTestImpactPlan Plan() => new(true, false, "synthetic impact", []);

    private sealed class ManifestFixture : IDisposable
    {
        public string Root { get; } = SharedTestSupport.CreateTempDirectory();
        public string Path { get; }

        public ManifestFixture(string json)
        {
            Path = System.IO.Path.Combine(Root, "acceptance-manifest.json");
            File.WriteAllText(Path, json);
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
