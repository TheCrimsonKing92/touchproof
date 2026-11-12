using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every store and source tree belongs to a unique fixture directory.
public sealed class ReverseDependencyEvidenceCostInputsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-12T12:00:00Z");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Compute_MediansAndWindowChange_RecomputesDecision()
    {
        var medians = new Dictionary<string, double> { ["Selected"] = 10, ["Other"] = 10 };
        var attempts = new[] { Attempt("red", Now.AddDays(-14)), Attempt("green", Now, passed: true) };
        ReverseDependencyEvidenceCostInputs Read(DateTimeOffset now) => ReverseDependencyEvidenceCostInputs.Compute(
            ["Selected", "Selected"], ["Selected", "Other"], medians, 3, attempts, now);
        var first = Read(Now);
        Assert.Equal(10, first.SelectionSeconds);
        Assert.Equal(20, first.FullGateSeconds);
        Assert.Equal(0.5, first.FailureRate);
        Assert.True(Decide(first));
        medians["Selected"] = 100;
        var second = Read(Now);
        Assert.Equal(100, second.SelectionSeconds);
        Assert.Equal(110, second.FullGateSeconds);
        Assert.False(Decide(second));
        medians["Selected"] = 10;
        var third = Read(Now.AddTicks(1));
        Assert.Equal(0, third.FailureRate);
        Assert.False(Decide(third));
    }

    [Fact]
    public void Compute_UnknownClasses_UsesPackerMedianThenConfiguredDefault()
    {
        var input = ReverseDependencyEvidenceCostInputs.Compute(["Unknown"], ["Known", "Unknown", "Unknown"],
            new Dictionary<string, double> { ["Known"] = 4, ["Second"] = 8 }, 3, [], Now);
        Assert.Equal(6, input.SelectionSeconds);
        Assert.Equal(10, input.FullGateSeconds);
        Assert.Null(input.FailureRate);
        var empty = ReverseDependencyEvidenceCostInputs.Compute(["Unknown"], ["A", "B"],
            new Dictionary<string, double>(), 3, [], Now);
        Assert.Equal(3, empty.SelectionSeconds);
        Assert.Equal(6, empty.FullGateSeconds);
    }

    [Fact]
    public void Compute_MixedAttempts_CountsOnlyConfirmedCandidateFailures()
    {
        var red = Attempt("red", Now);
        var mixed = red with { Metadata = red.Metadata with { AttemptId = "mixed" }, Result = red.Result! with
        {
            Acceptance = new(false, [Failure(AcceptanceTestFailureOrigin.Inherited), Failure(AcceptanceTestFailureOrigin.Introduced)])
        } };
        var attempts = new[]
        {
            red, red, mixed, Attempt("green", Now, passed: true),
            Attempt("flake", Now, origin: AcceptanceTestFailureOrigin.UnconfirmedIntroduced),
            Attempt("baseline", Now, origin: AcceptanceTestFailureOrigin.Inherited),
            Attempt("unknown", Now, origin: AcceptanceTestFailureOrigin.Unattributed),
            Attempt("environment", Now, environmental: true),
            red with { Metadata = red.Metadata with { AttemptId = "fault" }, Result = red.Result! with { FaultKind = "io" } },
            red with { Metadata = red.Metadata with { AttemptId = "infra" }, Result = red.Result! with { InfrastructureReasonCode = "host" } },
            red with { Metadata = red.Metadata with { AttemptId = "missing" }, Result = null },
            Attempt("evidence", Now, kind: "focused-evidence"), Attempt("old", Now.AddDays(-14).AddTicks(-1)),
            Attempt("future", Now.AddTicks(1)), red with { Metadata = red.Metadata with { AttemptId = "running", CompletedAt = null } }
        };
        var input = ReverseDependencyEvidenceCostInputs.Compute(["A"], ["A"],
            new Dictionary<string, double>(), 3, attempts, Now);
        Assert.Equal(0.2, input.FailureRate);
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 0)]
    public void Compute_LegacyCheckAttribution_RespectsRecordedCauseAndFlake(
        bool environmental, bool flake, double expected)
    {
        var attempt = Attempt("legacy", Now);
        var result = attempt.Result! with { Acceptance = new(false,
            [new("tests", false, 1, null, FailingTestAttributions: flake
                ? [new("Selected.Works", AcceptanceTestFailureOrigin.UnconfirmedIntroduced, "rerun passed")] : null)],
            CheckAttributions: [new("tests", AcceptanceFailureOrigin.Introduced, "baseline green",
                environmental ? AcceptanceFailureCause.EnvironmentalApparatus : AcceptanceFailureCause.NotClassified)]) };
        var input = ReverseDependencyEvidenceCostInputs.Compute(["A"], ["A"],
            new Dictionary<string, double>(), 3, [attempt with { Result = result }], Now);
        Assert.Equal(expected, input.FailureRate);
    }

    [Fact]
    public void Read_StoreChanges_RefreshesAllInputsAndExcludesOtherRepository()
    {
        using var fixture = new StoreFixture();
        fixture.WriteAttempt(Attempt("red", Now.AddDays(-14)));
        fixture.WriteAttempt(Attempt("green", Now, passed: true));
        fixture.WriteAttempt(Attempt("foreign", Now) with { Metadata = Attempt("foreign", Now).Metadata with
        { ExecutionDirectory = Path.Combine(fixture.Root, "other-repository") } });
        fixture.Record("first", "Selected", 10);
        fixture.Record("first", "Other", 10);
        var first = fixture.Read(Now);
        Assert.Null(first.UnavailableReason);
        Assert.Equal(10, first.SelectionSeconds);
        Assert.Equal(30, first.FullGateSeconds); // Includes the class in the second declared project.
        Assert.Equal(0.5, first.FailureRate);
        Assert.True(Decide(first));
        fixture.Record("second", "Selected", 100);
        var second = fixture.Read(Now);
        Assert.Equal(100, second.SelectionSeconds);
        Assert.Equal(165, second.FullGateSeconds);
        Assert.False(Decide(second));
        var third = fixture.Read(Now.AddTicks(1));
        Assert.Equal(0, third.FailureRate);
        Assert.False(Decide(third));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    public void Read_MissingHistoryAndUnreadableRecord_UsesFallbackOrSkips(string receipt)
    {
        using var fixture = new StoreFixture();
        var first = fixture.Read(Now);
        Assert.Null(first.UnavailableReason);
        Assert.Equal(3, first.SelectionSeconds);
        Assert.Equal(9, first.FullGateSeconds);
        Assert.Null(first.FailureRate);
        fixture.Write("state/acceptance-gate-attempts/broken.attempt.json", receipt);
        var broken = fixture.Read(Now);
        Assert.NotNull(broken.UnavailableReason);
        Assert.False(Decide(broken));
    }

    [Fact]
    public void Read_ManifestAndClassChanges_RefreshesGateClassesAndFallback()
    {
        using var fixture = new StoreFixture();
        Assert.Equal(9, fixture.Read(Now).FullGateSeconds);
        fixture.Write("config/acceptance-manifest.json", """
            {"engine":{"balancedLanePool":{"defaultClassSecondsWhenNoHistory":7}},
             "checks":[{"name":"one","type":"dotnet-test","project":"tests/One.Tests/One.Tests.csproj",
                        "arguments":["--filter","FullyQualifiedName=Selected"]}]}
            """);
        var input = fixture.Read(Now);
        Assert.Null(input.UnavailableReason);
        Assert.Equal(7, input.SelectionSeconds);
        Assert.Equal(7, input.FullGateSeconds);
        fixture.Write("tests/One.Tests/Additional.cs", "public class Additional { [Fact] public void Works() {} }");
        Assert.Equal(7, fixture.Read(Now).FullGateSeconds);
    }

    private static bool Decide(ReverseDependencyEvidenceCostInputs input) =>
        ReverseDependencyEvidenceCostRule.Evaluate(input.SelectionSeconds, input.FullGateSeconds, input.FailureRate).Run;

    private static AcceptanceCheckResult Failure(AcceptanceTestFailureOrigin origin, bool environmental = false) =>
        new("tests", false, 1, null, FailingTestAttributions: [new("Selected.Works", origin, "receipt")],
            FailureCauseEvidence: environmental ? new(AcceptanceFailureCause.EnvironmentalApparatus, "host") : null);

    private static ReverseDependencyEvidenceCostInputs.Attempt Attempt(string id, DateTimeOffset completed,
        bool passed = false, AcceptanceTestFailureOrigin origin = AcceptanceTestFailureOrigin.Introduced,
        bool environmental = false, string kind = "gate") => new(
        new(id, "goal", "goal", 0, null, null, completed.AddMinutes(-1), completed, 0, ConductorParallelAcceptanceAttemptOutcome.Failed,
            "", "", "", "", "", "", CompletedAt: completed, Kind: kind),
        new("accepted", null, null, null, null, new(passed, passed ? [] : [Failure(origin, environmental)]),
            null, null, null, null));

    private sealed class StoreFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "reverse-cost-" + Guid.NewGuid().ToString("N"));
        internal StoreFixture()
        {
            Write("config/acceptance-manifest.json", """
                {"engine":{"balancedLanePool":{"historyWindow":1,"defaultClassSecondsWhenNoHistory":3}},
                 "checks":[{"name":"one","type":"dotnet-test","project":"tests/One.Tests/One.Tests.csproj"},
                           {"name":"two","type":"dotnet-test","project":"tests/Two.Tests/Two.Tests.csproj"}]}
                """);
            Write("tests/One.Tests/One.Tests.csproj", "<Project />");
            Write("tests/Two.Tests/Two.Tests.csproj", "<Project />");
            Write("tests/One.Tests/Selected.cs", "public class Selected { [Fact] public void Works() {} }");
            Write("tests/One.Tests/Other.cs", "public class Other { [Fact] public void Works() {} }");
            Write("tests/Two.Tests/Unknown.cs", "public class Unknown { [Fact] public void Works() {} }");
        }
        internal void Write(string path, string text)
        {
            var target = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, text);
        }
        internal void WriteAttempt(ReverseDependencyEvidenceCostInputs.Attempt attempt)
        {
            Write($"state/acceptance-gate-attempts/{attempt.Metadata.AttemptId}.attempt.json", JsonSerializer.Serialize(attempt.Metadata, Json));
            Write($"state/acceptance-gate-attempts/{attempt.Metadata.AttemptId}.result.json", JsonSerializer.Serialize(attempt.Result, Json));
        }
        internal void Record(string id, string name, double seconds) => Assert.Null(
            AcceptanceClassDurationHistoryStore.RecordAttempt(Root, id, [new(id, "lane", name, seconds)], 1, true, true));
        internal ReverseDependencyEvidenceCostInputs Read(DateTimeOffset now) => ReverseDependencyEvidenceCostHistory.Read(
            Root, Path.Combine(Root, "state"), ["Selected"], ["src/Widget.cs"], Root, () => now);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
