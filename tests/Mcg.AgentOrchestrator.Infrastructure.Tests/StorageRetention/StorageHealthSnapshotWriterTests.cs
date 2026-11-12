using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Xunit;

// Parallel-safe: private retention roots, no public overload or machine MTP root.
public sealed class StorageHealthSnapshotWriterTests
{
    [Fact]
    public void CompletedRun_PublishesTheCapturedReportOnce()
    {
        using var fixture = new RetentionReclaimFixture();
        var entries = FixtureEntries();
        foreach (var name in new[] { "z", "a" })
        {
            Directory.CreateDirectory(Path.Combine(fixture.OrchestratorDirectory, name));
            File.WriteAllBytes(Path.Combine(fixture.OrchestratorDirectory, name, "data"), [1, 2]);
        }
        File.WriteAllText(Path.Combine(fixture.OrchestratorDirectory, "unknown"), "unregistered");
        var retiredPath = Path.Combine(fixture.OrchestratorDirectory, "retired-during-sweep");
        File.WriteAllText(retiredPath, "must be removed before measuring");
        CliStorageHealthQueryCommand.Report? captured = null;
        var calls = 0;
        var writer = new StorageHealthSnapshotWriter((directory, registry) =>
        {
            calls++;
            Assert.False(File.Exists(SnapshotPath(fixture)));
            Assert.False(File.Exists(retiredPath));
            return captured = CliStorageHealthQueryCommand.Build(directory, registry);
        }, entries);

        var result = Run(fixture, writer, () =>
        {
            Assert.Equal(0, calls);
            File.Delete(retiredPath);
        });

        Assert.DoesNotContain(result.Decisions, row => row.Action == EvidenceRetentionAction.Failed);
        Assert.DoesNotContain(result.Decisions, row => row.Reason == "storage-health-snapshot-failed");
        Assert.Equal(1, calls);
        Assert.NotNull(captured);
        Assert.Equal(2, captured.Totals.Over);
        Assert.Equal(1, captured.Totals.Unregistered);
        Assert.Equal("a", captured.Classes.First(row => row.Status == "over").Pattern);
        AssertPublished(fixture, captured);
        Assert.Empty(Directory.GetFiles(fixture.ExecutionDirectory, "storage-health-snapshot-*.tmp"));
    }

