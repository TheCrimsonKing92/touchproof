using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel safe: private artifact trees, no processes or stores, AsyncLocal console capture.
public sealed class GateFailureDetailReportTests : CliTaskQueryTestSupport
{
    private const string Lane = "infrastructure-tests-balanced-2";
    private const string Check = "infrastructure tests: Balanced 2";
    private const string Name = "Namespace.AttributedFailureAutoRouteScenarioTests.TrainRoutesOwnershipOrReachCulprit(ownership: True)";
    private const string Message = "Ownership probe timed out after 300000 ms";

    [Fact]
    public void DeniedFlake_TextAndJsonExposeDiagnosticsAndElevenAttemptHistory()
    {
        using var fixture = new Fixture();
        for (var index = 1; index <= 11; index++)
        {
            fixture.Accepted(index);
            fixture.Trx(index, Lane, 0, Name, index >= 10 ? "Failed" : "Passed");
        }
        fixture.Trx(11, Lane, 1, Name, "Passed");
        fixture.Artifact(11, ".out.log", $"LANE_RERUN lane=\"{Check}\" outcome=flake\n" +
            $"LANE_RERUN_CREDIT_DENIED lane=\"{Check}\" reason=no-cross-goal-history\n" +
            "LANE_RERUN_CREDIT_DENIED lane=\"unrelated lane\" reason=not-eligible\n");

        var report = fixture.Json(11);
        Assert.Equal(Fixture.Id(11), report.GetProperty("attemptId").GetString());
        Assert.Equal("flake-denied", report.GetProperty("cause").GetString());
        var verdict = report.GetProperty("verdictLine").GetString()!;
        Assert.Contains("Balanced 2", verdict);
        Assert.Contains("rerun 1 passed", verdict);
        Assert.Contains("no-cross-goal-history", verdict);
        Assert.DoesNotContain("unrelated lane", verdict);
        var test = Assert.Single(report.GetProperty("tests").EnumerateArray());
        Assert.Equal(Name, test.GetProperty("fullTestName").GetString());
        Assert.Equal(Lane, test.GetProperty("lane").GetString());
        Assert.Equal(Message, test.GetProperty("failureMessage").GetString());
        Assert.Equal(Enumerable.Range(1, 5).Select(index => $"  at frame{index}"),
            test.GetProperty("stackTraceLines").EnumerateArray().Select(line => line.GetString()));
        Assert.Equal("Failed", test.GetProperty("firstRunOutcome").GetString());
        var rerun = Assert.Single(test.GetProperty("rerunOutcomes").EnumerateArray());
        Assert.Equal(1, rerun.GetProperty("run").GetInt32());
        Assert.Equal("Passed", rerun.GetProperty("outcome").GetString());
        var trx = test.GetProperty("trxPath").GetString()!;
        Assert.Equal(Path.GetRelativePath(fixture.Workspace.OrchestratorDirectory,
            fixture.PathFor(11, $".{Lane}.trx")), trx);
        var history = test.GetProperty("flakeHistory");
        Assert.Equal(11, history.GetProperty("attempts").GetInt32());
        Assert.Equal(1, history.GetProperty("flakes").GetInt32());
        Assert.Equal(1, history.GetProperty("hardFailures").GetInt32());

        var text = fixture.Run(11);
        foreach (var expected in new[] { $"attempt={Fixture.Id(11)}", "cause=flake-denied", $"verdict={verdict}",
                     $"test={Name}", $"lane={Lane}", $"failureMessage={Message}", "stackTrace=  at frame1",
                     "stackTrace=  at frame5", "firstRunOutcome=Failed", "rerunOutcomes=1:Passed", $"trxPath={trx}",
                     "flakeHistory=attempts=11 flakes=1 hardFailures=1" })
            Assert.Contains(expected, text);
        Assert.DoesNotContain("at frame6", text);
    }

    [Fact]
    public void NoDecidingEvidence_VerdictIsExactlyUnattributed()
    {
        using var fixture = new Fixture();
        fixture.Accepted(1);
        fixture.Trx(1, Lane, 0, Name, "Failed");
        var report = fixture.Json(1);
        Assert.Equal("unattributed", report.GetProperty("cause").GetString());
        Assert.Equal("unattributed", report.GetProperty("verdictLine").GetString());
        Assert.Empty(Assert.Single(report.GetProperty("tests").EnumerateArray()).GetProperty("rerunOutcomes").EnumerateArray());
    }

