using Mcg.AgentOrchestrator.Infrastructure;

// Every case owns its temporary git repository; no shared index, cwd or git observers.
public sealed class RemovalReferenceCensusTests : HostCapacityBoundTestBase
{
    [Fact]
    public void Run_TrackedReferences_ClassifiesEveryHitWithLocationAndRule()
    {
        var root = CreateFixture();
        var result = RemovalReferenceCensus.Run(["Fixture.Removed"], root);

        Assert.Equal(new[] { "Fixture.Removed" }, result.Names);
        Assert.Equal(new[]
        {
            new RemovalReferenceHit("Fixture.Removed", "README.md", 1, "6 projects including Fixture.Removed", RemovalReferenceClassification.MustChange, "tracked-reference"),
            new RemovalReferenceHit("Fixture.Removed", "config/projects.json", 1, "[\"Fixture.Removed\"]", RemovalReferenceClassification.MustChange, "tracked-reference"),
            new RemovalReferenceHit("Fixture.Removed", "docs/architecture.md", 1, "# Fixture.Removed architecture", RemovalReferenceClassification.MustChange, "tracked-reference"),
            new RemovalReferenceHit("Fixture.Removed", "docs/incidents/2026-11-07-removal.md", 1, "Fixture.Removed was retired", RemovalReferenceClassification.Leave, "historical-doc"),
            new RemovalReferenceHit("Fixture.Removed", "src/App/App.csproj", 1, "<ProjectReference Include=\"Fixture.Removed.csproj\" />", RemovalReferenceClassification.MustChange, "tracked-reference"),
            new RemovalReferenceHit("Fixture.Removed", "src/App/AssemblyList.cs", 2, "var names = new[] { \"Fixture.Removed\" };", RemovalReferenceClassification.MustChange, "tracked-reference"),
            new RemovalReferenceHit("Fixture.Removed", "src/App/Notes.cs", 1, "  // Fixture.Removed", RemovalReferenceClassification.Leave, "comment"),
            new RemovalReferenceHit("Fixture.Removed", "tests/RuntimePaths.cs", 1, "const string Name = \"Fixture.Removed\";", RemovalReferenceClassification.MustChange, "tracked-reference")
        }, result.Hits);
    }

    [Fact]
    public void Render_MultipleNamesAndLines_GroupsOnlyMustChangeAndReportsHitlessName()
    {
        var root = CreateFixture();
        File.AppendAllText(Path.Combine(root, "src/App/AssemblyList.cs"), "var other = \"Other.Removed\";\n");
        var result = RemovalReferenceCensus.Run([" Fixture.Removed ", "Other.Removed", "Absent", "Fixture.Removed"], root);

        var rendered = RemovalReferenceCensus.RenderBriefList(result).ReplaceLineEndings("\n");

        Assert.Equal("""
            Removed names: Fixture.Removed, Other.Removed, Absent
            - `README.md`
              - line 1 (Fixture.Removed): 6 projects including Fixture.Removed
            - `config/projects.json`
              - line 1 (Fixture.Removed): ["Fixture.Removed"]
            - `docs/architecture.md`
              - line 1 (Fixture.Removed): # Fixture.Removed architecture
            - `src/App/App.csproj`
              - line 1 (Fixture.Removed): <ProjectReference Include="Fixture.Removed.csproj" />
            - `src/App/AssemblyList.cs`
              - line 2 (Fixture.Removed): var names = new[] { "Fixture.Removed" };
              - line 3 (Other.Removed): var other = "Other.Removed";
            - `tests/RuntimePaths.cs`
              - line 1 (Fixture.Removed): const string Name = "Fixture.Removed";
            - Absent: no references in src, tests, config, README.md or docs.
            Leave hits: 2
            """, rendered);
    }

    [Theory]
    [InlineData("docs/triage/current.md", "Fixture.Removed", "triage-doc")]
    [InlineData("docs/negative-controls/current.md", "Fixture.Removed", "negative-control-doc")]
    [InlineData("tests/Fixtures/sample.cs", "Fixture.Removed", "fixture")]
    [InlineData("docs/archive/report-2026-11.md", "Fixture.Removed", "historical-doc")]
    [InlineData("src/setup.ps1", "# Fixture.Removed", "comment")]
    [InlineData("src/notes.cs", "/* Fixture.Removed */", "comment")]
    [InlineData("src/notes.ts", " * Fixture.Removed", "comment")]
    [InlineData("src/App.csproj", "<!-- Fixture.Removed -->", "comment")]
    public void Run_LeaveRule_ReturnsClassifiedHit(string path, string text, string rule)
    {
        var root = CreateRepository(new Dictionary<string, string> { [path] = text });

        var hit = Assert.Single(RemovalReferenceCensus.Run(["Fixture.Removed"], root).Hits);

        Assert.Equal(RemovalReferenceClassification.Leave, hit.Classification);
        Assert.Equal(rule, hit.Rule);
        Assert.EndsWith("Leave hits: 1", RemovalReferenceCensus.RenderBriefList(new([hit.Name], [hit])));
    }