    [Fact]
    public void NextRun_CountsThePreviousRegisteredSnapshot()
    {
        using var fixture = new RetentionReclaimFixture();
        Run(fixture, StorageHealthSnapshotWriter.Default);
        var previousSize = new FileInfo(SnapshotPath(fixture)).Length;
        CliStorageHealthQueryCommand.Report? captured = null;
        var result = Run(fixture, new((directory, entries) =>
            captured = CliStorageHealthQueryCommand.Build(directory, entries)));

        Assert.DoesNotContain(result.Decisions, row => row.Action == EvidenceRetentionAction.Failed);
        Assert.NotNull(captured);
        var row = Assert.Single(captured.Classes, row => row.Pattern == StorageHealthSnapshotWriter.FileName);
        Assert.Equal(previousSize, row.Bytes);
        Assert.Equal(1, row.Files);
        Assert.Equal(0, captured.Totals.Over);
        AssertPublished(fixture, captured);
        using var json = JsonDocument.Parse(File.ReadAllText(SnapshotPath(fixture)));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("largestOverBudgetClass").ValueKind);
        var entry = OrchestratorArtifactRegistry.Classify(StorageHealthSnapshotWriter.FileName,
            OrchestratorArtifactRegistry.Entries);
        Assert.NotNull(entry);
        Assert.Equal("StorageHealthSnapshotWriter", entry.Owner);
        Assert.Equal(OrchestratorArtifactRegistry.RetentionKind.KeepForever, entry.Retention);
    }

    [Fact]
    public void FailedSweep_PreservesThePreviousSnapshotWithoutMeasuring()
    {
        using var fixture = new RetentionReclaimFixture();
        File.WriteAllText(SnapshotPath(fixture), "previous snapshot");
        var calls = 0;
        var writer = new StorageHealthSnapshotWriter((_, _) =>
        {
            calls++;
            throw new InvalidOperationException("must not measure a failed sweep");
        });

        var result = Run(fixture, writer, () => throw new IOException("sweep fault"));

        Assert.Contains(result.Decisions, row => row.Action == EvidenceRetentionAction.Failed && row.Reason == "sweep-failed");
        Assert.Equal(0, calls);
        Assert.Equal("previous snapshot", File.ReadAllText(SnapshotPath(fixture)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefreshFailure_PreservesOldBytesAndReportsAnAdvisory(bool failMeasurement)
    {
        using var fixture = new RetentionReclaimFixture();
        File.WriteAllText(SnapshotPath(fixture), "previous snapshot");
        using (var held = failMeasurement ? null : new FileStream(SnapshotPath(fixture),
            FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var calls = 0;
            var writer = new StorageHealthSnapshotWriter((directory, entries) =>
            {
                calls++;
                if (failMeasurement) throw new IOException("measurement fault");
                return CliStorageHealthQueryCommand.Build(directory, entries);
            });

            var result = Run(fixture, writer);

            Assert.Equal(1, calls);
            var diagnostic = Assert.Single(result.Decisions, row => row.Reason == "storage-health-snapshot-failed");
            Assert.Equal(EvidenceRetentionAction.Preserved, diagnostic.Action);
            Assert.Equal(nameof(IOException), diagnostic.FailureExceptionType);
            Assert.DoesNotContain(result.Decisions, row => row.Action == EvidenceRetentionAction.Failed);
        }
        Assert.Equal("previous snapshot", File.ReadAllText(SnapshotPath(fixture)));
        Assert.Empty(Directory.GetFiles(fixture.ExecutionDirectory, "storage-health-snapshot-*.tmp"));
        Assert.Empty(Directory.GetFiles(fixture.OrchestratorDirectory, "*.tmp"));
    }

    [Fact]
    public void MissingDirectory_IsLeftAbsentWithoutMeasurement()
    {
        using var fixture = new RetentionReclaimFixture();
        var missing = Path.Combine(fixture.ExecutionDirectory, "absent");
        var decisions = new List<EvidenceRetentionDecision>();
        new StorageHealthSnapshotWriter((_, _) => throw new InvalidOperationException("must not measure"))
            .Publish(missing, RetentionReclaimFixture.Now, decisions);
        Assert.False(Directory.Exists(missing));
        Assert.Empty(decisions);
    }

    private static string SnapshotPath(RetentionReclaimFixture fixture) =>
        Path.Combine(fixture.OrchestratorDirectory, StorageHealthSnapshotWriter.FileName);

    private static StorageRetentionResult Run(RetentionReclaimFixture fixture, StorageHealthSnapshotWriter writer,
        Action? beforeArchive = null) => StorageRetentionMaintenance.Run(fixture.LogDirectory,
        fixture.OrchestratorDirectory, fixture.ExecutionDirectory, [], RetentionReclaimFixture.Now,
        beforeGoalJournalArchiveForTests: beforeArchive, mtpResultsRoot: null, storageHealthSnapshotWriter: writer);

    private static IReadOnlyList<OrchestratorArtifactRegistry.Entry> FixtureEntries() =>
        OrchestratorArtifactRegistry.Entries.Concat(new[] { "z", "a" }.Select(name =>
            new OrchestratorArtifactRegistry.Entry(name, "fixture", OrchestratorArtifactRegistry.WriterKind.Source,
                null, OrchestratorArtifactRegistry.RetentionKind.KeepForever, null, 1, "fixture"))).ToArray();

    private static void AssertPublished(RetentionReclaimFixture fixture, CliStorageHealthQueryCommand.Report captured)
    {
        Assert.True(File.Exists(SnapshotPath(fixture)));
        using var document = JsonDocument.Parse(File.ReadAllText(SnapshotPath(fixture)));
        var json = document.RootElement;
        Assert.Equal(captured.Totals.Bytes, json.GetProperty("totalBytes").GetInt64());
        Assert.Equal(captured.Totals.Files, json.GetProperty("totalFiles").GetInt64());
        Assert.Equal(captured.Totals.Over, json.GetProperty("overBudgetCount").GetInt32());
        Assert.Equal(captured.Totals.Unregistered, json.GetProperty("unregisteredCount").GetInt32());
        Assert.Equal(captured.Classes.FirstOrDefault(row => row.Status == "over")?.Pattern,
            json.GetProperty("largestOverBudgetClass").GetString());
        Assert.Equal(RetentionReclaimFixture.Now, json.GetProperty("measuredAtUtc").GetDateTimeOffset());
    }
}
