using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static AcceptanceSelectedExecutionProjectFixture;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;
using TestRepository = RepositoryTestReachTests.RepositoryFixture;

// Parallel-safe: the checkout is private and the extract cache and probe are flow-scoped.
public sealed class AcceptanceSelectionSharedSourceTextTests
{
    private const string LaneTest = TestRepository.TestDirectory + "/ATests.cs";
    private const string PartialTest = TestRepository.TestDirectory + "/ATests.Partial.cs";
    private const string OwnerDirectory = TestRepository.TestDirectory + "/OwnerConsole";
    private const string ChangedTest = OwnerDirectory + "/ChangedTests.cs";
    private const string TestConsumer = OwnerDirectory + "/TestConsumerTests.cs";
    private const string ShippingConsumer = OwnerDirectory + "/ShippingConsumerTests.cs";
    private const string Unrelated = OwnerDirectory + "/UnrelatedTests.cs";
    private static readonly string[] Changes = [TestRepository.Source, ChangedTest];
    private static readonly string[] SourcePaths =
        [TestRepository.Source, TestRepository.Other, LaneTest, PartialTest, ChangedTest, TestConsumer, ShippingConsumer, Unrelated];
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    [Fact]
    public void ReadLive_WithStore_ReadsEachIndexedSourceOnce()
    {
        using var repository = CreateRepository();
        var (input, reads) = ObserveSourceReads(() =>
            AcceptanceImpactSelectionReader.ReadLive(repository.Root, Changes, sourceTexts: new SelectionSourceTexts()));

        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, input.Selection.Outcome);
        AssertSourcesReadOnce(repository.Root, reads);
    }

    [Fact]
    public void Transform_WithShippingAndTestChanges_SharesTextAcrossAllThreeReads()
    {
        using var repository = CreateRepository();
        var (live, _) = ObserveSourceReads(() =>
            AcceptanceImpactSelectionReader.ReadLive(repository.Root, Changes, sourceTexts: new SelectionSourceTexts()));
        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, live.Selection.Outcome);
        Assert.DoesNotContain(live.Selection.TestClassNames, name =>
            name is "ShippingConsumerTests" or "Candidate.ShippingConsumerTests" or "TestConsumerTests" or "Candidate.TestConsumerTests");

        var (lane, reads) = ObserveSourceReads(() => SelectOwnerCheck(repository.Root),
            AcceptanceSelectedExecutionSession.SelectionReadPart);

        Assert.Equal("narrowed", lane.Disposition);
        // Each consumer is reachable from only its own seed and is absent from live selection.
        Assert.Equal(new[] { "Candidate.ChangedTests", "Candidate.ShippingConsumerTests", "Candidate.TestConsumerTests" },
            lane.SelectedClasses);
        Assert.Equal(new[] { "Candidate.UnrelatedTests" }, lane.SkippedClasses);
        AssertSourcesReadOnce(repository.Root, reads);
    }

    [Fact]
    public void ReadLive_WithAndWithoutStore_ResolvesTheSameSelection()
    {
        using var repository = CreateRepository();
        var (withoutStore, uncachedReads) = ObserveSourceReads(() =>
            AcceptanceImpactSelectionReader.ReadLive(repository.Root, Changes));
        var (withStore, sharedReads) = ObserveSourceReads(() =>
            AcceptanceImpactSelectionReader.ReadLive(repository.Root, Changes, sourceTexts: new SelectionSourceTexts()));

        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, withoutStore.Selection.Outcome);
        Assert.Contains("A", withoutStore.Selection.TestClassNames);
        Assert.Single(withoutStore.Classes, item => item.FullName == "Candidate.A");
        Assert.Equal(withoutStore.Selection.Outcome, withStore.Selection.Outcome);
        Assert.Equal(withoutStore.Selection.TestClassNames, withStore.Selection.TestClassNames);
        Assert.Equal(withoutStore.Classes.Select(item => item.FullName), withStore.Classes.Select(item => item.FullName));
        Assert.Equal(withoutStore.GuardClasses, withStore.GuardClasses);
        var overlap = Path.GetFullPath(LaneTest, repository.Root);
        Assert.Equal(2, uncachedReads.Count(path => PathComparer.Equals(path, overlap)));
        Assert.Single(sharedReads, path => PathComparer.Equals(path, overlap));
    }

    [Fact]
    public void Transform_InNewSession_ReadsAndSelectsRewrittenSource()
    {
        using var repository = CreateRepository();
        var (first, firstReads) = ObserveSourceReads(() => SelectOwnerCheck(repository.Root),
            AcceptanceSelectedExecutionSession.SelectionReadPart);
        repository.Write(ShippingConsumer,
            "namespace Candidate; public class RewrittenShippingConsumerTests { private ChangedSource source; [Xunit.Fact] public void Runs() { } }");
        var (second, secondReads) = ObserveSourceReads(() => SelectOwnerCheck(repository.Root),
            AcceptanceSelectedExecutionSession.SelectionReadPart);

        var rewritten = Path.GetFullPath(ShippingConsumer, repository.Root);
        Assert.Single(firstReads, path => PathComparer.Equals(path, rewritten));
        Assert.Single(secondReads, path => PathComparer.Equals(path, rewritten));
        Assert.Equal("narrowed", first.Disposition);
        Assert.Equal("narrowed", second.Disposition);
        Assert.Contains("Candidate.ShippingConsumerTests", first.SelectedClasses);
        Assert.DoesNotContain("Candidate.RewrittenShippingConsumerTests", first.SelectedClasses);
        Assert.Contains("Candidate.RewrittenShippingConsumerTests", second.SelectedClasses);
        Assert.DoesNotContain("Candidate.ShippingConsumerTests", second.SelectedClasses);
    }

    private static AcceptanceSelectedExecutionLane SelectOwnerCheck(string root)
    {
        var session = new AcceptanceSelectedExecutionSession(AcceptanceSelectedExecutionMode.Enforce, root, Changes);
        session.Transform(AcceptanceSelectedExecutionFixture.Lanes, Scheduled);
        Assert.True(session.Applied);
        return Assert.Single(session.ProjectChecks, item => item.Project == OwnerConsoleTestsProject).Lane;
    }

    private static (T Result, string[] Reads) ObserveSourceReads<T>(Func<T> action, string? readPart = null)
    {
        using var cleanExtracts = AcceptanceTestClassSourceCache.Use(new AcceptanceTestClassSourceCache(null));
        var reads = new ConcurrentQueue<string>();
        var result = SelectionFileReadProbe.Observe((part, path, _) =>
        {
            if ((readPart is null || part == readPart) && Path.GetExtension(path) == ".cs")
                reads.Enqueue(path);
        }, action);
        return (result, reads.ToArray());
    }

    private static void AssertSourcesReadOnce(string root, string[] reads)
    {
        var expected = SourcePaths.Select(path => Path.GetFullPath(path, root)).Order(PathComparer);
        Assert.Equal(expected, reads.Order(PathComparer), PathComparer);
    }

    private static TestRepository CreateRepository()
    {
        var repository = Repository();
        repository.Write(LaneTest,
            "namespace Candidate; public partial class A { private ChangedSource source; [Xunit.Fact] public void Runs() { } }");
        repository.Write(PartialTest, "namespace Candidate; public partial class A { private void Helper() { } }");
        repository.Write(ChangedTest, "namespace Candidate; public class ChangedTests { [Xunit.Fact] public void Runs() { } }");
        repository.Write(TestConsumer,
            "namespace Candidate; public class TestConsumerTests { private ChangedTests source; [Xunit.Fact] public void Runs() { } }");
        repository.Write(ShippingConsumer,
            "namespace Candidate; public class ShippingConsumerTests { private ChangedSource source; [Xunit.Fact] public void Runs() { } }");
        repository.Write(Unrelated, "namespace Candidate; public class UnrelatedTests { [Xunit.Fact] public void Runs() { } }");
        return repository;
    }
}
