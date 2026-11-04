using System.Text;
using System.Text.Json;
using static BrokerDeliveryFixtureObservation;

// Parallel-safe: all clocks, sinks and observation state are owned by each invocation; no I/O.
public sealed class BrokerDeliveryFixtureObservationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepositoryPreparation_SeparatesInitializationWaitAndCopy(bool initialize)
    {
        var clock = new FakeClock();
        var lines = new List<string>();
        var launches = 0;
        var observation = new BrokerDeliveryFixtureObservation(lines.Add, "repository", clock: clock,
            openGitWindow: () =>
            {
                var before = launches;
                return () => launches - before;
            });
        Execute(observation, () =>
        {
            observation.Measure(Phase.Repository, () =>
            {
                observation.SetRepositoryVariant("dotnet=True;go=False;node=True;python=none");
                observation.Measure(Phase.RepositoryTemplateWait, () => clock.Advance(3));
                if (initialize)
                    observation.Measure(Phase.RepositoryTemplateInitialization, () =>
                    {
                        clock.Advance(4);
                        launches += 4;
                    });
                observation.Measure(Phase.RepositoryCopy, () => clock.Advance(5));
                clock.Advance(2); // Post-copy dirty edit stays inside repository elapsed.
            });
            return 1;
        }, _ => { }, _ => observation.Measure(Phase.Cleanup, () => clock.Advance(7)));

        using var json = ReadSingle(lines);
        var summary = json.RootElement;
        Assert.Equal(initialize ? 21 : 17, summary.GetProperty("elapsed").GetDouble());
        Assert.Equal(0, summary.GetProperty("unaccounted").GetDouble());
        Assert.Equal(initialize ? 14 : 10, Stage(summary, "repository").GetProperty("elapsed").GetDouble());
        Assert.Equal(initialize ? 4 : 0, Stage(summary, "repository").GetProperty("gitLaunches").GetInt32());
        Assert.Equal(6, summary.GetProperty("stages").GetArrayLength());
        // Context lifecycle is independent of whether this case constructed the repository template.
        Assert.Equal("cold", summary.GetProperty("lifecycle").GetString());
        var preparation = summary.GetProperty("repositoryPreparation");
        Assert.Equal("dotnet=True;go=False;node=True;python=none", preparation.GetProperty("variant").GetString());
        var initialization = preparation.GetProperty("initialization");
        Assert.Equal(initialize ? "completed" : "not-entered", initialization.GetProperty("status").GetString());
        if (initialize)
            Assert.Equal(4, initialization.GetProperty("gitLaunches").GetInt32());
        else
            Assert.Equal(JsonValueKind.Null, initialization.GetProperty("gitLaunches").ValueKind);
        var wait = preparation.GetProperty("wait");
        Assert.Equal("observed-wait", wait.GetProperty("kind").GetString());
        Assert.Equal(3, wait.GetProperty("elapsed").GetDouble());
        var copy = preparation.GetProperty("copy");
        Assert.Equal("elapsed", copy.GetProperty("kind").GetString());
        Assert.Equal(5, copy.GetProperty("elapsed").GetDouble());
        Assert.Equal(0, copy.GetProperty("gitLaunches").GetInt32());
        foreach (var stage in new[] { wait, copy })
        {
            Assert.Equal("repository", stage.GetProperty("parent").GetString());
            Assert.False(stage.GetProperty("additive").GetBoolean());
        }
    }

    [Fact]
    public void NestedStagesAndWaits_ExcludeOverlapAndExposeGaps()
    {
        var clock = new FakeClock();
        var lines = new List<string>();
        var observation = new BrokerDeliveryFixtureObservation(lines.Add, "case(row)", clock: clock);
        clock.Advance(2); // Root allocation/adapters are deliberately unaccounted.
        observation.Measure(Phase.FixtureSlotWait, () => clock.Advance(3));
        Execute(observation, () =>
        {
            observation.Measure(Phase.Repository, () =>
            {
                clock.Advance(4);
                observation.Measure(Phase.PrepareTask, () => clock.Advance(6));
            });
            clock.Advance(5);
            return 1;
        }, _ => clock.Advance(7), _ => observation.Measure(Phase.Cleanup, () =>
        {
            clock.Advance(2);
            observation.Measure(Phase.CleanupRetryWait, () => clock.Advance(3));
            observation.Measure(Phase.CleanupRetryWait, () => clock.Advance(4));
        }));
        observation.Complete();

        using var json = ReadSingle(lines);
        var summary = json.RootElement;
        Assert.Equal(36, summary.GetProperty("elapsed").GetDouble());
        Assert.Equal(7, summary.GetProperty("unaccounted").GetDouble());
        Assert.Equal(10, Stage(summary, "repository").GetProperty("elapsed").GetDouble());
        var nested = Stage(summary, "prepare-task");
        Assert.Equal(6, nested.GetProperty("elapsed").GetDouble());
        Assert.Equal("repository", nested.GetProperty("parent").GetString());
        Assert.False(nested.GetProperty("additive").GetBoolean());
        var wait = Stage(summary, "cleanup-retry-wait");
        Assert.Equal(7, wait.GetProperty("elapsed").GetDouble());
        Assert.Equal(2, wait.GetProperty("entries").GetInt32());
        Assert.Equal("cleanup", wait.GetProperty("parent").GetString());
        Assert.False(wait.GetProperty("additive").GetBoolean());
        Assert.Equal(3, summary.GetProperty("fixtureSlotWait").GetProperty("elapsed").GetDouble());
        Assert.False(summary.GetProperty("fixtureSlotWait").GetProperty("additive").GetBoolean());
    }

    [Fact]
    public void MissingStagesAndCounts_AreUnavailableRatherThanZero()
    {
        var clock = new FakeClock();
        var lines = new List<string>();
        var observation = new BrokerDeliveryFixtureObservation(lines.Add, "missing", clock: clock);
        clock.Advance(11);
        observation.Complete();
        using var json = ReadSingle(lines);
        var summary = json.RootElement;
        Assert.Equal(11, summary.GetProperty("unaccounted").GetDouble());
        Assert.Equal("not-entered", summary.GetProperty("setup").GetString());
        Assert.Equal(6, summary.GetProperty("stages").GetArrayLength());
        foreach (var stage in summary.GetProperty("stages").EnumerateArray())
        {
            Assert.Equal(JsonValueKind.Null, stage.GetProperty("elapsed").ValueKind);
            Assert.Equal(JsonValueKind.Null, stage.GetProperty("gitLaunches").ValueKind);
            Assert.Equal("unavailable-no-exact-invocation-scope", stage.GetProperty("gitStatus").GetString());
            Assert.Equal(0, stage.GetProperty("entries").GetInt32());
        }
        Assert.Equal("not-entered", Stage(summary, "repository").GetProperty("status").GetString());
        Assert.Equal("not-observed", summary.GetProperty("fixtureSlotWait").GetProperty("status").GetString());
        Assert.Equal("ms", summary.GetProperty("units").GetString());
        Assert.Equal("launches", summary.GetProperty("gitUnits").GetString());
    }

    [Fact]
    public void RetainedContext_LabelsUsesWithoutSharingInvocationState()
    {
        var coldLines = new List<string>();
        var warmLines = new List<string>();
        var coldClock = new FakeClock();
        var warmClock = new FakeClock();
        var cold = new BrokerDeliveryFixtureObservation(coldLines.Add, "cold(row)", "same-context", 1, coldClock);
        var warm = new BrokerDeliveryFixtureObservation(warmLines.Add, "warm(row)", "same-context", 2, warmClock);
        cold.Measure(Phase.Repository, () =>
        {
            coldClock.Advance(13);
            warm.Measure(Phase.Assertions, () => warmClock.Advance(17));
        });
        warm.Complete();
        cold.Complete();
        using var first = ReadSingle(coldLines);
        using var second = ReadSingle(warmLines);
        Assert.Equal("cold", first.RootElement.GetProperty("lifecycle").GetString());
        Assert.Equal("warm", second.RootElement.GetProperty("lifecycle").GetString());
        Assert.Equal(1, first.RootElement.GetProperty("useOrdinal").GetInt32());
        Assert.Equal(2, second.RootElement.GetProperty("useOrdinal").GetInt32());
        Assert.Equal(first.RootElement.GetProperty("context").GetString(), second.RootElement.GetProperty("context").GetString());
        Assert.NotEqual(first.RootElement.GetProperty("occurrence").GetString(), second.RootElement.GetProperty("occurrence").GetString());
        Assert.Equal("cold(row)", first.RootElement.GetProperty("case").GetString());
        Assert.Equal("warm(row)", second.RootElement.GetProperty("case").GetString());
        Assert.Equal(13, first.RootElement.GetProperty("elapsed").GetDouble());
        Assert.Equal(17, second.RootElement.GetProperty("elapsed").GetDouble());
        Assert.Equal(JsonValueKind.Null, Stage(second.RootElement, "assertions").GetProperty("parent").ValueKind);
    }

    [Fact]
    public void ScopedCensus_ReportsNestedCountsAndRejectsInactiveAttribution()
    {
        var census = new HostCapacitySlotGitCensus(() => "owned-invocation");
        var active = true;
        Func<int?>? Open()
        {
            if (!active) return null;
            var window = census.Open("owned-invocation");
            return () => active ? window?.Close()?.Launches : null;
        }
        var lines = new List<string>();
        var observation = new BrokerDeliveryFixtureObservation(lines.Add, "counts",
            clock: new FakeClock(), openGitWindow: Open);
        observation.Measure(Phase.Repository, () =>
        {
            census.Record("init", "test:fixture");
            observation.Measure(Phase.PrepareTask, () =>
            {
                census.Record("status", "test:fixture");
                census.Record("diff", "test:fixture");
            });
        });
        observation.Measure(Phase.Assertions, () => { });
        observation.Measure(Phase.Cleanup, () => active = false);
        observation.Complete();
        using var json = ReadSingle(lines);
        Assert.Equal(3, Stage(json.RootElement, "repository").GetProperty("gitLaunches").GetInt32());
        Assert.Equal(2, Stage(json.RootElement, "prepare-task").GetProperty("gitLaunches").GetInt32());
        Assert.False(Stage(json.RootElement, "prepare-task").GetProperty("additive").GetBoolean());
        Assert.Equal(0, Stage(json.RootElement, "assertions").GetProperty("gitLaunches").GetInt32());
        Assert.Equal("observed", Stage(json.RootElement, "assertions").GetProperty("gitStatus").GetString());
        Assert.Equal(JsonValueKind.Null, Stage(json.RootElement, "cleanup").GetProperty("gitLaunches").ValueKind);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void BodyAndCleanupFailures_PreserveVerdictAndRecordBoth(bool bodyFails, bool cleanupFails)
    {
        var clock = new FakeClock();
        var lines = new List<string>();
        var observation = new BrokerDeliveryFixtureObservation(lines.Add, "outcome", clock: clock);
        var bodyError = new InvalidOperationException("body sentinel");
        var cleanupError = new IOException("cleanup sentinel");
        var cleaned = 0;
        var entered = 0;
        var error = Record.Exception(() => Execute(observation, () => 42, fixture =>
        {
            Assert.Equal(42, fixture);
            entered++;
            clock.Advance(5);
            if (bodyFails) throw bodyError;
        }, _ => observation.Measure(Phase.Cleanup, () =>
        {
            cleaned++;
            clock.Advance(8);
            if (cleanupFails) throw cleanupError;
        })));
        Assert.Same(cleanupFails ? cleanupError : bodyFails ? bodyError : null, error);
        Assert.Equal(1, entered);
        Assert.Equal(1, cleaned);
        using var json = ReadSingle(lines);
        Assert.Equal("completed", json.RootElement.GetProperty("setup").GetString());
        Assert.Equal(bodyFails ? "failed" : "completed", Stage(json.RootElement, "assertions").GetProperty("status").GetString());
        Assert.Equal(cleanupFails ? "failed" : "completed", Stage(json.RootElement, "cleanup").GetProperty("status").GetString());
        Assert.Equal(5, Stage(json.RootElement, "assertions").GetProperty("elapsed").GetDouble());
        Assert.Equal(8, Stage(json.RootElement, "cleanup").GetProperty("elapsed").GetDouble());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparationFailure_FinalizesAfterOwnedCleanup(bool cleanupFails)
    {
        var lines = new List<string>();
        var clock = new FakeClock();
        var observation = new BrokerDeliveryFixtureObservation(lines.Add, "setup-failure", clock: clock);
        var setupError = new InvalidOperationException("setup sentinel");
        var cleanupError = new IOException("cleanup sentinel");
        var cleaned = 0;
        var error = Record.Exception(() => Execute<int>(observation, () =>
        {
            try
            {
                observation.Measure(Phase.Repository, () => { clock.Advance(3); throw setupError; });
                return 1;
            }
            catch
            {
                observation.Measure(Phase.Cleanup, () =>
                {
                    cleaned++;
                    clock.Advance(4);
                    if (cleanupFails) throw cleanupError;
                });
                throw;
            }
        }, _ => Assert.Fail("Body must not run after setup failure."),
            _ => Assert.Fail("Preparation already owns failure cleanup.")));
        Assert.Same(cleanupFails ? cleanupError : setupError, error);
        Assert.Equal(1, cleaned);
        using var json = ReadSingle(lines);
        Assert.Equal("failed", json.RootElement.GetProperty("setup").GetString());
        Assert.Equal("failed", Stage(json.RootElement, "repository").GetProperty("status").GetString());
        Assert.Equal(3, Stage(json.RootElement, "repository").GetProperty("elapsed").GetDouble());
        Assert.Equal("not-entered", Stage(json.RootElement, "assertions").GetProperty("status").GetString());
        Assert.Equal(cleanupFails ? "failed" : "completed", Stage(json.RootElement, "cleanup").GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("clock", false, false)]
    [InlineData("clock", true, false)]
    [InlineData("clock", true, true)]
    [InlineData("format", false, false)]
    [InlineData("format", true, false)]
    [InlineData("format", true, true)]
    [InlineData("sink", false, false)]
    [InlineData("sink", true, false)]
    [InlineData("sink", true, true)]
    [InlineData("counter-open", false, false)]
    [InlineData("counter-open", true, false)]
    [InlineData("counter-open", true, true)]
    [InlineData("counter-close", false, false)]
    [InlineData("counter-close", true, false)]
    [InlineData("counter-close", true, true)]
    public void DiagnosticFailures_PreserveOriginalVerdict(string failure, bool bodyFails, bool cleanupFails)
    {
        var lines = new List<string>();
        var clock = new FakeClock { Throw = failure == "clock" };
        var attempts = 0;
        var formatted = 0;
        var observation = new BrokerDeliveryFixtureObservation(line =>
        {
            attempts++;
            if (failure == "sink") throw new Exception("sink sentinel");
            lines.Add(line);
        }, "diagnostic", clock: clock, format: value =>
        {
            formatted++;
            if (failure == "format") throw new Exception("format sentinel");
            return JsonSerializer.Serialize(value);
        }, openGitWindow: () =>
        {
            if (failure == "counter-open") throw new Exception("counter open sentinel");
            return () => failure == "counter-close" ? throw new Exception("counter close sentinel") : null;
        });
        var bodyError = new InvalidOperationException("body sentinel");
        var cleanupError = new IOException("cleanup sentinel");
        var entered = 0;
        var cleaned = 0;
        var error = Record.Exception(() => Execute(observation, () => 1, _ =>
        {
            entered++;
            if (bodyFails) throw bodyError;
        }, _ => observation.Measure(Phase.Cleanup, () =>
        {
            cleaned++;
            if (cleanupFails) throw cleanupError;
        })));
        observation.Complete();
        Assert.Same(cleanupFails ? cleanupError : bodyFails ? bodyError : null, error);
        Assert.Equal(1, entered);
        Assert.Equal(1, cleaned);
        Assert.Equal(1, formatted);
        Assert.Equal(failure == "format" ? 0 : 1, attempts);
        if (failure == "clock")
        {
            using var json = ReadSingle(lines);
            Assert.Equal("incomplete", json.RootElement.GetProperty("observation").GetString());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("elapsed").ValueKind);
            Assert.Equal(JsonValueKind.Null, Stage(json.RootElement, "assertions").GetProperty("elapsed").ValueKind);
            Assert.Equal(bodyFails ? "failed" : "completed", Stage(json.RootElement, "assertions").GetProperty("status").GetString());
        }
    }

    [Fact]
    public void LongMetadata_EmitsOneBoundedValidJsonRecord()
    {
        var lines = new List<string>();
        var observation = new BrokerDeliveryFixtureObservation(lines.Add, new string('<', 20_000),
            new string('"', 20_000), clock: new FakeClock());
        foreach (var phase in Enum.GetValues<Phase>()) observation.Measure(phase, () => { });
        observation.Complete();
        observation.Complete();
        using var json = ReadSingle(lines);
        Assert.Equal(160, json.RootElement.GetProperty("case").GetString()!.Length);
        Assert.Equal(48, json.RootElement.GetProperty("context").GetString()!.Length);
        Assert.InRange(Encoding.UTF8.GetByteCount(Assert.Single(lines)), 1, MaximumSummaryBytes);
    }

    private static JsonDocument ReadSingle(List<string> lines)
    {
        var line = Assert.Single(lines);
        Assert.StartsWith(Prefix, line, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', line);
        Assert.InRange(Encoding.UTF8.GetByteCount(line), 1, MaximumSummaryBytes);
        return JsonDocument.Parse(line[Prefix.Length..]);
    }

    private static JsonElement Stage(JsonElement summary, string name) =>
        Assert.Single(summary.GetProperty("stages").EnumerateArray(), stage => stage.GetProperty("stage").GetString() == name);

    private sealed class FakeClock : TimeProvider
    {
        private long timestamp;
        internal bool Throw;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Throw ? throw new Exception("clock sentinel") : timestamp;
        internal void Advance(long milliseconds) => timestamp += milliseconds;
    }
}
