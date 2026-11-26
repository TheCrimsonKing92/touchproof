using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

public sealed class FocusedEvidenceUnexecutedClassCauseTests
{
    [Theory]
    [InlineData("CoreOwnedTests", "wrong-project", CoreTestsProject)]
    [InlineData("FullyQualifiedName~CoreOwnedTests.Example", "wrong-project", CoreTestsProject)]
    [InlineData("NowhereTests", "not-found", null)]
    [InlineData("SharedTests", "no-result-recorded", null)]
    [InlineData("NestedTests", "wrong-project", NestedProject)]
    public void Classify_UsesProjectThatOwnsTheDeclaration(string selector, string kind, string? owner)
    {
        using var worktree = new Worktree();
        worktree.Declare(CoreTestsProject, "CoreOwnedTests", "SharedTests");
        worktree.Declare(InfrastructureTestsProject, "SharedTests");
        worktree.Declare(NestedProject, "NestedTests");
        var generated = Path.Combine(worktree.Root, Path.GetDirectoryName(CoreTestsProject)!, "obj");
        Directory.CreateDirectory(generated);
        File.WriteAllText(Path.Combine(generated, "Generated.cs"), "public class NowhereTests { }");

        var cause = FocusedEvidenceUnexecutedClassCause.Classify(worktree.Root, "Infrastructure.Tests", selector);

        Assert.Equal(kind, cause.Kind);
        Assert.Equal(owner, cause.OwningProject);
    }

    [Fact]
    public void MissingWorktree_CannotEstablishThatAClassIsAbsent()
    {
        using var worktree = new Worktree();
        var missingRoot = Path.Combine(worktree.Root, "missing");

        var cause = FocusedEvidenceUnexecutedClassCause.Classify(missingRoot, "Infrastructure.Tests", "UnknownTests");

        Assert.Equal("no-result-recorded", cause.Kind);
    }

    private const string NestedProject =
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Nested/Fixture.Nested.Tests.csproj";

    internal sealed class Worktree : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("focused-evidence-remainder-").FullName;

        internal void Declare(string project, params string[] classes)
        {
            var projectPath = Path.Combine(Root, project);
            var directory = Path.GetDirectoryName(projectPath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(projectPath, """<Project Sdk="Microsoft.NET.Sdk" />""");
            File.WriteAllLines(Path.Combine(directory, "Declarations.cs"),
                classes.Select(name => $"public class {name} {{ }}"));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
