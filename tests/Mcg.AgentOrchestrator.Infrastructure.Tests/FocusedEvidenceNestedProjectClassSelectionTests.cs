using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;

public sealed class FocusedEvidenceNestedProjectClassSelectionTests : RemoteFirstFocusedEvidenceFixture, IDisposable
{
    private const string TestRoot = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/";
    private const string FixtureRoot = TestRoot + "Fixtures/RealProcessShardProbe/";
    private const string FixtureProject = FixtureRoot + "Mcg.AgentOrchestrator.RealProcessShardProbe.csproj";
    private const string ParentFilter = "FullyQualifiedName~ParentOwnedTests";
    private const string FixtureFilter = "FullyQualifiedName~ShardProbeAlphaTests";
    private readonly string _root = CreateManifestWorkspace("""
        {"version":1,"checks":[],"forbiddenChangedPathGlobs":[]}
        """);
    private readonly GoalId _goal = new(Guid.NewGuid().ToString("N"));

    public FocusedEvidenceNestedProjectClassSelectionTests()
    {
        Write(Project, "<Project />");
        Write(TestRoot + "Deep/ParentOwnedTests.cs", TestSource("ParentOwnedTests"));
        Write(FixtureProject, "<Project />");
        Write(FixtureRoot + "Deep/ShardProbeTests.cs", TestSource("ShardProbeAlphaTests"));
    }

    [Theory]
    [InlineData("Infrastructure.Tests")]
    [InlineData(Project)]
    public void MixedSelectionLaunchesOnlyParentClass(string alias)
    {
        var checks = Plan($"{alias}: {ParentFilter}|{FixtureFilter}", new(), out var coverage);

        var check = Assert.Single(checks);
        Assert.Equal(Project, check.Project);
        Assert.Equal(ParentFilter, Assert.Single(check.FocusedEvidenceTokens).CanonicalToken);
        Assert.Contains(ParentFilter, check.Arguments);
        AssertDrop(coverage);
    }

    [Fact]
    public void FixtureOnlySelectionPlansNoLaunch()
    {
        var checks = Plan($"Infrastructure.Tests: {FixtureFilter}", new(), out var coverage);

        Assert.Empty(checks);
        Assert.Equal("no-runnable-selection", coverage.ExecutionMode);
        AssertDrop(coverage);
    }

    [Fact]
    public void ParentClassMatchedBySubstringKeepsSelection()
    {
        Write(TestRoot + "Deep/ParentShardProbeAlphaTests.cs", TestSource("ParentShardProbeAlphaTests"));

        var check = Assert.Single(Plan($"Infrastructure.Tests: {FixtureFilter}", new(), out var coverage));

        Assert.Equal(Project, check.Project);
        Assert.Equal(FixtureFilter, Assert.Single(check.FocusedEvidenceTokens).CanonicalToken);
        Assert.Null(coverage.DroppedSelections);
    }

    [Fact]
    public void ExtractedClassStillRoutesToItsRegisteredProject()
    {
        const string extractedRoot = TestRoot + "ProviderEnvironment/";
        const string extractedProject = extractedRoot + "Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests.csproj";
        const string extractedFilter = "FullyQualifiedName~ProviderOwnedTests";
        Write(extractedProject, "<Project />");
        Write(extractedRoot + "ProviderOwnedTests.cs", TestSource("ProviderOwnedTests"));
        var settings = new AcceptanceGateEngineSettings
        {
            MtpInvocations = [new AcceptanceMtpInvocation { Project = extractedProject }]
        };

        var check = Assert.Single(Plan($"Infrastructure.Tests: {extractedFilter}", settings, out var coverage));

        Assert.Equal(extractedProject, check.Project);
        Assert.Equal(extractedFilter, Assert.Single(check.FocusedEvidenceTokens).CanonicalToken);
        Assert.Null(coverage.DroppedSelections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZeroExecutionForParentClassStillFails(bool includeFixture)
    {
        var calls = new List<string[]>();
        var verifier = new GoalAcceptanceVerifier((args, _, _, _) =>
        {
            calls.Add(args);
            var report = Array.IndexOf(args, "--report-trx-filename");
            if (report >= 0)
            {
                var directory = args[Array.IndexOf(args, "--results-directory") + 1];
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, args[report + 1]), """
                    <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                      <TestDefinitions /><Results />
                      <ResultSummary outcome="Completed">
                        <Counters total="0" executed="0" passed="0" failed="0" notExecuted="0" />
                      </ResultSummary>
                    </TestRun>
                    """);
            }
            return Task.FromResult(new CommandResult(0, "fixture command completed"));
        });
        var filter = includeFixture ? $"{ParentFilter}|{FixtureFilter}" : ParentFilter;

        var result = await verifier.RunFocusedEvidenceAsync(_root, _goal, "Infrastructure.Tests: " + filter);

        Assert.True(result.Accepted);
        Assert.False(result.Passed);
        Assert.Equal(FindingEvidenceOutcomeReason.ApparatusFailure, result.OutcomeReason);
        var failure = Assert.Single(result.Checks, check =>
            check.FailureClassification == AcceptanceFailureClassifications.FocusedSelectionApparatusFailure);
        Assert.Contains("executed 0 tests", failure.OutputTail);
        var launch = Assert.Single(calls, args => args.Contains("--filter-class"));
        Assert.Equal("*ParentOwnedTests*", launch[Array.IndexOf(launch, "--filter-class") + 1]);
        Assert.DoesNotContain(launch, argument => argument.Contains("ShardProbeAlphaTests", StringComparison.Ordinal));
        if (includeFixture)
            AssertDrop(result.Coverage!);
        else
            Assert.Null(result.Coverage!.DroppedSelections);
    }

    private IReadOnlyList<AcceptanceManifestCheck> Plan(
        string request, AcceptanceGateEngineSettings settings, out FocusedEvidenceCoverage coverage)
    {
        Assert.True(TryBuildFocusedEvidenceChecks(request, settings, _root, out var checks, out coverage,
            out var rejection), rejection.Detail);
        return checks;
    }

    private static void AssertDrop(FocusedEvidenceCoverage coverage)
    {
        var drop = Assert.Single(coverage.DroppedSelections!);
        Assert.Equal(Project, drop.Project);
        Assert.Equal("ShardProbeAlphaTests", drop.Selection);
        Assert.Equal("owned-by-nested-project:" + FixtureProject, Assert.Single(drop.Reasons));
    }

    private static string TestSource(string name) => $"public class {name} {{ [Xunit.Fact] public void Runs() {{ }} }}";

    private void Write(string relativePath, string contents)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    public void Dispose()
    {
        DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(_goal);
        DeleteDirectoryWithRetry(_root);
    }
}
