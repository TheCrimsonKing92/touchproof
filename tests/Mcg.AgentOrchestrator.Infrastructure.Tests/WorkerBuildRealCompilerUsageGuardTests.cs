using System.Reflection;
using System.Text.RegularExpressions;

// Parallel-safe: repository source reads and in-memory synthetic sources only.
public sealed class WorkerBuildRealCompilerUsageGuardTests
{
    private const string ProjectPath = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/";
    private static readonly string RealUsage = nameof(WorkerBuildRequestFixture.CompilerMode) + ".Real";
    private static readonly IReadOnlyDictionary<string, string> Retained = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [ProjectPath + "WorkerBuildRequestIntegrationTests.cs"] = "Real compilation verifies low-integrity sandboxing and invalid-source compiler failure.",
        [ProjectPath + "BuildRequestLauncherContractTests.cs"] = "Real compilation supplies the outcome, exit code and phase baseline for fake-launch parity.",
        [ProjectPath + "WorkerBuildRequestRecoveryTests.cs"] = "An MSBuild gate before CoreCompile verifies managed-host death interrupts the real compiler and preserves its recovery obligation.",
        [ProjectPath + "WorkerBuildRequestStructuralIntegrationTests.cs"] = "Real compilation feeds structural violation replay and a fresh successful correction."
    };

    [Fact]
    public void FixtureDefaultsToFakeCompiler()
    {
        var constructor = Assert.Single(typeof(WorkerBuildRequestFixture)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic));
        var mode = Assert.Single(constructor.GetParameters(), parameter => parameter.Name == "compilerMode");
        Assert.Equal(WorkerBuildRequestFixture.CompilerMode.Fake, mode.DefaultValue);
    }

    [Fact]
    public void RepositorySources_UseRealCompilerOnlyInRetainedTests()
    {
        var root = VerifiedRepositoryRoot.Find();
        var project = Path.Combine(root, ProjectPath);
        var snapshot = RepositorySourceSnapshotCache.Get(root);
        var sources = snapshot.EnumerateFiles(project)
            .Where(path => !SourceGuardFileFilter.WorkspaceAndReports.Excludes(Path.GetRelativePath(project, path)))
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) &&
                !path.EndsWith(".g.cs", StringComparison.Ordinal) && !path.EndsWith(".generated.cs", StringComparison.Ordinal))
            .Select(path => (RelativePath: NormalizePath(Path.GetRelativePath(root, path)), Text: snapshot.GetText(path)))
            // Only test cases choose a compiler mode; fixture/helper implementations are outside this policy.
            .Where(source => Regex.IsMatch(source.Text, @"\[\s*(?:\w+\.)*\w*(?:Fact|Theory)(?:Attribute)?\b"))
            .ToArray();
        Assert.NotEmpty(sources);
        Assert.Contains(sources, source => source.RelativePath == ProjectPath + "WorkerBuildChannelQosOptOutTests.cs");
        Assert.Contains(sources, source => source.RelativePath == ProjectPath + "WorkerBuildRealCompilerUsageGuardTests.cs");
        foreach (var retained in Retained)
        {
            Assert.False(string.IsNullOrWhiteSpace(retained.Value));
            var source = Assert.Single(sources, source => source.RelativePath == retained.Key);
            Assert.Contains(RealUsage, source.Text, StringComparison.Ordinal);
        }
        Assert.Empty(FindUnlistedRealUsages(sources));
    }

    [Fact]
    public void Detector_ReportsUnlistedRealUsageAndAllowsRetainedAndFakeSources()
    {
        const string unauthorized = ProjectPath + "Nested/UnauthorizedTests.cs";
        var realSource = "class Test { object Mode = WorkerBuildRequestFixture." + RealUsage + "; }";
        var offenders = FindUnlistedRealUsages(
        [
            (unauthorized.Replace('/', '\\'), realSource),
            (Retained.Keys.First(), realSource),
            (ProjectPath + "FakeTests.cs", "class Test { object Mode = WorkerBuildRequestFixture.CompilerMode.Fake; }")
        ]);
        Assert.Equal(new[] { unauthorized }, offenders);
    }

    private static string[] FindUnlistedRealUsages(IEnumerable<(string RelativePath, string Text)> sources) => sources
        .Where(source => source.Text.Contains(RealUsage, StringComparison.Ordinal) &&
            !Retained.ContainsKey(NormalizePath(source.RelativePath)))
        .Select(source => NormalizePath(source.RelativePath))
        .ToArray();

    private static string NormalizePath(string path) => path.Replace('\\', '/');
}
