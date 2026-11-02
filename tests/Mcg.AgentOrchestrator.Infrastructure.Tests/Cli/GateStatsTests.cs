using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel safe: private fixture roots and AsyncLocal console capture; no shared stores.
public sealed class GateStatsTests : CliTaskQueryTestSupport
{
    [Fact]
    public void Lanes_KnownSamples_ReportMedianP90MaxAndLast()
    {
        using var fixture = new Fixture();
        for (var day = 5; day >= 1; day--) fixture.Duration("alpha", day * 10, day);
        for (var day = 1; day <= 4; day++) fixture.Duration("beta", day * 10, day);
        var report = fixture.Report("lanes");
        var rows = report.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal("alpha", rows[0].GetProperty("lane").GetString());
        Numbers(rows[0], ("gates", 5), ("median_seconds", 30), ("p90_seconds", 50), ("max_seconds", 50));
        Numbers(rows[1], ("gates", 4), ("median_seconds", 25), ("p90_seconds", 40));
        var limited = fixture.Report("lanes", "--last", "3").GetProperty("rows")[0];
        Numbers(limited, ("gates", 3), ("median_seconds", 40), ("p90_seconds", 50));
    }

    [Fact]
    public void Classes_TwoAttempts_SumPerAttemptAndOrderByTotal()
    {
        using var fixture = new Fixture();
        fixture.Trx("z-goal", 1, "alpha", ("A", 1), ("B", 2));
        fixture.Trx("z-goal", 1, "beta", ("A", 1));
        fixture.Trx("a-goal", 2, "alpha", ("A", 1), ("B", 3));
        var rows = fixture.Report("classes").GetProperty("rows");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal("B", rows[0].GetProperty("class").GetString());
        Numbers(rows[0], ("gates", 2), ("total_seconds", 5), ("median_seconds", 2.5));
        Assert.Equal("A", rows[1].GetProperty("class").GetString());
        Numbers(rows[1], ("gates", 2), ("total_seconds", 3), ("median_seconds", 1.5));
        var alpha = fixture.Report("classes", "--lane", "alpha").GetProperty("rows");
        Numbers(alpha[1], ("total_seconds", 2), ("median_seconds", 1));
        var newest = fixture.Report("classes", "--last", "1", "--top", "1").GetProperty("rows");
        Assert.Equal(1, newest.GetArrayLength());
        Assert.Equal("B", newest[0].GetProperty("class").GetString());
        Numbers(newest[0], ("gates", 1), ("total_seconds", 3));
    }

    [Fact]
    public void Classes_LaneFilter_SelectsAttemptsBeforeApplyingLast()
    {
        using var fixture = new Fixture();
        fixture.Trx("goal", 1, "alpha", ("OnlyAlpha", 7));
        fixture.Trx("goal", 2, "beta", ("OnlyBeta", 100));
        var row = Assert.Single(fixture.Report("classes", "--lane", "alpha", "--last", "1")
            .GetProperty("rows").EnumerateArray());
        Assert.Equal("OnlyAlpha", row.GetProperty("class").GetString());
        Numbers(row, ("gates", 1), ("total_seconds", 7));
    }

    [Fact]
    public void Classes_MissingDefinitions_UseQualifiedNameWithoutDataArguments()
    {
        using var fixture = new Fixture();
        var path = fixture.Trx("goal", 1, "alpha", ("Unused", 1));
        File.WriteAllText(path, "<TestRun><Results><UnitTestResult testName='Namespace.A.Method(value.with.dot)' duration='00:00:02'/></Results></TestRun>");
        fixture.Trx("goal", 2, "alpha", ("B", 2));
        var rows = fixture.Report("classes").GetProperty("rows");
        Assert.Equal(new[] { "B", "Namespace.A" }, rows.EnumerateArray().Select(row => row.GetProperty("class").GetString()));
        Numbers(rows[1], ("gates", 1), ("total_seconds", 2), ("median_seconds", 2));
    }