    [Fact]
    public void Run_LiteralOptionLikeNameAndUntrackedFile_OnlyFindsTrackedLiteral()
    {
        var root = CreateRepository(new Dictionary<string, string>
        {
            ["docs/space in path.md"] = "-Fixture.Removed\r\n-FixtureXRemoved\r\n",
            ["other.md"] = "-Fixture.Removed"
        });
        File.WriteAllText(Path.Combine(root, "docs/untracked.md"), "-Fixture.Removed");
        var hit = Assert.Single(RemovalReferenceCensus.Run(["-Fixture.Removed"], root).Hits);
        Assert.Equal("docs/space in path.md", hit.Path);
        Assert.Equal(1, hit.Line);
        Assert.Equal("-Fixture.Removed", hit.Text);
    }

    [Fact]
    public void Run_MissingScanRoots_ReportsNoReferences()
    {
        var root = CreateRepository(new Dictionary<string, string> { ["other.md"] = "Fixture.Removed" });
        var result = RemovalReferenceCensus.Run(["Fixture.Removed"], root);
        Assert.Empty(result.Hits);
        Assert.Contains("- Fixture.Removed: no references in src, tests, config, README.md or docs.",
            RemovalReferenceCensus.RenderBriefList(result));
    }

    [Fact]
    public void Run_InvalidRootOrName_FailsLoudly()
    {
        Assert.Throws<InvalidOperationException>(() => RemovalReferenceCensus.Run(["A"], CreateTempDirectory()));
        var root = CreateFixture();
        var error = Assert.Throws<InvalidOperationException>(() => RemovalReferenceCensus.Run(["A"], Path.Combine(root, "src")));
        Assert.Contains("not a repository root", error.Message);
        Assert.Throws<ArgumentException>(() => RemovalReferenceCensus.Run([" "], root));
        Assert.Throws<ArgumentException>(() => RemovalReferenceCensus.Run(["A\nB"], root));
    }

    [Theory]
    [InlineData("## What to build\nRemoved names: `A`, B, A", true)]
    [InlineData("## What to build\n- Removed names: A", true)]
    [InlineData("## Other\nRemoved names: A", false)]
    [InlineData("## What to build\nExample Removed names: A", false)]
    [InlineData("## What to build\n```\nRemoved names: A\n```", false)]
    [InlineData("## What to build\n## Acceptance criteria\nRemoved names: A", false)]
    [InlineData("## What to build\nRemoved names: , ` `", false)]
    public void Parse_DeclarationLocation_OnlyAcceptsBuildSection(string brief, bool expected)
    {
        Assert.Equal(expected, RemovalReferenceCensus.TryParseRemovedNames(brief, out var names));
        if (expected) Assert.Equal(brief.Contains('B') ? new[] { "A", "B" } : new[] { "A" }, names);
        else Assert.Empty(names);
    }

    private static string CreateFixture() => CreateRepository(new Dictionary<string, string>
    {
        ["src/App/AssemblyList.cs"] = "using System;\nvar names = new[] { \"Fixture.Removed\" };\n",
        ["src/App/App.csproj"] = "<ProjectReference Include=\"Fixture.Removed.csproj\" />",
        ["config/projects.json"] = "[\"Fixture.Removed\"]",
        ["README.md"] = "6 projects including Fixture.Removed",
        ["docs/architecture.md"] = "# Fixture.Removed architecture",
        ["src/App/Notes.cs"] = "  // Fixture.Removed",
        ["docs/incidents/2026-11-07-removal.md"] = "Fixture.Removed was retired",
        ["tests/RuntimePaths.cs"] = "const string Name = \"Fixture.Removed\";"
    });

    private static string CreateRepository(IReadOnlyDictionary<string, string> files)
    {
        var root = CreateTempDirectory();
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        CheckGit(root, "init", "-q");
        CheckGit(root, "add", ".");
        CheckGit(root, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
            "-c", "commit.gpgsign=false", "-c", "core.hooksPath=" + Path.Combine(root, "no-hooks"), "commit", "-qm", "Fixture");
        return root;
    }

    private static void CheckGit(string root, params string[] args)
    {
        var result = GitCli.RunForRepository(root, args);
        Assert.True(result.ProcessStarted && !result.DrainTimedOut && result.ExitCode == 0, result.Error);
    }
}
