using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.App.Orchestration.PreTesterReverseDependencyObservation;

public sealed class PreTesterResolverObservationTests(ITestOutputHelper output)
{
    internal static readonly DateTimeOffset Start = new(2026, 11, 23, 0, 0, 0, TimeSpan.Zero);
    internal static readonly Boundary BeforeTester = new("goal", "tester", "source-under-test");

    [Fact]
    public void PolicyRoundTripsOneWindowAndOnlySupervisedDispatchingLoopsHaveAnObserver()
    {
        using var fixture = new PreTesterReverseDependencySelectionFixture();
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Root);
        var policy = ConductorAutonomyPolicy.Conservative;
        var id = Guid.NewGuid();
        var json = (policy with { PreTesterResolverObservationWindowId = id }).ToJson();
        Assert.Equal(id, ConductorAutonomyPolicy.ParseJson(json).PreTesterResolverObservationWindowId);
        Assert.Null(ConductorAutonomyPolicy.ParseJson(policy.ToJson()).PreTesterResolverObservationWindowId);
        Assert.Null(ConductorAutonomyPolicy.ParseJson(json.Replace($"\"{id:D}\"", "null"))
            .PreTesterResolverObservationWindowId);
        Assert.Throws<FormatException>(() => ConductorAutonomyPolicy.ParseJson(
            json.Replace(id.ToString("D"), "../invalid")));
        Assert.Null(PreTesterResolverObservationWindow.ForConductLoop(false, false, workspace));
        Assert.Null(PreTesterResolverObservationWindow.ForConductLoop(true, true, workspace));
        using var supervised = PreTesterResolverObservationWindow.ForConductLoop(true, false, workspace);
        Assert.NotNull(supervised);
        Assert.Null(supervised!.BeginTick(null, () => throw new InvalidOperationException("Disabled policy read the clock.")));
        Assert.False(Directory.Exists(Path.Combine(workspace.OrchestratorDirectory, "pretester-resolver-observation")));
    }

    [Fact]
    public void RecordsOrderedResolverInputsAndLeavesSelectionDiagnosticsAndCostUnchanged()
    {
        using var fixture = new PreTesterReverseDependencySelectionFixture();
        fixture.Test("SecondTests", "AppContract");
        var id = Guid.NewGuid();
        var now = Start;
        using var window = Window(fixture.Root);
        using var tick = Assert.IsType<Tick>(window.BeginTick(id, () => now));
        var first = new[] { "SecondTests", "ConsumerTests", "SecondTests" };
        var rejected = new[] { "ConsumerTests", "SecondTests" };
        var unresolved = new[] { "UnknownTests", "SecondTests" };
        foreach (var (names, run) in new[] { (first, true), (first, true), (rejected, false), (unresolved, true) })
        {
            var disabled = Select(null);
            var enabled = Select(tick);
            Assert.Equal(disabled.Selections, enabled.Selections);
            Assert.Equal(disabled.Provenance, enabled.Provenance);
            Assert.Equal(disabled.Lines, enabled.Lines);

            (FindingEvidenceSelection[] Selections, PreTesterChangedFileGuardClasses.Provenance[] Provenance,
                string[] Lines) Select(Tick? recorder)
            {
                var lines = new List<string>();
                var (selections, provenance) = PreTesterReverseDependencySelections.Select(fixture.Root,
                    [PreTesterReverseDependencySelectionFixture.Source], lines.Add,
                    (_, _) => new(true, names), _ => new(run, "fixture-cost"), recorder, BeforeTester);
                return (selections.ToArray(), provenance.ToArray(), lines.ToArray());
            }
        }

        tick.Complete();
        now += Duration;
        Assert.Null(window.BeginTick(id, () => now));
        var path = CorpusPath(fixture.Root, id);
        var seal = Receipt(path);
        var corpus = PreTesterResolverCorpus.Read(path, seal.Sha256!);
        Assert.Equal("complete", seal.Status);
        Assert.Equal([1, 2, 3, 4], corpus.Cases.Select(row => row.Invocation.Ordinal));
        Assert.Equal(first, corpus.Cases[0].Invocation.Names);
        Assert.Equal(first, corpus.Cases[1].Invocation.Names);
        Assert.Equal(2, corpus.Manifest.Workloads[0].Count);
        Assert.Equal(BeforeTester, corpus.Cases[0].Invocation.Boundary);
        Assert.Equal(false, corpus.Cases[2].Invocation.Cost?.Run);
        Assert.Equal("UnknownTests", corpus.Cases[3].Invocation.UnresolvedName);
        Assert.Contains("SecondTests", corpus.Cases[0].Invocation.Inventory);
        Assert.Equal(Start, corpus.Header.Started);
        Assert.Equal(Start + Duration, corpus.Header.Deadline);
    }

    [Fact]
    public void OnlyCompleteInBoundsTicksArePublishedInEntryOrder()
    {
        using var fixture = new PreTesterReverseDependencySelectionFixture();
        var id = Guid.NewGuid();
        var now = Start;
        using var window = Window(fixture.Root);
        using (var tick = Assert.IsType<Tick>(window.BeginTick(id, () => now)))
        {
            var first = tick.Enter()!.Value;
            var second = tick.Enter()!.Value;
            Record(tick, second, ["B"]);
            Record(tick, first, ["A"]);
            tick.Complete();
        }
        now = Start + Duration - TimeSpan.FromSeconds(1);
        using (var crossing = Assert.IsType<Tick>(window.BeginTick(id, () => now)))
        {
            Record(crossing, crossing.Enter()!.Value, ["B", "A"]);
            now += TimeSpan.FromSeconds(2);
            crossing.Complete();
        }

        var path = CorpusPath(fixture.Root, id);
        var seal = Receipt(path);
        Assert.Equal("insufficient-coverage", seal.Status);
        var corpus = PreTesterResolverCorpus.Read(path, seal.Sha256!);
        Assert.Equal(1, corpus.Manifest.CompleteTickCount);
        Assert.Equal(["A", "B"], corpus.Cases.Select(row => Assert.Single(row.Invocation.Names)));
        Assert.Null(window.BeginTick(id, () => now));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingEntryOrInterruptedTickCannotSealAndRenewalCannotReplaceIt(bool missing)
    {
        using var fixture = new PreTesterReverseDependencySelectionFixture();
        var id = Guid.NewGuid();
        var path = CorpusPath(fixture.Root, id);
        using (var window = Window(fixture.Root))
        using (var tick = Assert.IsType<Tick>(window.BeginTick(id, () => Start)))
        {
            Assert.Equal(1, tick.Enter());
            if (missing) tick.Complete();
        }
        var seal = Receipt(path);
        Assert.Equal("incomplete", seal.Status);
        Assert.Null(seal.Sha256);
        using var successor = Window(fixture.Root);
        Assert.Null(successor.BeginTick(id, () => Start.AddHours(1)));
        Assert.Equal(seal, Receipt(path));
        Assert.Throws<InvalidDataException>(() => PreTesterResolverCorpus.Read(path, Sha256(path)));
    }

    [Fact]
    public void FailedRecorderOpenLeavesResolverDecisionAvailable()
    {
        using var fixture = new PreTesterReverseDependencySelectionFixture();
        var blocked = Path.Combine(fixture.Root, "blocked");
        File.WriteAllText(blocked, "");
        var progress = new List<string>();
        using var window = Window(blocked, progress.Add);
        Assert.Null(window.BeginTick(Guid.NewGuid(), () => Start));
        Assert.Contains("status=start-failed", Assert.Single(progress), StringComparison.Ordinal);
        var result = PreTesterReverseDependencySelections.Select(fixture.Root,
            [PreTesterReverseDependencySelectionFixture.Source], selector: (_, _) => new(true, ["ConsumerTests"]),
            costDecision: _ => new(true, "fixture-cost"), resolverTick: window.ActiveTick, resolverBoundary: BeforeTester);
        Assert.Equal("ConsumerTests", Assert.Single(result.Selections).TestClass);
    }

    [Fact]
    public void MatchedOriginalResolverRunsReportCpuWithInventoryAndPersistence()
    {
        using var fixture = new PreTesterReverseDependencySelectionFixture();
        fixture.Test("SecondTests", "AppContract");
        var orderedWorkloads = new[]
        {
            new[] { "ConsumerTests", "SecondTests" },
            new[] { "SecondTests", "ConsumerTests", "SecondTests" }
        };
        const int repetitions = 16;
        Measure(enabled: false);
        Measure(enabled: true);
        for (var pair = 0; pair < 4; pair++)
        {
            var first = Measure(enabled: pair % 2 != 0);
            var second = Measure(enabled: pair % 2 == 0);
            var (off, on) = pair % 2 == 0 ? (first, second) : (second, first);
            output.WriteLine($"pair={pair + 1} invocations={orderedWorkloads.Length * repetitions} " +
                $"disabled_cpu_ms={off.TotalMilliseconds} enabled_cpu_ms={on.TotalMilliseconds} " +
                $"observation_delta_ms={(on - off).TotalMilliseconds}");
        }

        TimeSpan Measure(bool enabled)
        {
            var id = Guid.NewGuid();
            var now = Start;
            using var window = Window(fixture.Root);
            var started = Environment.CpuUsage.TotalTime;
            using var tick = window.BeginTick(enabled ? id : null, () => now);
            for (var repeat = 0; repeat < repetitions; repeat++)
            {
                foreach (var names in orderedWorkloads)
                {
                    var result = PreTesterReverseDependencySelections.Select(fixture.Root,
                        [PreTesterReverseDependencySelectionFixture.Source], selector: (_, _) => new(true, names),
                        costDecision: _ => new(true, "fixture-cost"), resolverTick: tick, resolverBoundary: BeforeTester);
                    Assert.Equal(names.Distinct(), result.Selections.Select(selection => selection.TestClass));
                }
            }
            tick?.Complete();
            now += Duration;
            window.BeginTick(enabled ? id : null, () => now);
            var elapsed = Environment.CpuUsage.TotalTime - started;
            var path = CorpusPath(fixture.Root, id);
            if (enabled)
            {
                var seal = Receipt(path);
                Assert.Equal("complete", seal.Status);
                Assert.Equal(orderedWorkloads.Length * repetitions,
                    PreTesterResolverCorpus.Read(path, seal.Sha256!).Cases.Length);
            }
            else
            {
                Assert.False(File.Exists(path));
            }
            return elapsed;
        }
    }

    [Fact]
    public void PlannedRenewalContinuesTheSameDeadlineWithSessionQualifiedOrdinals()
    {
        using var fixture = new PreTesterReverseDependencySelectionFixture();
        var id = Guid.NewGuid();
        var now = Start;
        var path = CorpusPath(fixture.Root, id);
        string? handoff;
        using (var first = Window(fixture.Root))
        {
            using var tick = Assert.IsType<Tick>(first.BeginTick(id, () => now));
            Record(tick, tick.Enter()!.Value, ["A", "B"]);
            tick.Complete();
            now += TimeSpan.FromHours(12);
            handoff = first.CloseForRenewal(() => now);
            Assert.NotNull(handoff);
        }

        using (var unbound = Window(fixture.Root))
            Assert.Null(unbound.BeginTick(id, () => now));
        using (var wrongDigest = Window(fixture.Root, handoff: handoff![..^64] + new string('0', 64)))
            Assert.Null(wrongDigest.BeginTick(id, () => now));
        Assert.False(File.Exists(path + ".receipt.json"));

        using var successor = Window(fixture.Root, handoff: handoff);
        now += TimeSpan.FromMinutes(1);
        using (var tick = Assert.IsType<Tick>(successor.BeginTick(id, () => now)))
        {
            Record(tick, tick.Enter()!.Value, ["B", "A"]);
            tick.Complete();
        }
        now = Start + Duration;
        Assert.Null(successor.BeginTick(id, () => now));
        var seal = Receipt(path);
        var corpus = PreTesterResolverCorpus.Read(path, seal.Sha256!);
        Assert.Equal("complete", seal.Status);
        Assert.Equal(now, corpus.Header.Deadline);
        Assert.Equal(2, corpus.Manifest.CompleteTickCount);
        Assert.Equal(2, corpus.Producers.Length);
        Assert.Equal(corpus.Producers.Select(producer => producer.SessionId),
            corpus.Cases.Select(row => row.SessionId));
        Assert.Equal([1, 1], corpus.Cases.Select(row => row.Invocation.Ordinal));
        using var repeated = Window(fixture.Root, handoff: handoff);
        Assert.Null(repeated.BeginTick(id, () => now));
        Assert.Equal(seal, Receipt(path));
    }

    internal static PreTesterResolverObservationWindow Window(string root, Action<string>? report = null,
        string? handoff = null) =>
        new(root, session => new(session, new string('1', 40), new string('2', 64), 1, Start, root),
            report ?? (_ => { }), handoff);

    internal static void Record(Tick tick, int ordinal, string[] names) =>
        tick.Record(ordinal, BeforeTester, "project.csproj", names, [new("A", null), new("B", null), new("A", null)],
            ["src/Shipping.cs"], names[0], null);

    internal static string CorpusPath(string root, Guid id) => Path.Combine(root, $"{id:D}.jsonl");

    internal static Seal Receipt(string path) =>
        JsonSerializer.Deserialize<Seal>(File.ReadAllText(path + ".receipt.json"))!;
}
