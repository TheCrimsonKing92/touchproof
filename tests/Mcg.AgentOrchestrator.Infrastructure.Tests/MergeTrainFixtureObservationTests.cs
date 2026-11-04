using System.Text;
using System.Text.Json;

// Parallel-safe: fake clocks, initialization entries and sinks are owned by each case.
public sealed class MergeTrainFixtureObservationTests
{
    [Fact]
    public void NestedStages_ExcludeOverlapAndExposeUnaccountedElapsed()
    {
        var clock = new FakeClock();
        var lines = new List<string>();
        var count = 0;
        var observer = new MergeTrainFixtureObservation("mode", lines.Add, clock, () => count);
        MergeTrainFixtureObservation.Occurrence(observer, () =>
        {
            clock.Tick(2);
            MergeTrainFixtureObservation.Run(observer, "copy", () =>
            {
                clock.Tick(3);
                MergeTrainFixtureObservation.Run(observer, "files", () => { clock.Tick(5); count += 2; });
                clock.Tick(7);
            });
            clock.Tick(11);
        });
        using var result = Read(lines);
        var root = result.RootElement;
        Assert.Equal(28, root.GetProperty("elapsedMs").GetDouble());
        Assert.Equal(15, root.GetProperty("accountedMs").GetDouble());
        Assert.Equal(13, root.GetProperty("unaccountedMs").GetDouble());
        Assert.Equal(15, Stage(root, "copy").GetProperty("ElapsedMs").GetDouble());
        Assert.Equal(5, Stage(root, "files").GetProperty("ElapsedMs").GetDouble());
        Assert.Equal("copy", Stage(root, "files").GetProperty("Parent").GetString());
        Assert.False(Stage(root, "files").GetProperty("Additive").GetBoolean());
        Assert.Equal(2, Stage(root, "files").GetProperty("ProbeLaunches").GetInt32());
        Assert.Equal("not-reached", Stage(root, "kernel").GetProperty("Measurement").GetString());
        Assert.Equal(JsonValueKind.Null, Stage(root, "kernel").GetProperty("ElapsedMs").ValueKind);
        Assert.Equal("elapsed-ms", root.GetProperty("units").GetString());
    }

    [Fact]
    public void Initialization_AttributesColdOnlyToBuilderAndWarmToReuse()
    {
        var clock = new FakeClock();
        var builds = 0;
        var entry = new MergeTrainFixtureObservation.Initialization<object>(_ =>
        {
            builds++;
            clock.Tick(17);
            return new object();
        });
        var coldLines = new List<string>();
        var warmLines = new List<string>();
        var cold = new MergeTrainFixtureObservation("same", coldLines.Add, clock);
        var warm = new MergeTrainFixtureObservation("same", warmLines.Add, clock);
        object? first = null;
        MergeTrainFixtureObservation.Occurrence(cold, () => first =
            MergeTrainFixtureObservation.Run(cold, "template-access", () => entry.Get(cold)));
        MergeTrainFixtureObservation.Occurrence(warm, () => Assert.Same(first,
            MergeTrainFixtureObservation.Run(warm, "template-access", () => entry.Get(warm))));
        using var coldResult = Read(coldLines);
        using var warmResult = Read(warmLines);
        Assert.Equal(1, builds);
        Assert.Equal("cold", coldResult.RootElement.GetProperty("template").GetString());
        Assert.Equal("warm", warmResult.RootElement.GetProperty("template").GetString());
        Assert.Equal(17, Stage(coldResult.RootElement, "cold-create").GetProperty("ElapsedMs").GetDouble());
        Assert.Equal("template-access", Stage(coldResult.RootElement, "cold-create").GetProperty("Parent").GetString());
        Assert.False(Stage(coldResult.RootElement, "cold-create").GetProperty("Additive").GetBoolean());
        Assert.Equal("not-reached", Stage(warmResult.RootElement, "cold-create").GetProperty("Measurement").GetString());
    }

