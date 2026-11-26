using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

public sealed class NestedFixtureProjectSelectionTests : IDisposable
{
    private const string TestRoot = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/";
    private const string ParentProject = TestRoot + "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
    private const string ParentSource = TestRoot + "Deep/ParentOwnedTests.cs";
    private const string FixtureRoot = TestRoot + "Fixtures/RealProcessShardProbe/";
    private const string FixtureSource = FixtureRoot + "ShardProbeAlphaTests.cs";
    private const string CoreRoot = "src/Mcg.AgentOrchestrator.Core/";
    private const string CoreSource = CoreRoot + "ChangedContract.cs";
    private const string ParentFilter = "FullyQualifiedName~ParentOwnedTests";
    private readonly string _root = SharedTestSupport.CreateTempDirectory();

    public NestedFixtureProjectSelectionTests()
    {
        Write(ParentProject, """
            <Project><ItemGroup>
              <ProjectReference Include="../../src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj" />
              <ProjectReference Include="Fixtures/RealProcessShardProbe/Mcg.AgentOrchestrator.RealProcessShardProbe.csproj"
                                ReferenceOutputAssembly="false" />
            </ItemGroup></Project>
            """);
        Write(FixtureRoot + "Mcg.AgentOrchestrator.RealProcessShardProbe.csproj", """
            <Project><ItemGroup>
              <ProjectReference Include="../../../../src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj" />
            </ItemGroup></Project>
            """);
        Write(CoreRoot + "Mcg.AgentOrchestrator.Core.csproj", "<Project />");
        Write(CoreSource, "public class ChangedContract { }");
        Write(ParentSource, TestSource("ParentOwnedTests"));
        Write(FixtureSource, TestSource("ShardProbeAlphaTests"));
    }

    [Fact]
    public void ChangedFixtureSourceKeepsOnlyParentOwnedClass()
    {
        AssertParentSelection([FixtureSource, ParentSource]);
    }

    [Fact]
    public void ChangedCoreSourceKeepsOnlyParentOwnedDependentClass()
    {
        AssertParentSelection([CoreSource]);
    }

    [Fact]
    public void ProjectWideCheckReportsOnlyParentOwnedChangedClass()
    {
        const string infrastructureSource = "src/Mcg.AgentOrchestrator.Infrastructure/ChangedService.cs";
        Write(infrastructureSource, "public class ChangedService { }");
        string[] changedFiles = [FixtureSource, ParentSource, infrastructureSource];
        var changes = RepositoryChangeClassifier.Classify(changedFiles);
        var plan = RepositoryTestImpactPlanner.Plan(changes, _root);

        var check = Assert.Single(plan.Checks, check => check.TestProject == RepositoryTestProject.Infrastructure);
        Assert.DoesNotContain("--filter", check.Command);
        Assert.Equal("ParentOwnedTests", Assert.Single(check.ChangedTestClasses));
        var context = ConductorDriver.BuildPreReviewEvidenceContext("fixture-candidate", changes, plan);
        Assert.Equal("Infrastructure.Tests: " + ParentFilter, context.FocusedRequest);
    }

    [Fact]
    public void ChangedExtractedSourceKeepsExistingFilter()
    {
        const string extractedRoot = TestRoot + "ProviderEnvironment/";
        const string extractedSource = extractedRoot + "ProviderOwnedTests.cs";
        Write(extractedRoot + "Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests.csproj", "<Project />");
        Write(extractedSource, TestSource("ProviderOwnedTests"));

        var plan = RepositoryTestImpactPlanner.Plan([extractedSource], _root);

        var check = Assert.Single(plan.Checks, check => check.TestProject == RepositoryTestProject.Infrastructure);
        Assert.Equal("FullyQualifiedName~ProviderOwnedTests", check.Command[^1]);
    }

    private void AssertParentSelection(string[] changedFiles)
    {
        var changes = RepositoryChangeClassifier.Classify(changedFiles);
        var plan = RepositoryTestImpactPlanner.Plan(changes, _root);
        var check = Assert.Single(plan.Checks, check => check.TestProject == RepositoryTestProject.Infrastructure);
        Assert.Equal(ParentFilter, check.Command[^1]);
        Assert.Equal("ParentOwnedTests", Assert.Single(check.TestClassSelections!));
        Assert.DoesNotContain("ShardProbeAlphaTests", check.ChangedTestClasses);

        var context = ConductorDriver.BuildPreReviewEvidenceContext("fixture-candidate", changes, plan);
        Assert.Equal("Infrastructure.Tests: " + ParentFilter, context.FocusedRequest);
        Assert.Equal(context.FocusedRequest, Assert.Single(context.SelectedFocusedTests));
    }

    private static string TestSource(string name) =>
        $"public class {name} {{ [Xunit.Fact] public void UsesContract() {{ _ = new ChangedContract(); }} }}";

    private void Write(string relativePath, string contents)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
}
