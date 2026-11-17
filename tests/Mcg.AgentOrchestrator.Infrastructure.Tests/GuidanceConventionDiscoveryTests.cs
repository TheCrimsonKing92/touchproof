using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each test owns its files and optional staged repository; no shared git configuration is changed.
public sealed class GuidanceConventionDiscoveryTests
{
    [Fact]
    public void Discover_MatchingHeading_CapturesOnlyVerbatimItemsWithProvenance()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        File.WriteAllText(Path.Combine(fixture.Root, "AGENTS.md"), """
            # Repository conventions
            - Keep **literal** rule text.
            1. Use `dotnet build`.
               Preserve the continuation.

            ## Background
            - This is unrelated.
            # Testing style
            + Keep punctuation: A & B!
            ```markdown
            # Build
            - Do not quote this example.
            ```
            """);
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);

        Assert.Equal(3, model.Conventions.Count);
        AssertConvention(model.Conventions[0], "Repository conventions", "Keep **literal** rule text.", 2);
        AssertConvention(model.Conventions[1], "Repository conventions", "Use `dotnet build`.\nPreserve the continuation.", 3);
        AssertConvention(model.Conventions[2], "Testing style", "Keep punctuation: A & B!", 9);
        Assert.DoesNotContain(model.OwnerQuestions, question => question.FactKey.StartsWith("conventions/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("# Background\n- Not a convention.\n")]
    public void Discover_AbsentGuidanceOrNoMatchingHeading_RecordsNothing(string? guidance)
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        if (guidance is not null)
            File.WriteAllText(Path.Combine(fixture.Root, "AGENTS.md"), guidance);
        Assert.Empty(new DotnetProjectDiscoveryAdapter().Discover(fixture.Root).Conventions);
    }

    [Fact]
    public void Discover_TrackedRootGuidanceOnly_SkipsUntrackedAndNestedGuidance()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        try
        {
            File.WriteAllText(Path.Combine(fixture.Root, "AGENTS.md"), "# Rules\n- Root rule.\n");
            File.WriteAllText(Path.Combine(fixture.Root, "README.md"), "# Build\n* README rule.\n");
            File.WriteAllText(Path.Combine(fixture.Root, "src/CLAUDE.md"), "# Rules\n- Nested rule.\n");
            Git(fixture.Root, "init");
            Git(fixture.Root, "add", "--all");
            File.WriteAllText(Path.Combine(fixture.Root, "CLAUDE.md"), "# Rules\n- Untracked rule.\n");

            var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
            Assert.Collection(model.Conventions,
                convention => Assert.Equal(("AGENTS.md", "Root rule."), (convention.DocumentPath, convention.Rule)),
                convention => Assert.Equal(("README.md", "README rule."), (convention.DocumentPath, convention.Rule)));
            Assert.DoesNotContain(model.OwnerQuestions, question => question.FactKey == "discovery/tracked-files");
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(Path.Combine(fixture.Root, ".git"));
        }
    }

    [Fact]
    public void Discover_AllRootGuidanceFiles_AreReadInDeclaredOrder()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        foreach (var path in new[] { "AGENTS.md", "CLAUDE.md", "README.md" })
            File.WriteAllText(Path.Combine(fixture.Root, path), $"# Build rules\n- Rule in {path}.\n");
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        Assert.Equal(new[] { "AGENTS.md", "CLAUDE.md", "README.md" }, model.Conventions.Select(convention => convention.DocumentPath));
        Assert.All(model.Conventions, convention => Assert.Equal($"Rule in {convention.DocumentPath}.", convention.Rule));
    }

    [Fact]
    public void Discover_SharedRule_MergesSourcesAndKeepsUniqueRule()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        File.WriteAllText(Path.Combine(fixture.Root, "AGENTS.md"),
            "# Build rules\n- Shared rule.\n- Agents only rule.\n");
        File.WriteAllText(Path.Combine(fixture.Root, "CLAUDE.md"),
            "# Build rules\n-   shared   RULE.  \n");

        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);

        Assert.Equal(2, model.Conventions.Count);
        AssertConvention(model.Conventions[0], "Build rules", "Shared rule.", 2);
        Assert.Equal(new[] { new FactSource("AGENTS.md", 2), new FactSource("CLAUDE.md", 2) },
            model.Conventions[0].Sources);
        AssertConvention(model.Conventions[1], "Build rules", "Agents only rule.", 3);
        Assert.Equal(new FactSource("AGENTS.md", 3), Assert.Single(model.Conventions[1].Sources));
    }

    [Fact]
    public void Discover_RepeatedRule_MergesAcrossHeadingsAndWithinOneFile()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        File.WriteAllText(Path.Combine(fixture.Root, "AGENTS.md"),
            "# Build rules\n- Keep evidence.\n- KEEP  evidence.\n# Testing style\n- Keep evidence.\n");

        var convention = Assert.Single(new DotnetProjectDiscoveryAdapter().Discover(fixture.Root).Conventions);

        AssertConvention(convention, "Build rules", "Keep evidence.", 2);
        Assert.Equal(new[] { new FactSource("AGENTS.md", 2), new FactSource("AGENTS.md", 3),
            new FactSource("AGENTS.md", 5) }, convention.Sources);
    }

    [Fact]
    public void Discover_Exemptions_PrependsWrappedLeadInToEveryItem()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        File.WriteAllText(Path.Combine(fixture.Root, "AGENTS.md"), """
            # Mutation testing contract
            Mutation testing is required. The following cases
            are exempt from mutation testing:

            - Documentation-only changes.

            - Generated files.
              Preserve the generator receipt.
            """);

        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);

        Assert.Equal(2, model.Conventions.Count);
        const string leadIn = "Mutation testing is required. The following cases are exempt from mutation testing:\n";
        AssertConvention(model.Conventions[0], "Mutation testing contract",
            leadIn + "Documentation-only changes.", 5);
        AssertConvention(model.Conventions[1], "Mutation testing contract",
            leadIn + "Generated files.\nPreserve the generator receipt.", 7);
        Assert.All(model.Conventions, convention =>
            Assert.Equal(convention.Evidence.Source, Assert.Single(convention.Sources)));
    }

    [Theory]
    [InlineData("# Testing rules\n", "Testing rules", 5)]
    [InlineData("```markdown\nHidden introduction.\n```\n", "Build rules", 7)]
    [InlineData("~~~markdown\nHidden introduction.\n~~~\n", "Build rules", 7)]
    public void Discover_HeadingOrFence_DiscardsEarlierLeadIn(string boundary, string heading, int line)
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        File.WriteAllText(Path.Combine(fixture.Root, "AGENTS.md"),
            "# Build rules\nEarlier introduction.\n\n" + boundary + "- Plain rule.\n");

        var convention = Assert.Single(new DotnetProjectDiscoveryAdapter().Discover(fixture.Root).Conventions);

        AssertConvention(convention, heading, "Plain rule.", line);
    }

    [Fact]
    public void Discover_NewParagraph_ReplacesLeadInAndPreservesDistinctMeanings()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        File.WriteAllText(Path.Combine(fixture.Root, "AGENTS.md"), """
            # Testing rules
            Discard this older paragraph.

            Required cases:
            - Shared item.

            Exempt cases:
            - Shared item.
            # Build rules
            - Plain rule.
            """);

        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);

        Assert.Equal(3, model.Conventions.Count);
        AssertConvention(model.Conventions[0], "Testing rules", "Required cases:\nShared item.", 5);
        AssertConvention(model.Conventions[1], "Testing rules", "Exempt cases:\nShared item.", 8);
        AssertConvention(model.Conventions[2], "Build rules", "Plain rule.", 10);
    }

    [Fact]
    public void Json_MultipleSources_RoundTripsWithValueEquality()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        foreach (var name in new[] { "AGENTS.md", "CLAUDE.md" })
            File.WriteAllText(Path.Combine(fixture.Root, name), "# Rules\n- Keep evidence.\n");
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        var original = Assert.Single(model.Conventions);
        Assert.Equal(2, original.Sources.Count);

        var json = ProjectModelJson.Serialize(model);
        var restored = ProjectModelJson.Deserialize(json);
        var convention = Assert.Single(restored.Conventions);

        Assert.Equal(original, convention);
        Assert.Equal(original.GetHashCode(), convention.GetHashCode());
        Assert.Equal(original.Sources, convention.Sources);
        Assert.Equal(json, ProjectModelJson.Serialize(restored));
        Assert.Equal(5, restored.SchemaVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Json_MissingOrNullSources_DerivesPrimarySource(bool explicitNull)
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        File.WriteAllText(Path.Combine(fixture.Root, "AGENTS.md"), "# Rules\n- Keep evidence.\n");
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        var document = JsonNode.Parse(ProjectModelJson.Serialize(model))!.AsObject();
        var storedConvention = document["conventions"]![0]!.AsObject();
        if (explicitNull)
            storedConvention["sources"] = null;
        else
            storedConvention.Remove("sources");

        var restored = Assert.Single(ProjectModelJson.Deserialize(document.ToJsonString()).Conventions);

        AssertConvention(restored, "Rules", "Keep evidence.", 2);
        Assert.Equal(restored.Evidence.Source, Assert.Single(restored.Sources));
        Assert.Equal(Assert.Single(model.Conventions), restored);
    }

    private static void AssertConvention(ProjectConvention convention, string heading, string rule, int line)
    {
        Assert.Equal("AGENTS.md", convention.DocumentPath);
        Assert.Equal(heading, convention.Heading);
        Assert.Equal(rule, convention.Rule);
        Assert.Equal(rule, convention.Evidence.Value);
        Assert.Equal(new FactSource("AGENTS.md", line), convention.Evidence.Source);
        Assert.Equal(FactConfidence.Medium, convention.Evidence.Confidence);
    }

    private static void Git(string root, params string[] arguments)
    {
        var result = GitCli.Run(root, arguments);
        Assert.True(result.ProcessStarted && result.Succeeded && !result.DrainTimedOut, result.Error);
    }
}
