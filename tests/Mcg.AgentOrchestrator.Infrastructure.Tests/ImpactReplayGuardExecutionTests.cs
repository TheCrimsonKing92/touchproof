using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each fixture owns a minimal repository, checkouts and SQLite database.
// The observer delegates every case to production ExecuteCase; no selection is stubbed.
public sealed class ImpactReplayGuardExecutionTests
{
    private const string Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
    private const string Declaration = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/BetaTests.cs";

    [Theory]
    [InlineData("LiveTreeStructural", "live-tree-structural")]
    [InlineData("TestAssemblyMembership", "test-assembly-membership")]
    public void ExecuteCase_DeclaredGuard_CommitsOwnershipBeforeNextCase(
        string marker, string dependencyKind)
    {
        using var fixture = new ImpactReplayFixture(initialTestFiles: new Dictionary<string, string>
        {
            ["BetaTests.cs"] = GuardSource(marker)
        });
        fixture.AddAttempt("first");
        fixture.AddAttempt("second");
        var checkpoint = Path.Combine(fixture.OutputDirectory, ImpactReplayCheckpointStore.FileName);
        var source = new ObservedSource();
        var observedCommit = false;
        source.BeforeCase = executed =>
        {
            if (executed != 1) return;
            var status = Assert.Single(ImpactReplayCheckpointStore.ReadStatus(checkpoint));
            Assert.Equal("Running", status.StoredState);
            Assert.Equal(1, status.Progress.Completed);
            Assert.Equal(1, status.Progress.Replayable);
            var committed = Assert.Single(ReadCases(checkpoint));
            Assert.Equal(ImpactReplayCaseKind.Replayable, committed.Kind);
            AssertGuard(committed.Record!, dependencyKind, ["src/Sample/Alpha.cs"]);
            Assert.False(File.Exists(Path.Combine(fixture.OutputDirectory, "impact-replay.json")));
            observedCommit = true;
        };

        var (report, markdown) = fixture.Run(source: source);

        Assert.True(observedCommit, "The independent reader must observe the first committed case during replay.");
        Assert.Equal(2, source.Executed);
        Assert.Equal(2, report.Attempts.Count);
        foreach (var record in report.Attempts)
        {
            Assert.Equal("Resolved", record.SelectionOutcome);
            Assert.Null(record.DegradationKind);
            AssertGuard(record, dependencyKind, ["src/Sample/Alpha.cs"]);
            Assert.Contains("Alpha lane", record.WouldRunLanes);
            Assert.Contains("Beta lane", record.WouldRunLanes);
        }
        Assert.Contains(Project, markdown);
        Assert.Contains(Declaration, markdown);
        Assert.Contains(dependencyKind, markdown);
        AssertReconciled(fixture, report);
    }

