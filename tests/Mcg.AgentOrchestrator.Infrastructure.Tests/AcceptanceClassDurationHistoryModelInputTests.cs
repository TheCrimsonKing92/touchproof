using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every test owns its filesystem root; no shared environment changes.
public sealed class AcceptanceClassDurationHistoryModelInputTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "wall-history-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ReadModelInputs_LegacyRecord_PreservesSecondsAndMissingMeasurements()
    {
        Write("""{"version":1,"classes":{"A":[{"attemptId":"old","lane":"L","className":"A","seconds":42}]}}""");
        var input = AcceptanceClassDurationHistoryStore.ReadModelInputs(root, 5, out var failure)["A"];
        Assert.Null(failure);
        Assert.Equal(42, input.Seconds);
        Assert.Null(input.SlotHeldSeconds);
        Assert.Null(input.SlotCount);
        Assert.Equal(42, AcceptanceClassDurationHistoryStore.ReadMedians(root, 5)["A"]);
        Assert.Equal(42, new AcceptanceLaneWallModel(1).Predict([new(input.Seconds, input.SlotHeldSeconds, false)]));
    }

    [Fact]
    public void RecordAndRead_OptionalMeasurements_UseOnlyAvailableSamplesInWindow()
    {
        Record("a", 10, 8, 2);
        Record("b", 20, null, null);
        Record("c", 30, 0, 4);
        var inputs = AcceptanceClassDurationHistoryStore.ReadModelInputs(root, 3, out var failure);
        Assert.Null(failure);
        Assert.Equal(new AcceptanceLaneWallModel.Input(20, 4, 3), inputs["A"]);
        var latest = AcceptanceClassDurationHistoryStore.ReadModelInputs(root, 2, out failure)["A"];
        Assert.Null(failure);
        Assert.Equal(new AcceptanceLaneWallModel.Input(25, 0, 4), latest);
        Assert.Equal(20, AcceptanceClassDurationHistoryStore.ReadMedians(root, 3)["A"]);
    }

    [Theory]
    [InlineData(-1d, 2)]
    [InlineData(double.NaN, 2)]
    [InlineData(double.PositiveInfinity, 2)]
    [InlineData(1d, 0)]
    public void Record_InvalidMeasurements_RejectsSample(double held, int slots)
    {
        Assert.Equal("class-history-invalid-sample", AcceptanceClassDurationHistoryStore.RecordAttempt(root, "bad",
            [new("bad", "L", "A", 10, held, slots)], 5, true, true));
        Assert.Empty(AcceptanceClassDurationHistoryStore.ReadMedians(root, 5));
    }

    [Fact]
    public void Read_InvalidNewField_ReportsCorruptHistory()
    {
        Write("""{"version":1,"classes":{"A":[{"attemptId":"a","lane":"L","className":"A","seconds":42,"slotHeldSeconds":-1}]}}""");
        Assert.Empty(AcceptanceClassDurationHistoryStore.ReadModelInputs(root, 5, out var failure));
        Assert.Equal("class-history-read:InvalidDataException", failure);
    }

    [Fact]
    public void ResultReader_SeparatesWaitHoldAndSerialSeconds_AndDeduplicatesMirrors()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "lane.trx");
        File.WriteAllText(path, """
            <TestRun><TestDefinitions><UnitTest id="a"><TestMethod className="Fixture.A" name="Test" /></UnitTest></TestDefinitions>
            <Results><UnitTestResult testId="a" outcome="Passed" duration="00:00:10" /></Results>
            <ResultSummary outcome="Completed"><Counters total="1" executed="1" notExecuted="0" /></ResultSummary></TestRun>
            """);
        File.WriteAllText(LaneSlotTimingReader.PathFor(path), """
            {"test_name":"Fixture.A.Test","wait_ms":2000,"held_ms":3000,"slot_count":2,"process_id":1}
            {"test_name":"Fixture.A.Test","wait_ms":1000,"held_ms":1000,"slot_count":4,"process_id":1}
            """);
        var mirror = Path.Combine(root, "mirror.trx");
        File.Copy(path, mirror);
        File.Copy(LaneSlotTimingReader.PathFor(path), LaneSlotTimingReader.PathFor(mirror));
        Assert.Equal(new AcceptanceLaneWallModel.Input(7, 4, 4),
            AcceptanceClassDurationResultReader.ReadModelInputs([path, mirror, path])!["Fixture.A"]);
        File.Delete(LaneSlotTimingReader.PathFor(path));
        Assert.Equal(new AcceptanceLaneWallModel.Input(10, null, null),
            AcceptanceClassDurationResultReader.ReadModelInputs([path])!["Fixture.A"]);
    }

    [Theory]
    [InlineData("{\"test_name\":\"Fixture.Other.Test\",\"wait_ms\":0,\"held_ms\":3000,\"slot_count\":2,\"process_id\":1}", 0d, 2)]
    [InlineData("", 0d, null)]
    [InlineData(null, null, null)]
    [InlineData("not-json", null, null)]
    public void ResultReader_ClassWithoutSlotRows_DistinguishesZeroFromUnavailableMeasurements(
        string? slotRows, double? expectedHeld, int? expectedSlots)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "lane.trx");
        File.WriteAllText(path, """
            <TestRun><TestDefinitions><UnitTest id="a"><TestMethod className="Fixture.A" name="Test" /></UnitTest></TestDefinitions>
            <Results><UnitTestResult testId="a" outcome="Passed" duration="00:00:10" /></Results>
            <ResultSummary outcome="Completed"><Counters total="1" executed="1" notExecuted="0" /></ResultSummary></TestRun>
            """);
        if (slotRows is not null) File.WriteAllText(LaneSlotTimingReader.PathFor(path), slotRows);

        var input = AcceptanceClassDurationResultReader.ReadModelInputs([path])!["Fixture.A"];
        Assert.Equal(new AcceptanceLaneWallModel.Input(10, expectedHeld, expectedSlots), input);
        Assert.Null(AcceptanceClassDurationHistoryStore.RecordAttempt(root, "absent-row",
            [new("absent-row", "L", "Fixture.A", input.Seconds, input.SlotHeldSeconds, input.SlotCount)], 5, true, true));
        Assert.Equal(input, AcceptanceClassDurationHistoryStore.ReadModelInputs(root, 5, out var failure)["Fixture.A"]);
        Assert.Null(failure);
    }

    private void Record(string attempt, double seconds, double? held, int? slots) =>
        Assert.Null(AcceptanceClassDurationHistoryStore.RecordAttempt(root, attempt,
            [new(attempt, "L", "A", seconds, held, slots)], 3, true, true));

    private void Write(string text)
    {
        var path = AcceptanceClassDurationHistoryStore.ResolveStorePath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
