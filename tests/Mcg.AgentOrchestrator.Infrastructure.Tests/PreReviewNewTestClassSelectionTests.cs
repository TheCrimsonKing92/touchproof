using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each row owns its repository, worktree, and source files.
public sealed class PreReviewNewTestClassSelectionTests
{
    private const string CoreTestProject = "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj";
    private const string InfrastructureTestRoot = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/";
    private const string InfrastructureTestProject = InfrastructureTestRoot + "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
    private const string CoreSource = "src/Mcg.AgentOrchestrator.Core/Application/AddedSelectionSource.cs";
    private const string InfrastructureSource = "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AddedSelectionSource.cs";
    private const string AddedTestFile = "AddedSelectionTests.cs";
    private const string Filter = "FullyQualifiedName~AddedSelectionTests";
    private const string DeclaredScope = "docs/selection.md";
    private const string IntegrationBranch = "main";
    private const string TestSource = """
        using Xunit;

        namespace SelectionFixture;

        public sealed class AddedSelectionTests
        {
            [Fact]
            public void ChecksSelection() { }
        }
        """;

    public static TheoryData<string, string, bool> SelectionCases()
    {
        var cases = new TheoryData<string, string, bool>();
        var projects = new[]
        {
            (CoreTestProject, CoreSource),
            (InfrastructureTestProject, InfrastructureSource),
            (InfrastructureTestRoot + "Acceptance/Mcg.AgentOrchestrator.Infrastructure.Acceptance.Tests.csproj", InfrastructureSource),
            (InfrastructureTestRoot + "Cli/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj", "src/Mcg.AgentOrchestrator.App/Cli/AddedSelectionSource.cs"),
            (InfrastructureTestRoot + "OwnerConsole/Mcg.AgentOrchestrator.Infrastructure.OwnerConsole.Tests.csproj", "src/Mcg.AgentOrchestrator.App/Console/AddedSelectionSource.cs"),
            (InfrastructureTestRoot + "StorageRetention/Mcg.AgentOrchestrator.Infrastructure.StorageRetention.Tests.csproj", InfrastructureSource)
        };
        foreach (var (testProject, source) in projects)
        {
            cases.Add(testProject, source, false);
            cases.Add(testProject, source, true);
        }
        return cases;
    }

    [Theory]
    [MemberData(nameof(SelectionCases))]
    public void DirectDiff_WithAddedClass_SelectsFocusedRequest(string testProject, string source, bool changeSource)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            Write(root, ".git", "fixture repository marker");
            SeedLayout(root, testProject, source);
            var changedFiles = AddCandidateFiles(root, testProject, source, changeSource);

            var context = ConductorDriver.BuildPreReviewEvidenceContext("direct-candidate", changedFiles, root);