    [Theory]
    [InlineData(95, false, "Resolved")]
    [InlineData(95, true, "Resolved")]
    [InlineData(999, true, "Resolved")]
    public void ExecuteCase_ChangedSupportAndGuard_PreservesCombinedSelection(
        int referencers, bool declaredGuard, string expected)
    {
        // Reverse dependency selection contributes AlphaTests. Changing a test helper
        // contributes its referencers through changed-test augmentation. Beta is an
        // unchanged guard, giving 1,001 identities in the largest case.
        var extraTests = "namespace SampleTests; " + string.Join("\n", Enumerable.Range(0, referencers)
            .Select(index => $"public class AlphaTestsExtra{index} {{ [Xunit.Fact] public void Works() {{ var helper = new Support.Helpers(); }} }}"));
        using var fixture = new ImpactReplayFixture(initialTestFiles: new Dictionary<string, string>
        {
            ["BetaTests.cs"] = GuardSource(declaredGuard ? "LiveTreeStructural" : null),
            ["ExtraTests.cs"] = extraTests,
            ["Support.cs"] = "namespace Support; public class Helpers { public int Value => 1; }"
        }, changedTestFiles: new Dictionary<string, string>
        {
            ["Support.cs"] = "namespace Support; public class Helpers { public int Value => 2; }"
        });
        fixture.AddAttempt("combined-cap");

        var (report, _) = fixture.Run();

        var record = Assert.Single(report.Attempts);
        Assert.Equal(expected, record.SelectionOutcome);
        Assert.Equal(1, record.ChangedSourceFileCount);
        Assert.Equal(referencers + 1, record.SelectedTestClasses.Count);
        Assert.Contains("AlphaTests", record.SelectedTestClasses);
        foreach (var index in Enumerable.Range(0, referencers))
            Assert.Contains($"AlphaTestsExtra{index}", record.SelectedTestClasses);
        if (declaredGuard)
            AssertGuard(record, "live-tree-structural", ["src/Sample/Alpha.cs",
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Support.cs"]);
        else Assert.Empty(record.DeclaredGuards!.Selections);
        Assert.Null(record.DeclaredGuards!.DegradationReason);
        Assert.Null(record.DegradationKind);
        Assert.Equal(declaredGuard, record.WouldRunLanes.Contains("Beta lane"));
        Assert.Equal(!declaredGuard, record.SkippedLanes.Contains("Beta lane"));
        AssertReconciled(fixture, report);
    }

    private static string GuardSource(string? marker) => "namespace SampleTests; " +
        (marker is null ? "" : $"[{marker}] ") +
        "public class BetaTests { [Xunit.Fact] public void Works() { var subject = new Sample.Beta(); } }";

    private static void AssertGuard(ImpactReplayAttemptRecord record, string kind, string[] triggers)
    {
        Assert.NotNull(record.DeclaredGuards);
        Assert.Null(record.DeclaredGuards.DegradationReason);
        var guard = Assert.Single(record.DeclaredGuards.Selections);
        Assert.Equal("SampleTests.BetaTests", guard.ClassName);
        Assert.Equal(Project, guard.Project);
        Assert.Equal(Declaration, guard.DeclarationPath);
        Assert.Equal(kind, guard.DependencyKind);
        Assert.Equal(triggers, guard.TriggerPaths);
        Assert.False(string.IsNullOrWhiteSpace(guard.Reason));
    }

    private static ImpactReplayCaseResult[] ReadCases(string checkpoint)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = checkpoint, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM cases ORDER BY ordinal";
        using var reader = command.ExecuteReader();
        var cases = new List<ImpactReplayCaseResult>();
        while (reader.Read()) cases.Add(JsonSerializer.Deserialize<ImpactReplayCaseResult>(
            reader.GetString(0), ImpactReplayCheckpointStore.Json)!);
        return cases.ToArray();
    }

    private static void AssertReconciled(ImpactReplayFixture fixture, ImpactReplayReport report)
    {
        var checkpoint = Path.Combine(fixture.OutputDirectory, ImpactReplayCheckpointStore.FileName);
        var status = Assert.Single(ImpactReplayCheckpointStore.ReadStatus(checkpoint));
        Assert.Equal(report.Run!.RunId, status.Identity.RunId);
        Assert.Equal("completed", status.Completion);
        Assert.Equal(report.Attempts.Count, status.Progress.Completed);
        Assert.Equal(report.Attempts.Count, status.Progress.Replayable);
        Assert.Equal(0, status.Progress.Skipped);
        Assert.Equal(0, status.Progress.Unreplayable);
        var cases = ReadCases(checkpoint);
        Assert.Equal(report.Attempts.Count, cases.Length);
        for (var index = 0; index < cases.Length; index++)
        {
            Assert.Equal(ImpactReplayCaseKind.Replayable, cases[index].Kind);
            // Full record equality includes guard ownership/reasons, fallback and lane decisions.
            Assert.Equal(ImpactReplayCheckpointStore.Serialize(report.Attempts[index]),
                ImpactReplayCheckpointStore.Serialize(cases[index].Record));
        }
        Assert.Equal(cases.Length, status.Checkouts.Count);
        Assert.All(status.Checkouts, checkout =>
        {
            Assert.Equal("succeeded", checkout.CleanupStatus);
            Assert.Null(checkout.Error);
            Assert.False(Directory.Exists(checkout.Path));
        });
    }

    private sealed class ObservedSource : ImpactReplayExecutionSource
    {
        internal int Executed { get; private set; }
        internal Action<int>? BeforeCase { get; set; }

        internal override ImpactReplayCaseResult ExecuteCase(ImpactReplayStoredAttempt attempt,
            ImpactReplayRunConfiguration configuration, ImpactReplayCaseContext context,
            IReadOnlyList<AcceptanceLaneFlakeRow> flakeRows,
            IReadOnlyDictionary<string, GateStatsReport.FlakeRow> history, IImpactReplaySink sink,
            CancellationToken cancellationToken)
        {
            BeforeCase?.Invoke(Executed);
            var result = base.ExecuteCase(attempt, configuration, context, flakeRows, history, sink, cancellationToken);
            Executed++;
            return result;
        }
    }
}
