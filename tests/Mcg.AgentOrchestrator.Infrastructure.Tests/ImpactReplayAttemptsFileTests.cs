using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: each replay uses its own temporary directory, writers, clock, and fake inventory.
public sealed class ImpactReplayAttemptsFileTests
{
    [Fact]
    public void ListedSubsetOverDefaultLimitReplaysInInventoryOrder()
    {
        using var fixture = new ImpactReplayObservationFixture();
        var storedIds = Enumerable.Range(0, 130).Select(index => $"attempt-{index:D3}").ToArray();
        fixture.Source.Attempts = storedIds.Select(ImpactReplayObservationFixture.Attempt).ToArray();
        var expected = storedIds.Skip(11).Take(101).ToArray();
        var path = WriteSet(fixture, 1, expected.Reverse().ToArray());

        var report = RunAndRead(fixture, path);

        Assert.Equal(expected, report.Attempts.Select(attempt => attempt.AttemptId));
        Assert.Empty(report.SkippedInputs);
    }

    [Fact]
    public void UnmatchedIdIsSkippedWithReasonWhileOtherIdsReplay()
    {
        using var fixture = new ImpactReplayObservationFixture();
        fixture.Source.Attempts = new[] { "first", "MISSING-ID", "second" }
            .Select(ImpactReplayObservationFixture.Attempt).ToArray();
        var path = WriteSet(fixture, 1, ["first", "missing-id", "second"]);

        var report = RunAndRead(fixture, path);

        Assert.Equal(new[] { "first", "second" }, report.Attempts.Select(attempt => attempt.AttemptId));
        var diagnostic = Assert.Single(report.SkippedInputs);
        Assert.Equal("missing-id", diagnostic.Source);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Reason));
        var status = fixture.Status();
        Assert.Equal("completed", status.Completion);
        Assert.Equal(1, status.Progress.Skipped);
    }

    [Theory]
    [InlineData("""{"attemptIds":["one"]}""", "Missing version")]
    [InlineData("""{"version":1.5,"attemptIds":["one"]}""", "integer")]
    [InlineData("""{"version":0,"attemptIds":["one"]}""", "positive")]
    [InlineData("""{"version":-1,"attemptIds":["one"]}""", "positive")]
    [InlineData("""{"version":1}""", "Missing attemptIds")]
    [InlineData("""{"version":1,"attemptIds":"one"}""", "must be an array")]
    [InlineData("""{"version":1,"attemptIds":[]}""", "array is empty")]
    [InlineData("""{"version":1,"attemptIds":[""]}""", "Blank")]
    [InlineData("""{"version":1,"attemptIds":["  "]}""", "Blank")]
    [InlineData("""{"version":1,"attemptIds":[null]}""", "Blank")]
    [InlineData("""{"version":1,"attemptIds":[5]}""", "Blank")]
    [InlineData("""{"version":1,"attemptIds":["one","one"]}""", "Duplicate attempt id 'one'")]
    public void ReaderNamesInvalidSetInput(string json, string problem)
    {
        using var fixture = new ImpactReplayObservationFixture();
        var path = Path.Combine(fixture.Root, "attempts.json");
        File.WriteAllText(path, json);

        var error = Assert.Throws<InvalidDataException>(() => ImpactReplayFixedSet.Read(path));

        Assert.Contains(problem, error.Message);
    }

    [Theory]
    [InlineData("--since", "2026-11-01")]
    [InlineData("--limit", "100")]
    public void ConflictingFlagsReturnUsageWithoutStartingReplay(string flag, string value)
    {
        using var fixture = new ImpactReplayObservationFixture();
        var path = WriteSet(fixture, 1, ["first"]);

        AssertUsageError(fixture, path, [flag, value]);
        Assert.Equal(CliCommandHelp.ImpactReplayUsage + Environment.NewLine, fixture.Error.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrMalformedFileReturnsUsageWithoutStartingReplay(bool malformed)
    {
        using var fixture = new ImpactReplayObservationFixture();
        var path = Path.Combine(fixture.Root, "attempts.json");
        if (malformed)
            File.WriteAllText(path, "{");

        AssertUsageError(fixture, path, []);
        Assert.Contains("Impact replay attempts file is invalid", fixture.Error.ToString());
    }

    [Fact]
    public void CheckpointRecordsSetVersionAndAbsolutePath()
    {
        using var fixture = new ImpactReplayObservationFixture();
        const int version = 7;
        var path = WriteSet(fixture, version, ["first"]);

        var report = RunAndRead(fixture, Path.Combine(fixture.Root, ".", Path.GetFileName(path)), "--floor", "Chaos gate");

        var configuration = fixture.Status().Identity.Configuration;
        Assert.Equal(version, configuration.AttemptSetVersion);
        Assert.Equal(path, configuration.AttemptSetPath);
        Assert.Null(configuration.Since);
        Assert.Equal("override", report.FloorSource);
        Assert.Contains("Chaos gate", report.FloorLanes);
    }

    [Fact]
    public void CommittedSetIsVersionOneAndUsesOnlyFixtureIds()
    {
        var root = VerifiedRepositoryRoot.Find();
        var set = ImpactReplayFixedSet.Read(Path.Combine(root, "docs", "impact-replay-fixed-set.json"));
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tests",
            "Mcg.AgentOrchestrator.Infrastructure.Tests", "TestData", "Fixtures", "impact-replay-retained-94-regression.json")));
        var fixtureIds = fixture.RootElement.GetProperty("rows").EnumerateArray()
            .Select(row => row[0].GetString()!).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(1, set.Version);
        Assert.NotEmpty(set.AttemptIds);
        Assert.All(set.AttemptIds, id => Assert.Contains(id, fixtureIds));
    }

    [Fact]
    public void UsageShowsAttemptsFileFlag()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(0, CliImpactReplayCommand.Run(["impact-replay", "--help"], "unused", "unused", null, output, error));
        Assert.Contains("--attempts-file <path>", CliCommandHelp.ImpactReplayUsage);
        Assert.Contains("--attempts-file <path>", output.ToString());
        Assert.Empty(error.ToString());
        CliCommandHelp.ThrowIfInvalidFlags(["impact-replay", "--attempts-file", "attempts.json", "--out", "report"]);
    }

    private static string WriteSet(ImpactReplayObservationFixture fixture, int version, string[] attemptIds)
    {
        var path = Path.Combine(fixture.Root, "attempts.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { version, attemptIds }));
        return path;
    }

    private static int Run(ImpactReplayObservationFixture fixture, string path, params string[] extraArgs) =>
        CliImpactReplayCommand.Run(["impact-replay", "--attempts-file", path, "--out", fixture.Destination, .. extraArgs],
            fixture.Root, fixture.Root, null, fixture.Output, fixture.Error, clock: fixture.Clock, source: fixture.Source);

    private static ImpactReplayReport RunAndRead(ImpactReplayObservationFixture fixture, string path, params string[] extraArgs)
    {
        var exitCode = Run(fixture, path, extraArgs);
        Assert.True(exitCode == 0, fixture.Error.ToString());
        return JsonSerializer.Deserialize<ImpactReplayReport>(
            File.ReadAllText(Path.Combine(fixture.Destination, "impact-replay.json")), ImpactReplayCheckpointStore.Json)!;
    }

    private static void AssertUsageError(ImpactReplayObservationFixture fixture, string path, string[] extraArgs)
    {
        var inventoryLoaded = false;
        fixture.Source.BeforeInventory = () => inventoryLoaded = true;

        Assert.Equal(2, Run(fixture, path, extraArgs));
        Assert.Contains(CliCommandHelp.ImpactReplayUsage, fixture.Error.ToString());
        Assert.False(inventoryLoaded);
        Assert.False(Directory.Exists(fixture.Destination));
    }
}