    [Fact]
    public void Compare_KnownSplit_ReportsDeltasAndMissingSideAsNull()
    {
        using var fixture = new Fixture();
        for (var day = 1; day <= 8; day++) fixture.Duration("alpha", day * 10, day);
        fixture.Duration("before-only", 12, 1);
        var rows = fixture.Report("compare", "--before", "2026-10-04T00:00:00Z",
            "--after", "2026-10-04T00:00:00Z", "--last", "3").GetProperty("rows");
        Assert.Equal(2, rows.GetArrayLength());
        Numbers(rows[0], ("before_gates", 3), ("before_median", 20), ("before_p90", 30),
            ("after_gates", 3), ("after_median", 60), ("after_p90", 70), ("median_delta", 40), ("p90_delta", 40));
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("median_delta").ValueKind);
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("p90_delta").ValueKind);
    }

    [Theory]
    [InlineData("<TestRun><Results>")]
    [InlineData("<TestRun />")]
    [InlineData("<TestRun><Results><UnitTestResult testName='Bad.Test' /></Results></TestRun>")]
    [InlineData("<TestRun><Results><UnitTestResult testName='Leak.Test' duration='00:00:09'/><UnitTestResult testName='Bad.Test' duration='bad'/></Results></TestRun>")]
    public void Classes_InvalidTrx_SkipsWholeFileWithoutPartialTotals(string content)
    {
        using var fixture = new Fixture();
        fixture.Trx("goal", 1, "valid", ("Good", 2));
        var corrupt = fixture.Trx("goal", 1, "bad", ("Unused", 1));
        File.WriteAllText(corrupt, content);
        var report = fixture.Report("classes");
        Assert.Equal(1, report.GetProperty("skipped").GetInt32());
        var row = Assert.Single(report.GetProperty("rows").EnumerateArray());
        Assert.Equal("Good", row.GetProperty("class").GetString());
        Numbers(row, ("total_seconds", 2));
    }

    [Fact]
    public void Lanes_MalformedJsonLines_CountSkippedAndKeepValidSamples()
    {
        using var fixture = new Fixture();
        fixture.Duration("alpha", 10, 1);
        File.AppendAllText(fixture.DurationsPath, "{broken\n{}\n");
        var report = fixture.Report("lanes");
        Assert.Equal(2, report.GetProperty("skipped").GetInt32());
        Numbers(Assert.Single(report.GetProperty("rows").EnumerateArray()), ("median_seconds", 10));
    }

    [Theory]
    [InlineData("lanes")]
    [InlineData("classes")]
    [InlineData("compare", "--before", "2026-10-01T00:00:00Z", "--after", "2026-10-02T00:00:00Z")]
    public void EmptyEvidence_AllForms_SucceedWithoutCreatingFiles(params string[] args)
    {
        using var fixture = new Fixture();
        Assert.Empty(fixture.Report(args).GetProperty("rows").EnumerateArray());
        Assert.False(Directory.Exists(fixture.Workspace.OrchestratorDirectory));
    }

    [Theory]
    [InlineData()]
    [InlineData("unknown")]
    [InlineData("lanes", "--last", "0")]
    [InlineData("classes", "--top", "-1")]
    [InlineData("lanes", "--lane", "alpha")]
    [InlineData("classes", "--bogus")]
    [InlineData("compare")]
    [InlineData("compare", "--before", "2026-10-01", "--after", "2026-10-02")]
    [InlineData("compare", "--before", "2026-10-03T00:00:00Z", "--after", "2026-10-02T00:00:00Z")]
    public void Arguments_InvalidForms_ThrowUsage(params string[] args)
    {
        using var fixture = new Fixture();
        var error = Assert.Throws<ArgumentException>(() =>
            CliGateStatsQueryCommand.Execute(["gate-stats", .. args], fixture.Workspace));
        Assert.Contains(CliCommandHelp.GateStatsUsage, error.Message);
        Assert.False(Directory.Exists(fixture.Workspace.OrchestratorDirectory));
        Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(["gate-stats", "--help"]));
        Assert.True(CliHandledVerbRegistry.IsHandled("gate-stats"));
        Assert.Throws<ArgumentException>(() => CliCommandHelp.ThrowIfInvalidFlags(["gate-stats", "--bogus"]));
    }

    private static void Numbers(JsonElement row, params (string Name, double Value)[] expected)
    {
        foreach (var (name, value) in expected) Assert.Equal(value, row.GetProperty(name).GetDouble());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = CreateTempDirectory();
        internal OrchestratorWorkspace Workspace => OrchestratorWorkspace.ForDirectory(_root);
        internal string DurationsPath => Path.Combine(Workspace.OrchestratorDirectory, AcceptanceLaneDurationStore.FileName);

        internal void Duration(string lane, double seconds, int day)
        {
            Directory.CreateDirectory(Workspace.OrchestratorDirectory);
            File.AppendAllText(DurationsPath, JsonSerializer.Serialize(new { version = 1, laneName = lane,
                durationSeconds = seconds, recordedAt = $"2026-10-{day:00}T00:00:00Z" }) + "\n");
        }

        internal string Trx(string goal, int day, string lane, params (string Class, double Seconds)[] tests)
        {
            var directory = Path.Combine(Workspace.OrchestratorDirectory, "acceptance-gate-attempts", goal);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"12345678-0-202610{day:00}000000000-00000000000000000000000000000000.{lane}.trx");
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            new XDocument(new XElement(ns + "TestRun", new XElement(ns + "Results", tests.Select((test, i) =>
                new XElement(ns + "UnitTestResult", new XAttribute("testId", i),
                    new XAttribute("testName", $"Namespace.{test.Class}.Method(case.with.dot)"),
                    new XAttribute("outcome", "Failed"), new XAttribute("duration", TimeSpan.FromSeconds(test.Seconds).ToString("c", CultureInfo.InvariantCulture))))),
                new XElement(ns + "TestDefinitions", tests.Select((test, i) => new XElement(ns + "UnitTest",
                    new XAttribute("id", i), new XElement(ns + "TestMethod", new XAttribute("className", test.Class),
                        new XAttribute("name", "Method"))))))).Save(path);
            return path;
        }

        internal JsonElement Report(params string[] args)
        {
            var table = Run(["gate-stats", .. args]);
            using var json = JsonDocument.Parse(Run(["gate-stats", .. args, "--json"]));
            var report = json.RootElement;
            var lines = table.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            var rows = report.GetProperty("rows").EnumerateArray().ToArray();
            Assert.Equal(rows.Length + 1, lines.Length);
            foreach (var (row, line) in rows.Zip(lines))
                Assert.Equal(string.Join('\t', row.EnumerateObject().Select(property =>
                    $"{property.Name}={(property.Value.ValueKind == JsonValueKind.Null ? "-" : property.Value.ToString())}")), line);
            Assert.Equal($"skipped={report.GetProperty("skipped").GetInt32()}", lines[^1]);
            return report.Clone();
        }

        private string Run(string[] args)
        {
            var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
            var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? goal = null;
            Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));
            var text = CaptureConsole(() =>
            {
                Assert.True(CliReadOnlyCommandRunner.TryExecute(args, repository, Workspace,
                    new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref goal, out var changed));
                Assert.False(changed);
            });
            Assert.Empty(repository.ObservedWriteOperationTags);
            Assert.Equal(0, repository.FullLoadAttempts + repository.LoadGoalsCount + repository.LoadGoalCount +
                repository.ListGoalMetadataCount + repository.SaveAttempts + repository.MergeSaveAttempts +
                repository.MutationAttempts + repository.ListOutboxMessagesCount + repository.OutboxClaimAttempts);
            Assert.Equal(before.Keys.Order(), Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order());
            foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
            return text;
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
