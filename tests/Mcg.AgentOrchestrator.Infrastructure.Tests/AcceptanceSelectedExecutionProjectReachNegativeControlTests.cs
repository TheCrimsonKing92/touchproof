using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using static AcceptanceSelectedExecutionProjectFixture;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

// Parallel-safe: every repository, source and cache identity belongs to a unique fixture root.
public sealed class AcceptanceSelectedExecutionProjectReachNegativeControlTests
{
    [Fact]
    public void ShippingChange_LiveSession_RetainsCliClassAndDetectsItsRemoval()
    {
        using var repository = Repository();
        WriteTests(repository);
        string[] changes = [RepositoryTestReachTests.RepositoryFixture.Source];
        var baseline = ReverseDependencyTestImpactReader.Read(repository.Root, changes, bypassCache: true);
        var reach = ReverseDependencyTestImpactReader.Read(repository.Root, changes, bypassCache: true,
            propagateShippingSourceReach: true, additionalTargetTestProjects: [CliTestsProject, OwnerConsoleTestsProject]);
        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, baseline.Outcome);
        Assert.DoesNotContain("CliReached", baseline.TestClassNames);
        Assert.DoesNotContain("ConsoleReached", baseline.TestClassNames);
        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, reach.Outcome);
        Assert.Contains("CliReached", reach.TestClassNames);
        Assert.Contains("ConsoleReached", reach.TestClassNames);

        var session = new AcceptanceSelectedExecutionSession(AcceptanceSelectedExecutionMode.Enforce,
            repository.Root, changes);
        var plan = EffectivePlan(repository.Root, session, changes);
        Assert.True(session.Applied);
        AssertRetainsCli(plan.Checks);
        Assert.Contains("FullyQualifiedName=Candidate.ConsoleReached",
            Assert.Single(plan.Checks, check => check.Name == "owner console tests").Arguments[^1]);

        // Same assertion, with the reached CLI identity deliberately removed from retention.
        var dropped = Session(reached: ["A", "Candidate.ConsoleReached"], guards: []);
        dropped.Transform(AcceptanceSelectedExecutionFixture.Lanes, Scheduled);
        var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertRetainsCli(dropped.TransformProjectChecks(Checks)));
        Assert.Contains("Assert.Contains() Failure", failure.Message);
    }

    [Fact]
    public void Reader_MissingAdditionalTargetOrIndexBound_FailsClosed()
    {
        using var repository = Repository();
        WriteTests(repository);
        string[] changes = [RepositoryTestReachTests.RepositoryFixture.Source];
        var bounded = ReverseDependencyTestImpactReader.Read(repository.Root, changes, maximumIndexedSourceFiles: 0,
            additionalTargetTestProjects: [CliTestsProject]);
        Assert.Equal(ReverseDependencyDegradationKind.IndexedSourceBound, bounded.DegradationKind);
        var missing = ReverseDependencyTestImpactReader.Read(repository.Root, changes,
            additionalTargetTestProjects: ["tests/Missing/Missing.Tests.csproj"]);
        Assert.Equal(ReverseDependencySelectionOutcome.Unreadable, missing.Outcome);
        Assert.Empty(missing.TestClassNames);
    }

    [Fact]
    public void LiveSession_UnreadableAdditionalReach_KeepsProjectChecksWhole()
    {
        using var repository = Repository();
        WriteTests(repository);
        File.Delete(Path.Combine(repository.Root, OwnerConsoleTestsProject));
        var session = new AcceptanceSelectedExecutionSession(AcceptanceSelectedExecutionMode.Enforce,
            repository.Root, [RepositoryTestReachTests.RepositoryFixture.Source]);
        session.Transform(AcceptanceSelectedExecutionFixture.Lanes, Scheduled);
        Assert.True(session.Applied);
        Assert.All(session.ProjectChecks, entry => Assert.Equal("retained", entry.Lane.Disposition));
        Assert.Equal(Checks, session.TransformProjectChecks(Checks));
    }

    private static void AssertRetainsCli(IReadOnlyList<Check> checks)
    {
        Assert.Contains(checks, check => check.Name == "cli tests" &&
            check.Arguments.Contains("(FullyQualifiedName=Candidate.CliReached)", StringComparer.Ordinal));
    }

    private static void WriteTests(RepositoryTestReachTests.RepositoryFixture repository)
    {
        repository.Write("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ATests.cs",
            "namespace Candidate; public class A { [Xunit.Fact] public void Runs() { } }");
        repository.Write("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/CliReached.cs",
            "namespace Candidate; public class CliReached { private ChangedSource source; [Xunit.Fact] public void Runs() { } }");
        repository.Write("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OwnerConsole/ConsoleReached.cs",
            "namespace Candidate; public class ConsoleReached { private ChangedSource source; [Xunit.Fact] public void Runs() { } }");
    }
}