            AssertSelected(context, root, testProject);
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Theory]
    [MemberData(nameof(SelectionCases))]
    public void GoalWorktreeDiff_WithOrWithoutDeclaredScope_SelectsAddedClass(
        string testProject, string source, bool changeSource)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            MainBranchGitRepositoryTemplate.CopyTo(root, includeSkillCatalog: false);
            SeedLayout(root, testProject, source);
            Git(root, "add", ".");
            Git(root, "commit", "-m", "Seed selection fixture");

            var goalId = GoalId.New();
            var worktree = GoalWorktrees.Ensure(root, goalId, trunkBranch: IntegrationBranch);
            var changedFiles = AddCandidateFiles(worktree, testProject, source, changeSource);
            Git(worktree, "add", ".");
            Git(worktree, "commit", "-m", "Add test class to goal candidate");

            Assert.Equal(changedFiles.Order(StringComparer.Ordinal),
                GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(worktree, IntegrationBranch).Order(StringComparer.Ordinal));
            var candidateSha = Git(worktree, "rev-parse", "HEAD").Trim();
            var tasks = new[] { new TaskSpec(TaskId.New(), "Inspect focused selection", AgentRole.Developer) };
            var unscopedGoal = new Goal(goalId, "Select the added test class.", tasks);
            var scopedGoal = new Goal(goalId, $"Select the added test class.\n\n## Scope\n\n- `{DeclaredScope}`", tasks);
            // Both scope variants use the same committed candidate.
            Assert.Empty(GoalFileScopeInference.FromGoal(unscopedGoal, 4096, out _));
            Assert.Equal(DeclaredScope, Assert.Single(GoalFileScopeInference.FromGoal(scopedGoal, 4096, out _)).Path);

            foreach (var goal in new[] { unscopedGoal, scopedGoal })
            {
                var context = ConductorDriver.BuildPreReviewEvidenceContext(goal, root, IntegrationBranch);

                Assert.Equal(candidateSha, context.CandidateSha);
                AssertSelected(context, worktree, testProject);
            }
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Theory]
    [InlineData("SelectionHelper.cs", "internal static class SelectionHelper { }", "declares no qualifying test class")]
    [InlineData("SecuritySelectionTests.cs", TestSource, "Security, sandbox, credential, or policy-sensitive paths changed")]
    public void EmptySelection_WithChangedTestSource_ReportsPathAndPlannerReason(
        string fileName, string contents, string reason)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            Write(root, ".git", "fixture repository marker");
            var testPath = InfrastructureTestRoot + fileName;
            Write(root, testPath, contents);

            var context = ConductorDriver.BuildPreReviewEvidenceContext("empty-candidate", [testPath], root);

            Assert.True(context.NoApplicableTests, context.MappingReason);
            Assert.False(context.MappingNeedsInput, context.MappingReason);
            Assert.Empty(context.SelectedFocusedTests);
            Assert.Null(context.FocusedRequest);
            Assert.Contains("project-wide checks are deferred to the acceptance gate", context.MappingReason);
            Assert.Contains("Changed test sources were present", context.MappingReason);
            Assert.Contains(testPath, context.MappingReason);
            Assert.Contains(reason, context.MappingReason);
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    private static void AssertSelected(PreReviewEvidenceContext context, string root, string testProject)
    {
        Assert.False(context.NoApplicableTests, context.MappingReason);
        Assert.False(context.MappingNeedsInput, context.MappingReason);
        Assert.NotNull(context.FocusedRequest);
        var request = context.FocusedRequest.Split(';', StringSplitOptions.TrimEntries)
            .FirstOrDefault(item => item.Split(':', 2).Last().Split('|')
                .Any(clause => clause.Trim(' ', '(', ')') == Filter));
        Assert.NotNull(request);
        Assert.Contains(request, context.SelectedFocusedTests);
        Assert.True(FocusedEvidenceRequestResolver.TryBuildFocusedEvidenceChecks(
            request, AcceptanceGateEngineSettings.Load(root), root, out var checks, out _, out var rejection),
            rejection.Detail);
        Assert.Contains(checks, check => check.Project == testProject && check.Arguments.Contains(Filter));
    }

    private static void SeedLayout(string root, string testProject, string source)
    {
        var sourceDirectory = Path.GetDirectoryName(Path.GetDirectoryName(source))!;
        var sourceProject = Path.Combine(sourceDirectory, Path.GetFileName(sourceDirectory) + ".csproj");
        var projectReference = Path.GetRelativePath(Path.Combine(root, Path.GetDirectoryName(testProject)!),
            Path.Combine(root, sourceProject)).Replace('\\', '/');
        Write(root, sourceProject, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        Write(root, testProject, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="{projectReference}" /></ItemGroup>
            </Project>
            """);
        Write(root, "config/acceptance-manifest.json", $$"""
            {
              "engine": {
                "mtpInvocations": [{
                  "project": "{{testProject}}",
                  "executablePathTemplate": "bin/{projectName}{executableExtension}",
                  "arguments": ["{executable}"]
                }]
              },
              "checks": [{ "name": "fixture checks", "type": "dotnet-test", "project": "{{testProject}}" }]
            }
            """);
        Write(root, source, "namespace SelectionFixture; public sealed class AddedSelectionSource { }");
        Write(root, DeclaredScope, "Selection fixture notes");
    }

    private static string[] AddCandidateFiles(string root, string testProject, string source, bool changeSource)
    {
        var testPath = testProject[..(testProject.LastIndexOf('/') + 1)] + AddedTestFile;
        Write(root, testPath, TestSource);
        if (!changeSource) return [testPath];

        Write(root, source, "namespace SelectionFixture; public sealed class AddedSelectionSource { public int Value => 1; }");
        return [testPath, source];
    }

    private static void Write(string root, string relativePath, string contents)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static string Git(string directory, params string[] arguments)
    {
        var result = GitCli.Run(directory, arguments);
        Assert.True(result.Succeeded, $"git {string.Join(' ', arguments)}: {result.Error}");
        return result.Output;
    }
}
