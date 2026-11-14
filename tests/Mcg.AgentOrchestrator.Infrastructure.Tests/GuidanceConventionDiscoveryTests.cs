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
