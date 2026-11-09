using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: all filesystem observations are confined to a unique temporary directory.
public sealed class MultiRepositoryScopeIntakeTests
{
    private const string HomePath = "docs/contracts/README.md";
    private static readonly string[] CrosswalkPaths =
    [
        "schema/v1/fixtures/invalid-duplicate-name.json",
        "record/record_test.go",
        "schema/v1/README.md"
    ];

    private const string ScopeText = """
        ## Scope
        In scope, orchestrator repository (key `default`):
        - `docs/contracts/README.md`

        In scope, crosswalk repository (key `crosswalk`):
        - `schema/v1/fixtures/invalid-duplicate-name.json`
        - `record/record_test.go`
        - `schema/v1/README.md`
        """;

    [Xunit.Fact]
    public void Build_KeyedScopeLabels_RecordsEachPathUnderItsRepositoryKey()
    {
        using var roots = new TemporaryRoots();
        roots.Write("default", HomePath);
        foreach (var path in CrosswalkPaths) roots.Write("crosswalk", path);

        var report = GoalScopeCollisionAdvisor.Build([ScopeText], [],
            repositoryRoot: roots.Default, repositories: roots.Declaration);

        var expected = new[] { ("default", HomePath) }
            .Concat(CrosswalkPaths.Select(path => ("crosswalk", path))).OrderBy(pair => pair.Item2);
        Xunit.Assert.Equal(expected, report.ProposedScopes.Select(scope => (scope.RepositoryKey, scope.Path))
            .OrderBy(pair => pair.Path));
        Xunit.Assert.All(report.ProposedScopes, scope =>
            Xunit.Assert.Equal(FileScopeProvenance.Explicit, scope.Provenance));
        Xunit.Assert.Empty(report.IntakeWarnings);
    }

    [Xunit.Fact]
    public void Build_KeyedScopeLabels_ResolvesEachPathOnlyAgainstItsOwnRoot()
    {
        using var roots = new TemporaryRoots();
        roots.Write("crosswalk", HomePath);
        foreach (var path in CrosswalkPaths) roots.Write("default", path);

        var report = GoalScopeCollisionAdvisor.Build([ScopeText], [],
            repositoryRoot: roots.Default, repositories: roots.Declaration);

        Xunit.Assert.Empty(report.ProposedScopes);
        Xunit.Assert.Contains(report.EvidenceGaps, gap => gap.Gap == ScopeEvidenceGap.ProposedScopesMissing);
    }

    [Xunit.Fact]
    public void Build_AbsentHomePathWithoutNew_ReproducesEmptyLegacyIntake()
    {
        using var roots = new TemporaryRoots();
        foreach (var path in CrosswalkPaths) roots.Write("crosswalk", path);
        Xunit.Assert.False(File.Exists(Path.Combine(roots.Default, HomePath)));
        Xunit.Assert.Equal(4, GoalFileScopeInference.FromText(ScopeText).Count);

        // The old intake call supplied only the home root, without the repository declaration.
        var report = GoalScopeCollisionAdvisor.Build([ScopeText], [], repositoryRoot: roots.Default);

        Xunit.Assert.Empty(report.ProposedScopes);
        Xunit.Assert.Contains(report.EvidenceGaps, gap => gap.Gap == ScopeEvidenceGap.ProposedScopesMissing);
    }

    [Xunit.Theory]
    [Xunit.InlineData("ghost")]
    [Xunit.InlineData("bad/key")]
    public void Build_UndeclaredKeyLabel_WarnsWithKeyAndRecordsNoPaths(string key)
    {
        using var roots = new TemporaryRoots();
        roots.Write("default", "docs/before.md");
        roots.Write("default", "docs/rejected.md");
        roots.Write("crosswalk", "docs/rejected.md");
        roots.Write("crosswalk", "record/record_test.go");
        var text = $"""
            ## Scope
            In scope, home (key default):
            - `docs/before.md`
            In scope, unknown (key `{key}`):
            - `docs/rejected.md`
            In scope, crosswalk (key crosswalk):
            - `record/record_test.go`
            """;

        var report = GoalScopeCollisionAdvisor.Build([text], [],
            repositoryRoot: roots.Default, repositories: roots.Declaration);

        Xunit.Assert.Contains(key, Xunit.Assert.Single(report.IntakeWarnings));
        Xunit.Assert.Equal(new[] { ("default", "docs/before.md"), ("crosswalk", "record/record_test.go") },
            report.ProposedScopes.Select(scope => (scope.RepositoryKey, scope.Path)));
    }

    [Xunit.Fact]
    public void Build_PlainLabelAfterKeyedLabel_PreservesBothRepositoryScopes()
    {
        using var roots = new TemporaryRoots();
        const string path = "schema/v1/README.md";
        roots.Write("default", path);
        roots.Write("crosswalk", path);
        var text = $"## Scope\nIn scope, other (key `CROSSWALK`):\n- `{path}`\nIn scope:\n- `{path}`";

        var report = GoalScopeCollisionAdvisor.Build([text], [],
            repositoryRoot: roots.Default, repositories: roots.Declaration);

        Xunit.Assert.Equal(new[] { "crosswalk", "default" },
            report.ProposedScopes.Select(scope => scope.RepositoryKey).Order());
    }

    [Xunit.Fact]
    public void Build_NoDeclaration_KeepsLegacyDefaultKeyWithoutWarning()
    {
        var report = GoalScopeCollisionAdvisor.Build(
            ["## Scope\nIn scope, unknown (key ghost):\n- `docs/contracts/README.md`"], []);

        Xunit.Assert.Equal("default", Xunit.Assert.Single(report.ProposedScopes).RepositoryKey);
        Xunit.Assert.Empty(report.IntakeWarnings);
    }

    [Xunit.Fact]
    public void Build_NewPathInAnotherBlock_DoesNotAdmitMissingPath()
    {
        using var roots = new TemporaryRoots();
        const string text = """
            ## Scope
            In scope, home (key default):
            - `docs/new.md`
            In scope, other (key crosswalk):
            - new `docs/new.md`
            """;

        var report = GoalScopeCollisionAdvisor.Build([text], [],
            repositoryRoot: roots.Default, repositories: roots.Declaration);

        var scope = Xunit.Assert.Single(report.ProposedScopes);
        Xunit.Assert.Equal("crosswalk", scope.RepositoryKey);
        Xunit.Assert.Equal("docs/new.md", scope.Path);
    }

    [Xunit.Fact]
    public void Deserialize_KeylessScope_ReadsAsDefault()
    {
        var scope = JsonSerializer.Deserialize<DeclaredFileScope>("""{"Path":"docs/file.md","Provenance":0}""");

        Xunit.Assert.NotNull(scope);
        Xunit.Assert.Equal("default", scope.RepositoryKey);
    }

    private sealed class TemporaryRoots : IDisposable
    {
        private readonly string root = Directory.CreateTempSubdirectory("scope-intake-").FullName;

        public TemporaryRoots()
        {
            Default = Directory.CreateDirectory(Path.Combine(root, "default")).FullName;
            var crosswalk = Directory.CreateDirectory(Path.Combine(root, "crosswalk")).FullName;
            Declaration = new GoalRepositoryDeclaration(["default", "crosswalk"],
                new Dictionary<string, string> { ["default"] = Default, ["crosswalk"] = crosswalk });
        }

        public string Default { get; }
        public GoalRepositoryDeclaration Declaration { get; }

        public void Write(string key, string path)
        {
            var absolute = Path.Combine(Declaration.CheckoutRoots[key], path);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllText(absolute, string.Empty);
        }

        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
