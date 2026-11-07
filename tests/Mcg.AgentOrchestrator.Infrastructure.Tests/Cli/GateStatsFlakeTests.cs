using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel safe: private attempt directories and AsyncLocal console capture; no shared stores.
public sealed class GateStatsFlakeTests : CliTaskQueryTestSupport
{
    [Fact]
    public void Flakes_FailedThenPassed_CountsOneFlakePerAttempt()
    {
        using var fixture = new Fixture();
        fixture.Trx(1, "alpha", 0, ("Flaky", "Failed"));
        fixture.Trx(1, "alpha", 1, ("Flaky", "Passed"));
        fixture.Trx(1, "alpha", 2, ("Flaky", "Failed"));
        fixture.Trx(1, "alpha", 3, ("Flaky", "Passed"));
        Counts(OnlyRow(fixture.Report()), attempts: 1, pass: 0, flake: 1, hard: 0, rate: 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Flakes_FinalFailure_CountsHardFailureWithoutFlake(bool intermediatePass)
    {
        using var fixture = new Fixture();
        fixture.Trx(1, "alpha", 0, ("Hard", "Failed"));
        if (intermediatePass) fixture.Trx(1, "alpha", 1, ("Hard", "Passed"));
        fixture.Trx(1, "alpha", 2, ("Hard", "Failed"));
        Counts(OnlyRow(fixture.Report()), attempts: 1, pass: 0, flake: 0, hard: 1, rate: 0);
    }

    [Fact]
    public void Flakes_AllPassed_CountsCleanAttemptsWithZeroRate()
    {
        using var fixture = new Fixture();
        fixture.Trx(1, "alpha", 0, ("Clean", "Passed"));
        fixture.Trx(1, "alpha", 1, ("Clean", "Passed"));
        fixture.Trx(2, "alpha", 0, ("Clean", "Passed"));
        Counts(OnlyRow(fixture.Report()), attempts: 2, pass: 2, flake: 0, hard: 0, rate: 0);
    }

    [Fact]
    public void Flakes_TwoGoalsWithSameAttemptStamp_AggregatesSeparateAttempts()
    {
        using var fixture = new Fixture();
        fixture.Trx("a-goal", 1, "alpha", 0, ("Mixed", "Failed"));
        fixture.Trx("a-goal", 1, "alpha", 1, ("Mixed", "Passed"));
        fixture.Trx("b-goal", 1, "alpha", 0, ("Mixed", "Passed"));
        var report = fixture.Report();
        Assert.Equal(2, report.GetProperty("gates").GetInt32());
        Counts(OnlyRow(report), attempts: 2, pass: 1, flake: 1, hard: 0, rate: 0.5);
    }

    [Fact]
    public void Flakes_CountThenRateThenName_SortsBeforeTopLimit()
    {
        using var fixture = new Fixture();
        fixture.Trx(1, "alpha", 0, ("Z", "Failed"), ("B", "Failed"), ("A", "Failed"), ("C", "Failed"));
        fixture.Trx(1, "alpha", 1, ("Z", "Passed"), ("B", "Passed"), ("A", "Passed"), ("C", "Passed"));
        fixture.Trx(2, "alpha", 0, ("Z", "Failed"), ("C", "Passed"));
        fixture.Trx(2, "alpha", 1, ("Z", "Passed"));
        fixture.Trx(3, "alpha", 0, ("Z", "Passed"));
        var rows = fixture.Report().GetProperty("tests");
        Assert.Equal(new[] { "Z", "A", "B", "C" }, rows.EnumerateArray()
            .Select(row => row.GetProperty("test").GetString()!["Namespace.Tests.".Length..]));
        Counts(rows[0], attempts: 3, pass: 1, flake: 2, hard: 0, rate: 2d / 3);
        Assert.Equal(new[] { "Namespace.Tests.Z", "Namespace.Tests.A" }, fixture.Report("--top", "2")
            .GetProperty("tests").EnumerateArray().Select(row => row.GetProperty("test").GetString()));
    }

    [Fact]
    public void Flakes_RunTenAfterRunTwo_UsesNumericOrderAndLatestPresentResult()
    {
        using var fixture = new Fixture();
        fixture.Trx(1, "alpha", 10, ("Numeric", "Passed"));
        fixture.Trx(1, "alpha", 2, ("Numeric", "Failed"), ("AbsentLater", "Failed"));
        var rows = fixture.Report().GetProperty("tests");
        Assert.Equal("Namespace.Tests.Numeric", rows[0].GetProperty("test").GetString());
        Counts(rows[0], attempts: 1, pass: 0, flake: 1, hard: 0, rate: 1);
        Counts(rows[1], attempts: 1, pass: 0, flake: 0, hard: 1, rate: 0);
    }

    [Theory]
    [InlineData("Passed", "Passed", 1, 0, 0)]
    [InlineData("Failed", "Passed", 0, 1, 0)]
    [InlineData("Failed", "Failed", 0, 0, 1)]
    [InlineData("Passed", "Failed", 0, 0, 1)]
    [InlineData("NotExecuted", "Passed", 1, 0, 0)]
    public void Flakes_MultipleLanes_CountsOnceWithHardOverFlakeOverPass(
        string initial, string final, int pass, int flake, int hard)
    {
        using var fixture = new Fixture();
        fixture.Trx(1, "alpha", 0, ("Shared", initial));
        fixture.Trx(1, "alpha", 1, ("Shared", final));
        fixture.Trx(1, "beta", 0, ("Shared", "Passed"));
        var report = fixture.Report();
        Assert.Equal(0, report.GetProperty("nonterminal").GetInt32());
        Counts(OnlyRow(report), attempts: 1, pass, flake, hard, rate: flake);
    }

    [Fact]
    public void Flakes_FailInOneLanePassInAnother_DoesNotMatchAcrossLanes()
    {
        using var fixture = new Fixture();
        fixture.Trx(1, "alpha", 0, ("Separate", "Failed"));
        fixture.Trx(1, "beta", 1, ("Separate", "Passed"));
        Counts(OnlyRow(fixture.Report()), attempts: 1, pass: 0, flake: 0, hard: 1, rate: 0);
    }

    [Fact]
    public void Flakes_HardLaneAlongsideFlakyLane_CountsOnlyHardFailure()
    {
        using var fixture = new Fixture();
        fixture.Trx(1, "alpha", 0, ("Shared", "Failed"));
        fixture.Trx(1, "alpha", 1, ("Shared", "Passed"));
        fixture.Trx(1, "beta", 0, ("Shared", "Failed"));
        Counts(OnlyRow(fixture.Report()), attempts: 1, pass: 0, flake: 0, hard: 1, rate: 0);
    }

    [Theory]
    [InlineData("NotExecuted")]
    [InlineData("Inconclusive")]
    [InlineData("Skipped")]
    [InlineData("Error")]
    [InlineData("Timeout")]
    [InlineData(null)]
    public void Flakes_NonterminalOutcomes_IgnoreRunsAndCountOnlyIgnoredPairs(string? outcome)
    {
        using var fixture = new Fixture();
        fixture.Trx(1, "alpha", 0, ("OnlyIgnored", outcome), ("LatestTerminal", "Failed"));
        fixture.Trx(1, "alpha", 1, ("OnlyIgnored", outcome), ("LatestTerminal", outcome));
        fixture.Trx(1, "beta", 0, ("OnlyIgnored", outcome));
        fixture.Trx(2, "alpha", 0, ("OnlyIgnored", outcome));
        var report = fixture.Report();
        Assert.Equal(2, report.GetProperty("nonterminal").GetInt32());
        var row = OnlyRow(report);
        Assert.Equal("Namespace.Tests.LatestTerminal", row.GetProperty("test").GetString());
        Counts(row, attempts: 1, pass: 0, flake: 0, hard: 1, rate: 0);
    }

    [Fact]
    public void Flakes_TheoryCasesAndDuplicateResults_PreserveIdentityAndFailure()
    {
        using var fixture = new Fixture();
        fixture.Trx(1, "alpha", 0, ("Theory(1)", "failed"), ("Theory(1)", "Passed"), ("Theory(2)", "passed"));
        fixture.Trx(1, "alpha", 1, ("Theory(1)", "PASSED"));
        var rows = fixture.Report().GetProperty("tests");
        Assert.Equal("Namespace.Tests.Theory(1)", rows[0].GetProperty("test").GetString());
        Counts(rows[0], attempts: 1, pass: 0, flake: 1, hard: 0, rate: 1);
        Assert.Equal("Namespace.Tests.Theory(2)", rows[1].GetProperty("test").GetString());
        Counts(rows[1], attempts: 1, pass: 1, flake: 0, hard: 0, rate: 0);
    }

    [Theory]
    [InlineData("<TestRun><Results>")]
    [InlineData("<TestRun />")]
    [InlineData("<TestRun><Results><UnitTestResult testName='Namespace.Tests.Hard' outcome='Passed' duration='00:00:01'/><UnitTestResult duration='bad'/></Results></TestRun>")]
    public void Flakes_CorruptRerun_CountsSkippedWithoutPartialResults(string content)
    {
        using var fixture = new Fixture();
        fixture.Trx(1, "alpha", 0, ("Hard", "Failed"));
        var corrupt = fixture.Trx(1, "alpha", 1, ("Hard", "Passed"));
        File.WriteAllText(corrupt, content);
        var report = fixture.Report();
        Assert.Equal(1, report.GetProperty("gates").GetInt32());
        Assert.Equal(1, report.GetProperty("skipped").GetInt32());
        Counts(OnlyRow(report), attempts: 1, pass: 0, flake: 0, hard: 1, rate: 0);
    }

    [Fact]
    public void Flakes_DefaultWindowAndExplicitLast_SelectRecentAttempts()
    {
        using var fixture = new Fixture();
        for (var day = 1; day <= 21; day++) fixture.Trx(day, "alpha", 0, ("Clean", "Passed"));
        var report = fixture.Report();
        Assert.Equal(20, report.GetProperty("gates").GetInt32());
        Counts(OnlyRow(report), attempts: 20, pass: 20, flake: 0, hard: 0, rate: 0);
        Counts(OnlyRow(fixture.Report("--last", "2")), attempts: 2, pass: 2, flake: 0, hard: 0, rate: 0);
    }

    [Fact]
    public void Flakes_OverflowingRunSuffix_CountsSkippedWithoutLosingAttempt()
    {
        using var fixture = new Fixture();
        fixture.Trx(1, "alpha-run-2147483648", 0, ("InvalidRun", "Passed"));
        var report = fixture.Report();
        Assert.Equal(1, report.GetProperty("gates").GetInt32());
        Assert.Equal(1, report.GetProperty("skipped").GetInt32());
        Assert.Empty(report.GetProperty("tests").EnumerateArray());
    }

    [Fact]
    public void Flakes_DefaultTop_LimitsToTwentyTests()
    {
        using var fixture = new Fixture();
        fixture.Trx(1, "alpha", 0, Enumerable.Range(1, 21)
            .Select(index => ($"Test{index:00}", (string?)"Passed")).ToArray());
        Assert.Equal(20, fixture.Report().GetProperty("tests").GetArrayLength());
        Assert.Equal(21, fixture.Report("--top", "21").GetProperty("tests").GetArrayLength());
    }

    [Fact]
    public void Flakes_EmptyEvidence_ReportsZeroWithoutCreatingState()
    {
        using var fixture = new Fixture();
        var report = fixture.Report();
        Assert.Empty(report.GetProperty("tests").EnumerateArray());
        Assert.Equal(0, report.GetProperty("gates").GetInt32());
        Assert.Equal(0, report.GetProperty("skipped").GetInt32());
        Assert.Equal(0, report.GetProperty("nonterminal").GetInt32());
        Assert.False(Directory.Exists(fixture.Workspace.OrchestratorDirectory));
    }

    [Theory]
    [InlineData("--lane", "alpha")]
    [InlineData("--before", "2026-10-01T00:00:00Z")]
    [InlineData("--after", "2026-10-01T00:00:00Z")]
    [InlineData("--top", "0")]
    [InlineData("--last", "-1")]
    [InlineData("--last", "2147483648")]
    [InlineData("--top")]
    [InlineData("--json", "--json")]
    public void Flakes_InvalidArguments_ThrowUsage(params string[] args)
    {
        using var fixture = new Fixture();
        var error = Assert.Throws<ArgumentException>(() =>
            CliGateStatsQueryCommand.Execute(["gate-stats", "flakes", .. args], fixture.Workspace));
        Assert.Equal(CliCommandHelp.GateStatsUsage, error.Message);
        Assert.False(Directory.Exists(fixture.Workspace.OrchestratorDirectory));
    }

    private static JsonElement OnlyRow(JsonElement report) => Assert.Single(report.GetProperty("tests").EnumerateArray());

    private static void Counts(JsonElement row, int attempts, int pass, int flake, int hard, double rate)
    {
        Assert.Equal(attempts, row.GetProperty("attempts").GetInt32());
        Assert.Equal(pass, row.GetProperty("pass").GetInt32());
        Assert.Equal(flake, row.GetProperty("flake").GetInt32());
        Assert.Equal(hard, row.GetProperty("hard").GetInt32());
        Assert.Equal(rate, row.GetProperty("flakeRate").GetDouble());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = CreateTempDirectory();
        internal OrchestratorWorkspace Workspace => OrchestratorWorkspace.ForDirectory(_root);

        internal string Trx(int day, string lane, int run, params (string Name, string? Outcome)[] tests) =>
            Trx("goal", day, lane, run, tests);

        internal string Trx(string goal, int day, string lane, int run, params (string Name, string? Outcome)[] tests)
        {
            var directory = Path.Combine(Workspace.OrchestratorDirectory, "acceptance-gate-attempts", goal);
            Directory.CreateDirectory(directory);
            var suffix = run == 0 ? "" : $"-run-{run.ToString(CultureInfo.InvariantCulture)}";
            var path = Path.Combine(directory, $"12345678-0-202610{day:00}000000000-00000000000000000000000000000000.{lane}{suffix}.trx");
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            new XDocument(new XElement(ns + "TestRun", new XElement(ns + "Results", tests.Select((test, index) =>
                new XElement(ns + "UnitTestResult", new XAttribute("testId", index),
                    new XAttribute("testName", $"Namespace.Tests.{test.Name}"),
                    test.Outcome is null ? null : new XAttribute("outcome", test.Outcome),
                    new XAttribute("duration", "00:00:01")))),
                new XElement(ns + "TestDefinitions", tests.Select((test, index) => new XElement(ns + "UnitTest",
                    new XAttribute("id", index), new XElement(ns + "TestMethod",
                        new XAttribute("className", "Namespace.Tests"),
                        new XAttribute("name", test.Name.Split('(')[0]))))))).Save(path);
            return path;
        }

        // Every fixture report round-trips JSON and checks identical text numbers and ordering.
        internal JsonElement Report(params string[] args)
        {
            var table = Run(["gate-stats", "flakes", .. args]);
            using var json = JsonDocument.Parse(Run(["gate-stats", "flakes", .. args, "--json"]));
            var report = json.RootElement;
            Assert.Equal("flakes", report.GetProperty("form").GetString());
            var lines = table.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            var rows = report.GetProperty("tests").EnumerateArray().ToArray();
            Assert.Equal(rows.Length + 1, lines.Length);
            Assert.Equal($"form=flakes\tgates={report.GetProperty("gates").GetInt32()}\tskipped={report.GetProperty("skipped").GetInt32()}\tnonterminal={report.GetProperty("nonterminal").GetInt32()}", lines[0]);
            foreach (var (row, line) in rows.Zip(lines.Skip(1)))
            {
                Assert.Equal(string.Join('\t', row.EnumerateObject().Select(property =>
                    $"{property.Name}={property.Value}")), line);
                Assert.Equal(row.GetProperty("attempts").GetInt32(), row.GetProperty("pass").GetInt32() +
                    row.GetProperty("flake").GetInt32() + row.GetProperty("hard").GetInt32());
            }
            return report.Clone();
        }

        private string Run(string[] args)
        {
            var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
            var directories = Directory.GetDirectories(_root, "*", SearchOption.AllDirectories).Order().ToArray();
            var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? goal = null;
            Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));
            var text = CaptureConsole(() =>
            {
                Assert.True(CliReadOnlyCommandRunner.TryExecute(args, repository, Workspace,
                    new InMemoryModelProviderRegistry([]), ref agents, ref profiles, ref goal, out var changed));
                Assert.False(changed);
            });
            Assert.Empty(repository.ObservedWriteOperationTags);
            Assert.Equal(0, repository.FullLoadAttempts + repository.LoadGoalsCount + repository.LoadGoalCount +
                repository.ListGoalMetadataCount + repository.SaveAttempts + repository.MergeSaveAttempts +
                repository.MutationAttempts + repository.ListOutboxMessagesCount + repository.OutboxClaimAttempts);
            Assert.Equal(before.Keys.Order(), Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order());
            Assert.Equal(directories, Directory.GetDirectories(_root, "*", SearchOption.AllDirectories).Order());
            foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
            return text;
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
