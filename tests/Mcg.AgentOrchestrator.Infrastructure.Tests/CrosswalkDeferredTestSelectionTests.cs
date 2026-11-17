using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its temporary manifest and uses no shared mutable state.
public sealed class CrosswalkDeferredTestSelectionTests : IDisposable
{
    private const string TestFile = "src/game/persistence/validation-facade.test.ts";
    private const string TestId = TestFile + "::validates saves";
    private const string CheckName = "crosswalk selection";
    private readonly string _root = CreateTempDirectory();

    [Theory]
    [InlineData(TestFile)]
    [InlineData(TestId)]
    public void Resolve_SingleCrosswalkCheck_PreservesLiteralName(string name)
    {
        WriteManifest(CrosswalkFocusedEvidenceTests.Manifest);
        Assert.False(Directory.Exists(Path.Combine(_root, "tests")));

        var result = DeveloperDeferredTestSelections.Resolve(_root, "tests: deferred - " + name);

        Assert.Equal(new FindingEvidenceSelection(CheckName, name), Assert.Single(result.Selections));
        Assert.Empty(result.NotRun);
    }

    [Fact]
    public void Resolve_MultipleDeferredNames_PreservesOrderAndLiteralValues()
    {
        WriteManifest(CrosswalkFocusedEvidenceTests.Manifest);

        var result = DeveloperDeferredTestSelections.Resolve(
            _root, $"tests: deferred - {TestFile},{TestId}");

        Assert.Equal(new[] { new FindingEvidenceSelection(CheckName, TestFile),
            new FindingEvidenceSelection(CheckName, TestId) }, result.Selections);
        Assert.Empty(result.NotRun);
    }

    [Theory]
    [InlineData(TestFile)]
    [InlineData(TestId)]
    public void Normalize_CrosswalkSelection_KeepsLiteralName(string name)
    {
        WriteManifest(CrosswalkFocusedEvidenceTests.Manifest);
        var declaration = DeveloperDeferredTestSelections.Resolve(_root, "tests: deferred - " + name);
        var selection = Assert.Single(declaration.Selections);
        Assert.True(CrosswalkFocusedEvidence.IsSupportedSelector(selection.TestClass));

        var normalized = DeveloperDeferredTestSelectionNormalization.Normalize(
            declaration, AcceptanceGateEngineSettings.Parse(CrosswalkFocusedEvidenceTests.Manifest));

        Assert.Equal(new FindingEvidenceSelection(CheckName, name), Assert.Single(normalized.Selections));
        Assert.Empty(normalized.NotRun);
    }

    [Fact]
    public void ForProject_TwoCrosswalkChecks_PreservesFallback()
    {
        WriteManifest("""
            {"checks":[
              {"name":"first","type":"crosswalk-test","project":"."},
              {"name":"second","type":"CROSSWALK-TEST","project":"."}
            ]}
            """);

        AssertFallback();
    }

    [Fact]
    public void ForProject_DotnetWithoutTestsDirectory_PreservesFallback()
    {
        WriteManifest("""
            {"checks":[{"name":"unit","type":"dotnet-test","project":"tests/App.Tests/App.Tests.csproj"}]}
            """);

        AssertFallback();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ForProject_CrosswalkBesideOtherTestCheck_PreservesFallback(bool dotnet)
    {
        var otherCheck = dotnet
            ? """{"name":"unit","type":"dotnet-test","project":"tests/App.Tests/App.Tests.csproj"}"""
            : """{"name":"go tests","type":"command","command":"go","arguments":["test","./..."]}""";
        var manifest = $$"""
            {"checks":[
              {"name":"crosswalk selection","type":"crosswalk-test","project":"."},
              {{otherCheck}}
            ]}
            """;
        WriteManifest(manifest);
        var settings = AcceptanceGateEngineSettings.Parse(manifest);
        Assert.NotNull(settings.DeclaredCrosswalkTestCheck);
        if (dotnet) Assert.Single(settings.DeclaredDotnetTestChecks);
        else Assert.NotNull(settings.DeclaredGoTestCheck);

        AssertFallback();
    }

    [Fact]
    public void ForProject_CrosswalkBesideMtpInvocation_PreservesFallback()
    {
        const string manifest = """
            {"checks":[{"name":"crosswalk selection","type":"crosswalk-test","project":"."}],
             "engine":{"mtpInvocations":[{"project":"tests/App.Tests/App.Tests.csproj",
               "executablePathTemplate":"bin/{projectName}/{configuration}/{projectName}{executableExtension}",
               "arguments":["{executable}"]}]}}
            """;
        WriteManifest(manifest);
        Assert.Single(AcceptanceGateEngineSettings.Parse(manifest).MtpInvocations);

        AssertFallback();
    }

    [Fact]
    public void ForProject_DeclaredJunitCheckBesideCrosswalk_KeepsDeclaredSelections()
    {
        WriteManifest("""
            {"checks":[
              {"name":"declared-test","type":"junit-command","project":"."},
              {"name":"crosswalk selection","type":"crosswalk-test","project":"client"}
            ]}
            """);

        var result = DeclaredTestEvidenceSelectors.ForProject(
            _root, $"tests: deferred - {TestFile},{TestId}", null);

        Assert.NotNull(result);
        Assert.Equal(new[] { new FindingEvidenceSelection(".", TestFile),
            new FindingEvidenceSelection(".", TestId) }, result.Selections);
        Assert.Empty(result.NotRun);
    }

    [Fact]
    public void ForProject_MultipleDeclaredChecksBesideCrosswalk_PreservesFallback()
    {
        WriteManifest("""
            {"checks":[
              {"name":"declared-test","type":"junit-command","project":"a"},
              {"name":"declared-test","type":"junit-command","project":"b"},
              {"name":"crosswalk selection","type":"crosswalk-test","project":"."}
            ]}
            """);

        AssertFallback();
    }

    private void AssertFallback()
    {
        const string declaration = "tests: deferred - ExampleTests";
        Assert.False(Directory.Exists(Path.Combine(_root, "tests")));
        Assert.Null(DeclaredTestEvidenceSelectors.ForProject(_root, declaration, null));
        var result = DeveloperDeferredTestSelections.Resolve(_root, declaration);
        Assert.Empty(result.Selections);
        Assert.Equal(new[] { "ExampleTests" }, result.NotRun);
    }

    private void WriteManifest(string manifest)
    {
        var config = Directory.CreateDirectory(Path.Combine(_root, "config")).FullName;
        File.WriteAllText(Path.Combine(config, "acceptance-manifest.json"), manifest);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
