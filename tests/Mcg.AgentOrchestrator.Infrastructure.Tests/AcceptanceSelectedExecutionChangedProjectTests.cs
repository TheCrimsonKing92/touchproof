using Mcg.AgentOrchestrator.Infrastructure;
using static AcceptanceSelectedExecutionProjectFixture;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

// Parallel-safe: each live reader and cache operates on its own disposable repository.
public sealed class AcceptanceSelectedExecutionChangedProjectTests
{
    [Theory]
    [InlineData(CliTestsProject, "cli tests", "source")]
    [InlineData(OwnerConsoleTestsProject, "owner console tests", "source")]
    [InlineData(CliTestsProject, "cli tests", "linked-helper")]
    [InlineData(OwnerConsoleTestsProject, "owner console tests", "linked-helper")]
    [InlineData(CliTestsProject, "cli tests", "deleted-source")]
    [InlineData(OwnerConsoleTestsProject, "owner console tests", "deleted-source")]
    [InlineData(CliTestsProject, "cli tests", "data")]
    [InlineData(OwnerConsoleTestsProject, "owner console tests", "data")]
    [InlineData(CliTestsProject, "cli tests", "project")]
    [InlineData(OwnerConsoleTestsProject, "owner console tests", "project")]
    public void LiveSession_ChangedProjectFile_KeepsOnlyAffectedCheckWhole(
        string projectPath, string checkName, string kind)
    {
        using var repository = Repository();
        repository.Write("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ATests.cs",
            "namespace Candidate; public class A { [Xunit.Fact] public void Runs() { } }");
        foreach (var check in Checks)
        {
            var directory = Path.GetDirectoryName(check.Project!)!;
            repository.Write(Path.Combine(directory, "ChangedTests.cs"),
                "namespace Candidate; public class ChangedTests { private SharedHelper helper; [Xunit.Fact] public void Runs() { } }");
            repository.Write(Path.Combine(directory, "OtherTests.cs"),
                "namespace Candidate; public class OtherTests { [Xunit.Fact] public void Runs() { } }");
        }
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var changedFile = Path.Combine(projectDirectory, "ChangedTests.cs");
        if (kind == "linked-helper")
        {
            changedFile = "shared/SharedHelper.cs";
            repository.Write(changedFile, "namespace Candidate; public class SharedHelper { }");
            var projectFile = Path.Combine(repository.Root, projectPath);
            var include = Path.GetRelativePath(Path.Combine(repository.Root, projectDirectory),
                Path.Combine(repository.Root, changedFile));
            File.WriteAllText(projectFile, File.ReadAllText(projectFile).Replace("</Project>",
                $"<ItemGroup><Compile Include=\"{include}\" /></ItemGroup></Project>", StringComparison.Ordinal));
        }
        else if (kind == "deleted-source")
        {
            File.Delete(Path.Combine(repository.Root, changedFile));
        }
        else if (kind == "data")
        {
            changedFile = Path.Combine(projectDirectory, "input.json");
            repository.Write(changedFile, "{}");
        }
        else if (kind == "project") changedFile = projectPath;

        // A shipping change also exercises the second reach; it does not reach either project.
        string[] changes = [RepositoryTestReachTests.RepositoryFixture.Source, changedFile];
        var session = new AcceptanceSelectedExecutionSession(AcceptanceSelectedExecutionMode.Enforce,
            repository.Root, changes);
        var plan = EffectivePlan(repository.Root, session, changes);

        Assert.True(session.Applied); // A full-gate fallback cannot satisfy the regression.
        var entry = Assert.Single(session.ProjectChecks, item => item.Name == checkName);
        Assert.Equal("retained", entry.Lane.Disposition);
        Assert.Contains("Candidate.OtherTests", entry.Lane.SelectedClasses);
        Assert.Empty(entry.Lane.SkippedClasses);
        Assert.Null(entry.Lane.Filter);
        var original = Assert.Single(Checks, item => item.Name == checkName);
        Assert.Equal(original.Arguments, Assert.Single(plan.Checks, item => item.Name == checkName).Arguments);
        Assert.Same(original, Assert.Single(session.TransformProjectChecks(Checks)));
        Assert.Equal("skipped", Assert.Single(session.ProjectChecks, item => item.Name != checkName).Lane.Disposition);
        Assert.DoesNotContain(plan.Checks, item => Checks.Any(other => other.Name != checkName && other.Name == item.Name));
    }
}