    [Fact]
    public void Initialization_CachesFailureWithoutReplayingColdMeasurement()
    {
        var original = new InvalidOperationException("builder");
        var builds = 0;
        var entry = new MergeTrainFixtureObservation.Initialization<object>(_ => { builds++; throw original; });
        var coldLines = new List<string>();
        var warmLines = new List<string>();
        var cold = new MergeTrainFixtureObservation("mode", coldLines.Add, new FakeClock());
        var warm = new MergeTrainFixtureObservation("mode", warmLines.Add, new FakeClock());
        Assert.Same(original, Record.Exception(() => MergeTrainFixtureObservation.Occurrence(cold, () => entry.Get(cold))));
        Assert.Same(original, Record.Exception(() => MergeTrainFixtureObservation.Occurrence(warm, () => entry.Get(warm))));
        using var first = Read(coldLines);
        using var second = Read(warmLines);
        Assert.Equal(1, builds);
        Assert.Equal("failed", Stage(first.RootElement, "cold-create").GetProperty("Status").GetString());
        Assert.Equal("cached-failure", second.RootElement.GetProperty("template").GetString());
        Assert.Equal("not-reached", Stage(second.RootElement, "cold-create").GetProperty("Measurement").GetString());
        Assert.Equal("not-reached", second.RootElement.GetProperty("cleanup").GetString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Lifecycle_PreservesExceptionIdentityAndFinallyPrecedence(bool failBody, bool failCleanup)
    {
        var bodyError = new InvalidOperationException("body");
        var cleanupError = new InvalidOperationException("cleanup");
        var lines = new List<string>();
        var observer = new MergeTrainFixtureObservation("mode", lines.Add, new FakeClock());
        var cleaned = false;
        var error = Record.Exception(() => MergeTrainFixtureObservation.Occurrence(observer, () =>
            MergeTrainFixtureObservation.Lifecycle(observer,
                () => MergeTrainFixtureObservation.Run(observer, "body", () => { if (failBody) throw bodyError; }),
                () => { cleaned = true; if (failCleanup) throw cleanupError; })));
        Assert.True(cleaned);
        Assert.Same(failCleanup ? cleanupError : failBody ? bodyError : null, error);
        using var result = Read(lines);
        Assert.Equal(failBody ? "failed" : "passed", Stage(result.RootElement, "body").GetProperty("Status").GetString());
        Assert.Equal(failCleanup ? "failed" : "passed", result.RootElement.GetProperty("cleanup").GetString());
        Assert.Equal(failBody || failCleanup ? "failed" : "passed", result.RootElement.GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void BrokenDiagnostics_CannotChangeVerdictOrSkipCleanup(bool failBody, bool failCleanup)
    {
        var bodyError = new InvalidOperationException("body");
        var cleanupError = new InvalidOperationException("cleanup");
        var cleaned = false;
        var emissions = 0;
        var observer = new MergeTrainFixtureObservation("mode", _ => { emissions++; throw new Exception("sink"); },
            new BrokenClock(), () => throw new Exception("counter"));
        var error = Record.Exception(() => MergeTrainFixtureObservation.Occurrence(observer, () =>
            MergeTrainFixtureObservation.Lifecycle(observer,
                () => MergeTrainFixtureObservation.Run(observer, "body", () => { if (failBody) throw bodyError; }),
                () => { cleaned = true; if (failCleanup) throw cleanupError; })));
        Assert.True(cleaned);
        Assert.Same(failCleanup ? cleanupError : failBody ? bodyError : null, error);
        Assert.Equal(1, emissions);
    }

    [Fact]
    public void BrokenClock_ReportsUnavailableSeparatelyFromAbsentStages()
    {
        var lines = new List<string>();
        var observer = new MergeTrainFixtureObservation("mode", lines.Add, new BrokenClock(),
            () => throw new Exception("counter"));
        MergeTrainFixtureObservation.Occurrence(observer, () =>
            MergeTrainFixtureObservation.Run(observer, "body", () => { }));
        using var result = Read(lines);
        Assert.True(result.RootElement.GetProperty("diagnosticsFailed").GetBoolean());
        Assert.Equal("unavailable", Stage(result.RootElement, "body").GetProperty("Measurement").GetString());
        Assert.Equal("not-reached", Stage(result.RootElement, "cleanup").GetProperty("Measurement").GetString());
        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("unaccountedMs").ValueKind);
        Assert.Equal(JsonValueKind.Null, Stage(result.RootElement, "body").GetProperty("ProbeLaunches").ValueKind);
    }

    [Fact]
    public void NestedOccurrences_ShareSamplerAndDeclareOverlapWithinBoundedOutput()
    {
        var lines = new List<string>();
        var clock = new FakeClock();
        var count = 0;
        var outer = new MergeTrainFixtureObservation(new string('\u2603', 10000), lines.Add, clock, () => count);
        var inner = new MergeTrainFixtureObservation("mode", lines.Add, clock, () => count, outer.Id);
        MergeTrainFixtureObservation.Occurrence(outer, () => MergeTrainFixtureObservation.Run(outer, "body", () =>
            MergeTrainFixtureObservation.Occurrence(inner, () => MergeTrainFixtureObservation.Run(inner, "body", () =>
            {
                clock.Tick(7);
                count++;
            }))));
        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => Assert.InRange(Encoding.UTF8.GetByteCount(line), 1, 8192));
        using var child = JsonDocument.Parse(lines[0]);
        using var parent = JsonDocument.Parse(lines[1]);
        Assert.Equal(outer.Id, child.RootElement.GetProperty("parentOccurrence").GetString());
        Assert.True(child.RootElement.GetProperty("nestedOccurrence").GetBoolean());
        Assert.False(parent.RootElement.GetProperty("nestedOccurrence").GetBoolean());
        Assert.Equal(7, parent.RootElement.GetProperty("accountedMs").GetDouble());
        Assert.Equal(1, Stage(parent.RootElement, "body").GetProperty("ProbeLaunches").GetInt32());
        Assert.Equal(1, Stage(child.RootElement, "body").GetProperty("ProbeLaunches").GetInt32());
    }

    [Fact]
    public void OptionalObserver_LeavesLifecycleAndInitializationUsable()
    {
        var initialized = new MergeTrainFixtureObservation.Initialization<int>(_ => 42);
        var cleaned = false;
        MergeTrainFixtureObservation.Occurrence(null, () => MergeTrainFixtureObservation.Lifecycle(null,
            () => Assert.Equal(42, initialized.Get(null)), () => cleaned = true));
        Assert.True(cleaned);
    }

    private static JsonDocument Read(List<string> lines) => JsonDocument.Parse(Assert.Single(lines));
    private static JsonElement Stage(JsonElement root, string name) =>
        Assert.Single(root.GetProperty("stages").EnumerateArray(), stage => stage.GetProperty("Name").GetString() == name);

    private sealed class FakeClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => ticks;
        internal void Tick(long value) => ticks += value;
    }

    private sealed class BrokenClock : TimeProvider
    {
        public override long GetTimestamp() => throw new InvalidOperationException("clock");
    }
}
