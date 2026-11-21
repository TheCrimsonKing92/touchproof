using System.Text.Json;
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
    public void LiveSession_ChangedProjectFile_PreservesAffectedTests(
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
            LinkSource(repository.Root, projectPath, changedFile);
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
        Assert.Equal("skipped", Assert.Single(session.ProjectChecks, item => item.Name != checkName).Lane.Disposition);
        Assert.DoesNotContain(plan.Checks, item => Checks.Any(other => other.Name != checkName && other.Name == item.Name));
        if (kind == "source")
        {
            // Changed test classes already have a focused check before whole-project selection runs.
            Assert.DoesNotContain(session.ProjectChecks, item => item.Name == checkName);
            var focused = Assert.Single(plan.Checks, item => item.Project is not null && ProjectMatches(item.Project, projectPath));
            Assert.True(IsFocusedProjectDotnetCheck(focused));
            var filterIndex = focused.Arguments.ToList().IndexOf("--filter");
            Assert.InRange(filterIndex, 0, focused.Arguments.Count - 2);
            var includes = AcceptanceLaneMembership.CreateClassMatcher(new(checkName, focused.Arguments[filterIndex + 1]));
            Assert.True(includes("Candidate.ChangedTests"));
            Assert.False(includes("Candidate.OtherTests"));
            Assert.Same(focused, Assert.Single(session.TransformProjectChecks([focused])));
            return;
        }

        var entry = Assert.Single(session.ProjectChecks, item => item.Name == checkName);
        Assert.Equal("retained", entry.Lane.Disposition);
        Assert.Contains("Candidate.OtherTests", entry.Lane.SelectedClasses);
        Assert.Empty(entry.Lane.SkippedClasses);
        Assert.Null(entry.Lane.Filter);
        var original = Assert.Single(Checks, item => item.Name == checkName);
        Assert.Equal(original.Arguments, Assert.Single(plan.Checks, item => item.Name == checkName).Arguments);
        Assert.Same(original, Assert.Single(session.TransformProjectChecks(Checks)));
    }

    [Theory]
    [InlineData(100, false)]
    [InlineData(1, true)]
    public void LiveSession_ChangedLeafAndLinkedSupport_PreservesBothTestClasses(int threshold, bool fullRun)
    {
        using var switches = AcceptanceShardPolicySwitches.Use(new(FullShards: false, ChangeScoped: true));
        using var repository = Repository();
        repository.Write("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ATests.cs",
            "namespace Candidate; public class A { [Xunit.Fact] public void Runs() { } }");
        const string support = "shared/SharedSupport.cs";
        repository.Write(support, "namespace Candidate; public class SharedSupport { }");
        LinkSource(repository.Root, OwnerConsoleTestsProject, support);
        var directory = Path.GetDirectoryName(OwnerConsoleTestsProject)!;
        var leaf = Path.Combine(directory, "LeafTests.cs");
        repository.Write(leaf, "namespace Candidate; public class LeafTests { [Xunit.Fact] public void Runs() { } }");
        repository.Write(Path.Combine(directory, "Helper.cs"),
            "namespace Candidate; public class Helper { public SharedSupport Value = new(); }");
        repository.Write(Path.Combine(directory, "ReachedTests.cs"),
            "namespace Candidate; public class ReachedTests { public Helper Value = new(); [Xunit.Fact] public void Runs() { } }");
        repository.Write(Path.Combine(directory, "DirectTests.cs"),
            "namespace Candidate; public class DirectTests { public SharedSupport Value = new(); [Xunit.Fact] public void Runs() { } }");
        repository.Write(Path.Combine(directory, "UnrelatedTests.cs"),
            "namespace Candidate; public class UnrelatedTests { [Xunit.Fact] public void Runs() { } }");
        string[] changes = [RepositoryTestReachTests.RepositoryFixture.Source, leaf, support];
        var settings = new AcceptanceGateEngineSettings
        {
            SupportClosureClassThresholdValue = JsonSerializer.SerializeToElement(threshold)
        };
        var session = new AcceptanceSelectedExecutionSession(AcceptanceSelectedExecutionMode.Enforce,
            repository.Root, changes, engineSettings: settings);

        var plan = EffectivePlan(repository.Root, session, changes);

        Assert.True(session.Applied);
        var dispatched = Assert.Single(plan.Checks, check => check.Project is not null &&
            ProjectMatches(check.Project, OwnerConsoleTestsProject));
        Assert.Equal(fullRun, plan.PolicyShardPlan.RequiresFullSupportRun(OwnerConsoleTestsProject));
        if (fullRun)
        {
            Assert.Equal(Checks.Single(check => check.Project == OwnerConsoleTestsProject).Arguments, dispatched.Arguments);
            Assert.Equal(support, Assert.Single(session.ReadSupportFallbacks()).Path);
            return;
        }

        Assert.True(IsFocusedProjectDotnetCheck(dispatched));
        var filterIndex = dispatched.Arguments.ToList().IndexOf("--filter");
        Assert.InRange(filterIndex, 0, dispatched.Arguments.Count - 2);
        var includes = AcceptanceLaneMembership.CreateClassMatcher(new(dispatched.Name, dispatched.Arguments[filterIndex + 1]));
        Assert.True(includes("Candidate.LeafTests"));
        Assert.True(includes("Candidate.ReachedTests"));
        Assert.True(includes("Candidate.DirectTests"));
        Assert.False(includes("Candidate.UnrelatedTests"));
    }

    private static void LinkSource(string root, string projectPath, string sourcePath)
    {
        var projectFile = Path.Combine(root, projectPath);
        var include = Path.GetRelativePath(Path.GetDirectoryName(projectFile)!, Path.Combine(root, sourcePath));
        File.WriteAllText(projectFile, File.ReadAllText(projectFile).Replace("</Project>",
            $"<ItemGroup><Compile Include=\"{include}\" /></ItemGroup></Project>", StringComparison.Ordinal));
    }
}