    [Fact]
    public void RerunFailures_KeepLaneIdentityOrderAndEarliestDiagnostics()
    {
        using var fixture = new Fixture();
        fixture.Accepted(1);
        fixture.Trx(1, Lane, 0, Name, "Passed");
        fixture.Trx(1, Lane, 1, Name, "Failed", "first failure");
        fixture.Trx(1, Lane, 2, Name, "Failed", "later failure");
        fixture.Trx(1, Lane, 3, Name, "Passed");
        fixture.Trx(1, Lane, 4, Name, "Passed");
        fixture.Trx(1, "alpha", 0, Name, "Failed", "other lane");
        var tests = fixture.Json(1).GetProperty("tests").EnumerateArray().ToArray();
        Assert.Equal(new[] { "alpha", Lane }, tests.Select(test => test.GetProperty("lane").GetString()));
        Assert.All(tests, test => Assert.Equal(Name, test.GetProperty("fullTestName").GetString()));
        Assert.Equal("other lane", tests[0].GetProperty("failureMessage").GetString());
        var test = tests[1];
        Assert.Equal("first failure", test.GetProperty("failureMessage").GetString());
        Assert.Equal("Passed", test.GetProperty("firstRunOutcome").GetString());
        Assert.EndsWith($".{Lane}-run-1.trx", test.GetProperty("trxPath").GetString());
        var reruns = test.GetProperty("rerunOutcomes").EnumerateArray().ToArray();
        Assert.Equal(new[] { 1, 2, 3, 4 }, reruns.Select(run => run.GetProperty("run").GetInt32()));
        Assert.Equal(new[] { "Failed", "Failed", "Passed", "Passed" },
            reruns.Select(run => run.GetProperty("outcome").GetString()));
    }

    [Fact]
    public void MissingDiagnosticsAndHistory_StayExplicitlyUnavailable()
    {
        using var fixture = new Fixture();
        fixture.Accepted(1);
        // Error is fatal to the classifier but is not a terminal pass/fail in Flakes.
        fixture.Trx(1, Lane, 1, Name, "Error", message: null);
        fixture.Trx(1, Lane, 2, "Namespace.Other.Test", "Passed");
        var test = Assert.Single(fixture.Json(1).GetProperty("tests").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, test.GetProperty("failureMessage").ValueKind);
        Assert.Equal(JsonValueKind.Null, test.GetProperty("firstRunOutcome").ValueKind);
        Assert.Empty(test.GetProperty("stackTraceLines").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, test.GetProperty("flakeHistory").ValueKind);
        var reruns = test.GetProperty("rerunOutcomes").EnumerateArray().ToArray();
        Assert.Equal(2, reruns.Length);
        Assert.Equal("Error", reruns[0].GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, reruns[1].GetProperty("outcome").ValueKind);
        var text = fixture.Run(1);
        Assert.Contains("failureMessage=unavailable", text);
        Assert.Contains("firstRunOutcome=unavailable", text);
        Assert.Contains("stackTrace=unavailable", text);
        Assert.Contains("flakeHistory=unavailable", text);
    }

    [Fact]
    public void OlderAndResultOnlyAttempts_AreAddressableWithoutLastWindow()
    {
        using var fixture = new Fixture();
        for (var index = 1; index <= 22; index++)
            fixture.Result(index, new { kind = "fault", faultKind = "blocked-build-slot" });
        fixture.Trx(1, Lane, 0, Name, "Failed");
        fixture.Trx(22, Lane, 0, "Namespace.Other.Test", "Passed");
        var old = fixture.Json(1);
        Assert.Equal("apparatus-or-environment", old.GetProperty("cause").GetString());
        Assert.Contains("blocked-build-slot", old.GetProperty("verdictLine").GetString());
        Assert.Single(old.GetProperty("tests").EnumerateArray());
        Assert.Empty(fixture.Json(2).GetProperty("tests").EnumerateArray());
        Assert.Contains("tests=none", fixture.Run(2));
    }

    [Theory]
    [InlineData(".result.json", "{broken")]
    [InlineData(".result.json", "[]")]
    [InlineData(".attempt.json", "{broken")]
    [InlineData(".infrastructure-tests-balanced-2.trx", "<broken")]
    public void MalformedEvidence_ThrowsWithAttemptAndSource(string suffix, string contents)
    {
        using var fixture = new Fixture();
        fixture.Accepted(1);
        fixture.Trx(1, Lane, 0, Name, "Failed");
        fixture.Artifact(1, suffix, contents);
        var error = Assert.Throws<InvalidOperationException>(() => fixture.Run(1, "--json"));
        Assert.Contains(Fixture.Id(1), error.Message);
        Assert.Contains(suffix, error.Message);
    }

