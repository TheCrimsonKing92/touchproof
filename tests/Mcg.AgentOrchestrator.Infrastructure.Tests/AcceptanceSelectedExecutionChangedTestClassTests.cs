using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static AcceptanceSelectedExecutionProjectFixture;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

// Parallel-safe: each live selection owns its fixture repository and cache identity.
public sealed class AcceptanceSelectedExecutionChangedTestClassTests
{
    private const string OwnerCheck = "owner console tests";
    private static readonly string OwnerDirectory = Path.GetDirectoryName(OwnerConsoleTestsProject)!;
    private static readonly string ChangedFile = Path.Combine(OwnerDirectory, "ChangedTests.cs");

    [Fact]
    public void LiveSession_ChangedClassSource_NarrowsCheckToDeclarationsAndReach()
    {
        using var repository = Repository();
        WriteTests(repository);

        var session = new AcceptanceSelectedExecutionSession(AcceptanceSelectedExecutionMode.Enforce, repository.Root, [ChangedFile]);
        var lane = Transform(session);

        Assert.Equal("narrowed", lane.Disposition);
        Assert.Equal(new[] { "Candidate.ChangedFirstTests", "Candidate.ChangedSecondTests", "Candidate.ReachedTests" },
            lane.SelectedClasses);
        Assert.Equal(new[] { "Candidate.UnrelatedTests" }, lane.SkippedClasses);
        Assert.Equal("(FullyQualifiedName=Candidate.ChangedFirstTests|FullyQualifiedName=Candidate.ChangedSecondTests|FullyQualifiedName=Candidate.ReachedTests)",
            lane.Filter);
        var check = Assert.Single(session.TransformProjectChecks(Checks), item => item.Name == OwnerCheck);
        Assert.Equal(lane.Filter, check.Arguments[^1]);
    }

    [Fact]
    public void LiveSession_TestReach_RetainsReaderReachedClassAndSkipsUnreachedClass()
    {
        using var repository = Repository();
        WriteTests(repository);
        string[] changes = [ChangedFile];
        var reach = ReverseDependencyTestImpactReader.Read(repository.Root, changes, bypassCache: true,
            propagateShippingSourceReach: true, additionalTargetTestProjects: [CliTestsProject, OwnerConsoleTestsProject]);
        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, reach.Outcome);
        Assert.Contains("ReachedTests", reach.TestClassNames);
        Assert.DoesNotContain("UnrelatedTests", reach.TestClassNames);

        var session = new AcceptanceSelectedExecutionSession(AcceptanceSelectedExecutionMode.Enforce, repository.Root, changes);
        var graphSourceRead = false;
        var graphSourcePath = Path.GetFullPath(RepositoryTestReachTests.RepositoryFixture.Source, repository.Root);
        // The lane and support inventories read test sources. The reach reader also reads the project graph's shipping sources.
        var lane = SelectionFileReadProbe.Observe((_, path, _) =>
            graphSourceRead |= path.Equals(graphSourcePath, StringComparison.OrdinalIgnoreCase),
            () => Transform(session));

        Assert.True(graphSourceRead, "The live selection did not read the project graph for test reach.");
        Assert.Equal("narrowed", lane.Disposition);
        Assert.Contains("Candidate.ReachedTests", lane.SelectedClasses);
        Assert.Equal(new[] { "Candidate.UnrelatedTests" }, lane.SkippedClasses);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("linked")]
    [InlineData("unresolved")]
    public void LiveSession_MappedAndUnmappableChanges_RetainsWholeProject(string kind)
    {
        using var repository = Repository();
        WriteTests(repository);
        var extraChange = OwnerConsoleTestsProject;
        if (kind == "linked")
        {
            extraChange = "shared/LinkedTests.cs";
            repository.Write(extraChange, "namespace Candidate; public class LinkedTests { [Xunit.Fact] public void Runs() { } }");
            var include = Path.GetRelativePath(Path.Combine(repository.Root, OwnerDirectory), Path.Combine(repository.Root, extraChange));
            var projectText = File.ReadAllText(Path.Combine(repository.Root, OwnerConsoleTestsProject));
            repository.Write(OwnerConsoleTestsProject, projectText.Replace("</Project>",
                $"<ItemGroup><Compile Include=\"{include}\" /></ItemGroup></Project>", StringComparison.Ordinal));
        }
        else if (kind == "unresolved")
        {
            extraChange = Path.Combine(OwnerDirectory, "PartialInventoryTests.cs");
            repository.Write(extraChange, """
                namespace Candidate;
                public class PartialInventoryTests { [Xunit.Fact] public void Runs() { } }
                internal class NonTestHelper { }
                """);
        }

        var mappedOnly = new AcceptanceSelectedExecutionSession(AcceptanceSelectedExecutionMode.Enforce, repository.Root, [ChangedFile]);
        Assert.Equal("narrowed", Transform(mappedOnly).Disposition);

        var session = new AcceptanceSelectedExecutionSession(AcceptanceSelectedExecutionMode.Enforce, repository.Root,
            [ChangedFile, extraChange]);
        var lane = Transform(session);

        Assert.Equal("retained", lane.Disposition);
        Assert.Null(lane.Filter);
        Assert.Empty(lane.SkippedClasses);
        Assert.Contains("Candidate.UnrelatedTests", lane.SelectedClasses);
        var original = Assert.Single(Checks, item => item.Name == OwnerCheck);
        Assert.Same(original, Assert.Single(session.TransformProjectChecks(Checks), item => item.Name == OwnerCheck));
    }

    [Fact]
    public void LiveSession_TestReachUnavailable_RetainsWholeChangedProject()
    {
        using var repository = Repository();
        WriteTests(repository);
        File.Delete(Path.Combine(repository.Root, CliTestsProject));

        var session = new AcceptanceSelectedExecutionSession(AcceptanceSelectedExecutionMode.Enforce, repository.Root, [ChangedFile]);
        var lane = Transform(session);

        Assert.Equal("retained", lane.Disposition);
        Assert.Null(lane.Filter);
        Assert.Contains("Candidate.UnrelatedTests", lane.SelectedClasses);
    }

    private static AcceptanceSelectedExecutionLane Transform(AcceptanceSelectedExecutionSession session)
    {
        session.Transform(AcceptanceSelectedExecutionFixture.Lanes, Scheduled);
        Assert.True(session.Applied);
        return Assert.Single(session.ProjectChecks, item => item.Project == OwnerConsoleTestsProject).Lane;
    }

    private static void WriteTests(RepositoryTestReachTests.RepositoryFixture repository)
    {
        repository.Write("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ATests.cs",
            "namespace Candidate; public class A { [Xunit.Fact] public void Runs() { } }");
        repository.Write(ChangedFile, """
            namespace Candidate;
            public class ChangedFirstTests { [Xunit.Fact] public void Runs() { } }
            public class ChangedSecondTests { [Xunit.Fact] public void Runs() { } }
            """);
        repository.Write(Path.Combine(OwnerDirectory, "ReachedTests.cs"),
            "namespace Candidate; public class ReachedTests { private ChangedFirstTests value; [Xunit.Fact] public void Runs() { } }");
        repository.Write(Path.Combine(OwnerDirectory, "UnrelatedTests.cs"),
            "namespace Candidate; public class UnrelatedTests { [Xunit.Fact] public void Runs() { } }");
    }
}