    [Theory]
    [InlineData("failure", "--last", "1")]
    [InlineData("failure", "--top", "1")]
    [InlineData("failure", "--attempt", "")]
    [InlineData("failure", "--attempt", "--json")]
    [InlineData("failure", "--json", "--json")]
    [InlineData("failures", "--attempt", "unknown")]
    public void InvalidOptions_ThrowUsage(string form, string first, string second)
    {
        using var fixture = new Fixture();
        var error = Assert.Throws<ArgumentException>(() => CliGateStatsQueryCommand.Execute(
            ["gate-stats", form, first, second], fixture.Workspace));
        Assert.Equal(CliCommandHelp.GateStatsUsage, error.Message);
    }

    [Fact]
    public void HelpAndRequiredAttempt_ValidateTheNewForm()
    {
        Assert.Contains("gate-stats failure --attempt <id> [--json]", CliCommandHelp.GateStatsUsage);
        CliCommandHelp.ThrowIfInvalidFlags(["gate-stats", "failure", "--attempt", "unknown", "--json"]);
        using var fixture = new Fixture();
        var missing = Assert.Throws<ArgumentException>(() => CliGateStatsQueryCommand.Execute(
            ["gate-stats", "failure"], fixture.Workspace));
        Assert.Equal(CliCommandHelp.GateStatsUsage, missing.Message);
        var unknown = Assert.Throws<ArgumentException>(() => CliGateStatsQueryCommand.Execute(
            ["gate-stats", "failure", "--attempt", "unknown-attempt", "--json"], fixture.Workspace));
        Assert.Contains("unknown-attempt", unknown.Message);
        var duplicate = Assert.Throws<ArgumentException>(() => CliGateStatsQueryCommand.Execute(
            ["gate-stats", "failure", "--attempt", "x", "--attempt", "y"], fixture.Workspace));
        Assert.Equal(CliCommandHelp.GateStatsUsage, duplicate.Message);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = CreateTempDirectory();
        internal OrchestratorWorkspace Workspace => OrchestratorWorkspace.ForDirectory(_root);
        internal static string Id(int index) => "12345678-0-" + new DateTime(2026, 11, 4).AddMinutes(index)
            .ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + "-00000000000000000000000000000000";
        internal string PathFor(int index, string suffix) => Path.Combine(Workspace.OrchestratorDirectory,
            "acceptance-gate-attempts", "123456789abcdef", Id(index) + suffix);

        internal void Artifact(int index, string suffix, string text)
        {
            var path = PathFor(index, suffix);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        internal void Result(int index, object result) => Artifact(index, ".result.json", JsonSerializer.Serialize(result));

        internal void Accepted(int index)
        {
            Result(index, new { kind = "accepted", acceptance = new { passed = false, failedChecks = new[] { Check },
                checkAttributions = new[] { new { checkName = Check, origin = 2, cause = 0, evidence = "fixture attribution" } } } });
            Artifact(index, ".out.log", "");
        }

        internal void Trx(int index, string lane, int run, string name, string outcome, string? message = Message)
        {
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            var error = message is null ? null : new XElement(ns + "Output", new XElement(ns + "ErrorInfo",
                new XElement(ns + "Message", message), new XElement(ns + "StackTrace",
                    string.Join("\r\n", Enumerable.Range(1, 7).Select(frame => $"  at frame{frame}")))));
            var xml = new XDocument(new XElement(ns + "TestRun", new XElement(ns + "Results",
                new XElement(ns + "UnitTestResult", new XAttribute("testId", "1"), new XAttribute("testName", name),
                    new XAttribute("outcome", outcome), new XAttribute("duration", "00:00:01"), error)),
                new XElement(ns + "TestDefinitions", new XElement(ns + "UnitTest", new XAttribute("id", "1"),
                    new XElement(ns + "TestMethod", new XAttribute("className", name[..name.LastIndexOf('.')]),
                        new XAttribute("name", name[(name.LastIndexOf('.') + 1)..].Split('(')[0]))))));
            Artifact(index, $".{lane}{(run == 0 ? "" : $"-run-{run.ToString(CultureInfo.InvariantCulture)}")}.trx", xml.ToString());
        }

        internal JsonElement Json(int index)
        {
            using var document = JsonDocument.Parse(Run(index, "--json"));
            return document.RootElement.Clone();
        }

        internal string Run(int index, params string[] options)
        {
            var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
            var directories = Directory.GetDirectories(_root, "*", SearchOption.AllDirectories).Order().ToArray();
            var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? goal = null;
            string[] args = ["gate-stats", "failure", "--attempt", Id(index), .. options];
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
            Assert.Equal(directories, Directory.GetDirectories(_root, "*", SearchOption.AllDirectories).Order());
            foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
            return text;
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
