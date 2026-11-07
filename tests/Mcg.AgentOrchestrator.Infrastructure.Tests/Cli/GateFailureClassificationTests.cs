using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel safe: private artifact trees, no processes/stores, AsyncLocal console capture.
public sealed class GateFailureClassificationTests : CliTaskQueryTestSupport
{
    [Fact]
    public void RecordedCauses_RowsCountsRecurringTestsAndPresentationsAgree()
    {
        using var fixture = new Fixture();
        fixture.Result(1, new { kind = "fault", faultKind = "identity-stale" });
        fixture.Result(2, new { kind = "early-done", state = "Landed", earlyOutcomeKind = "skip-already-merged" });
        fixture.Result(3, new { kind = "fault", faultKind = "infrastructure-deferred", infrastructureReasonCode = "runner-unavailable" });
        fixture.Accepted(4, checks: ["infrastructure tests: Balanced 2", "infrastructure tests"]);
        fixture.Trx(4, "infrastructure-tests-balanced-2", 0, ("Denied", "Failed"));
        fixture.Trx(4, "infrastructure-tests-balanced-2", 1, ("Denied", "Passed"));
        fixture.Log(4, "LANE_RERUN lane=\"infrastructure tests: Balanced 2\" outcome=flake\n" +
            "LANE_RERUN_CREDIT_DENIED lane=\"infrastructure tests: Balanced 2\" reason=no-cross-goal-history\n");
        fixture.Accepted(5, origin: 1);
        fixture.Trx(5, "alpha", 0, ("Shared", "Failed"));
        fixture.Accepted(6);
        fixture.Trx(6, "alpha", 0, ("Unknown", "Failed"));
        fixture.Accepted(7, passed: true);
        fixture.Trx(7, "alpha", 0, ("Shared", "Failed"));
        fixture.Trx(7, "alpha", 1, ("Shared", "Passed"));

        var report = fixture.Report();
        Assert.Equal(7, report.GetProperty("attempts").GetInt32());
        var rows = Rows(report);
        Assert.Equal(6, rows.Length);
        var expected = new[] { "identity-stale", "early-outcome", "apparatus-or-environment", "flake-denied", "candidate", "unattributed" };
        for (var day = 1; day <= 6; day++)
        {
            var row = rows.Single(row => row.GetProperty("attemptId").GetString() == Fixture.Id(day));
            Assert.Equal(expected[day - 1], row.GetProperty("cause").GetString());
            Assert.Equal("12345678", row.GetProperty("goalPrefix").GetString());
            Assert.Equal(new DateTimeOffset(2026, 10, day, 0, 0, 0, TimeSpan.Zero), row.GetProperty("startedAt").GetDateTimeOffset());
            Assert.Equal(day <= 3 ? day == 2 ? "early-done" : "fault" : "accepted", row.GetProperty("resultKind").GetString());
            if (day != 6) Assert.NotEmpty(row.GetProperty("decisiveEvidence").EnumerateArray());
        }
        Assert.DoesNotContain(rows, row => row.GetProperty("attemptId").GetString() == Fixture.Id(7));
        var denied = rows.Single(row => row.GetProperty("cause").GetString() == "flake-denied");
        Assert.Equal(new[] { "infrastructure tests: Balanced 2", "infrastructure tests" },
            denied.GetProperty("failedChecks").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("no-cross-goal-history", Assert.Single(denied.GetProperty("denials").EnumerateArray()).GetProperty("reason").GetString());
        var test = Assert.Single(denied.GetProperty("tests").EnumerateArray());
        Assert.Equal("Namespace.Tests.Denied", test.GetProperty("test").GetString());
        Assert.Equal("Failed", test.GetProperty("firstRunOutcome").GetString());
        Assert.Equal("Passed", test.GetProperty("rerunOutcome").GetString());
        Assert.Contains("origin=2", rows.Single(row => row.GetProperty("cause").GetString() == "unattributed").GetProperty("evidenceGaps").ToString());
        var counts = report.GetProperty("causeCounts").EnumerateArray().ToArray();
        Assert.Equal(expected, counts.Select(count => count.GetProperty("cause").GetString()));
        Assert.All(counts, count => Assert.Equal(1, count.GetProperty("count").GetInt32()));
        Assert.Equal(rows.Length, counts.Sum(count => count.GetProperty("count").GetInt32()));
        var recurring = Assert.Single(report.GetProperty("recurringTests").EnumerateArray());
        Assert.Equal("Namespace.Tests.Shared", recurring.GetProperty("test").GetString());
        Assert.Equal(2, recurring.GetProperty("attempts").GetInt32());
    }

    [Fact]
    public void MissingMalformedAndLockedResults_DoNotAbortOtherAttempts()
    {
        using var fixture = new Fixture();
        fixture.Artifact(1, ".attempt.json", "{}");
        fixture.Artifact(2, ".result.json", "{broken");
        fixture.Result(3, new { kind = "fault", faultKind = "blocked-build-slot" });
        var report = fixture.Report();
        Assert.Equal(3, Rows(report).Length);
        foreach (var day in new[] { 1, 2 })
        {
            var row = Rows(report).Single(row => row.GetProperty("attemptId").GetString() == Fixture.Id(day));
            Assert.Equal("unattributed", row.GetProperty("cause").GetString());
            Assert.Contains("result.json", row.GetProperty("evidenceGaps").ToString());
            Assert.Contains(day == 1 ? "FileNotFoundException" : "malformed JSON", row.GetProperty("evidenceGaps").ToString());
        }
        Assert.Equal("apparatus-or-environment", Rows(report)[0].GetProperty("cause").GetString());
        // The lock is the input, not a timing race. Release it before the no-write snapshot.
        fixture.Result(4, new { kind = "fault", faultKind = "identity-stale" });
        using (var locked = new FileStream(fixture.PathFor(4, ".result.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using var document = JsonDocument.Parse(fixture.RunJsonWithoutSnapshot());
            var row = Rows(document.RootElement).Single(row => row.GetProperty("attemptId").GetString() == Fixture.Id(4));
            Assert.Equal("unattributed", row.GetProperty("cause").GetString());
            Assert.Contains("IOException", row.GetProperty("evidenceGaps").ToString());
        }
    }

    [Theory]
    [InlineData("identity-stale", "identity-stale")]
    [InlineData("blocked-build-slot", "apparatus-or-environment")]
    [InlineData("blocked-build-lock", "apparatus-or-environment")]
    [InlineData("structural-coverage-permit-unavailable", "apparatus-or-environment")]
    [InlineData("infrastructure-deferred", "apparatus-or-environment")]
    [InlineData("gate-engine-fault", "apparatus-or-environment")]
    [InlineData("cancelled", "unattributed")]
    [InlineData("exception", "unattributed")]
    [InlineData("future-fault", "unattributed")]
    public void FaultKinds_RequirePositiveRecordedEvidence(string fault, string expected)
    {
        using var fixture = new Fixture();
        fixture.Result(1, new { kind = "fault", faultKind = fault });
        Assert.Equal(expected, Assert.Single(Rows(fixture.Report())).GetProperty("cause").GetString());
    }

    [Theory]
    [InlineData(0, 0, "unattributed")]
    [InlineData(1, 0, "candidate")]
    [InlineData(2, 0, "unattributed")]
    [InlineData(1, 1, "apparatus-or-environment")]
    [InlineData(2, 1, "apparatus-or-environment")]
    [InlineData(2, 2, "unattributed")]
    [InlineData(42, 0, "unattributed")]
    [InlineData(1, 42, "unattributed")]
    public void Attributions_UseEnumMeaningsAndEnvironmentalPrecedence(int origin, int cause, string expected)
    {
        using var fixture = new Fixture();
        fixture.Accepted(1, origin, cause);
        fixture.Trx(1, "alpha", 0, ("Failure", "Failed"));
        var row = Assert.Single(Rows(fixture.Report()));
        Assert.Equal(expected, row.GetProperty("cause").GetString());
        Assert.Contains("recorded fixture evidence", row.ToString());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void DeniedCredit_RequiresPassingRerunAndDenialForEveryLane(bool pass, bool deny)
    {
        using var fixture = new Fixture();
        fixture.Accepted(1, checks: ["alpha", "beta"]);
        fixture.Trx(1, "alpha", 0, ("Alpha", "Failed"));
        fixture.Trx(1, "alpha", 1, ("Alpha", "Passed"));
        fixture.Trx(1, "beta", 0, ("Beta", "Failed"));
        fixture.Trx(1, "beta", 1, ("Beta", pass ? "Passed" : "Failed"));
        fixture.Log(1, "LANE_RERUN_CREDIT_DENIED lane=\"alpha\" reason=no-cross-goal-history\n" +
            (deny ? "LANE_RERUN_CREDIT_DENIED lane=\"beta\" reason=not-eligible\n" : ""));
        Assert.Equal("unattributed", Assert.Single(Rows(fixture.Report())).GetProperty("cause").GetString());
    }

    [Fact]
    public void LaneLog_DoesNotInventIndividualRerunOutcomes()
    {
        using var fixture = new Fixture();
        fixture.Accepted(1);
        fixture.Trx(1, "alpha", 0, ("Failure", "Failed"));
        fixture.Log(1, "LANE_RERUN lane=\"alpha\" outcome=flake\nLANE_RERUN_CREDIT_DENIED lane=\"alpha\" reason=no-cross-goal-history\n");
        var row = Assert.Single(Rows(fixture.Report()));
        Assert.Equal("flake-denied", row.GetProperty("cause").GetString());
        Assert.Equal("not-recorded", Assert.Single(row.GetProperty("tests").EnumerateArray()).GetProperty("rerunOutcome").GetString());
    }

    [Theory]
    [InlineData("Failed", "flake", "unattributed")]
    [InlineData("Passed", "confirmed-failure", "unattributed")]
    [InlineData("NotExecuted", "flake", "unattributed")]
    [InlineData("Passed", "flake", "flake-denied")]
    public void RerunEvidence_RejectsConflictsAndUsesLatestRun(string outcome, string logged, string expected)
    {
        using var fixture = new Fixture();
        fixture.Accepted(1);
        fixture.Trx(1, "alpha", 0, ("Failure", "Failed"));
        fixture.Trx(1, "alpha", 1, ("Failure", "Passed"));
        fixture.Trx(1, "alpha", 2, ("Failure", outcome));
        fixture.Log(1, $"LANE_RERUN lane=\"alpha\" outcome={logged}\nLANE_RERUN_CREDIT_DENIED lane=\"alpha\" reason=no-cross-goal-history\n");
        Assert.Equal(expected, Assert.Single(Rows(fixture.Report())).GetProperty("cause").GetString());
    }

    [Theory]
    [InlineData("early-held")]
    [InlineData("early-escalated")]
    [InlineData("early-done")]
    [InlineData("early-executed")]
    public void EarlyKinds_PrecedeApparatusEvidenceWithoutRequiringTestArtifacts(string kind)
    {
        using var fixture = new Fixture();
        fixture.Result(1, new { kind, state = "AcceptancePending", infrastructureReasonCode = "unavailable" });
        var row = Assert.Single(Rows(fixture.Report()));
        Assert.Equal("early-outcome", row.GetProperty("cause").GetString());
        Assert.Empty(row.GetProperty("evidenceGaps").EnumerateArray());
    }

    [Theory]
    [InlineData("Error")]
    [InlineData("Timeout")]
    [InlineData("Aborted")]
    [InlineData("NotRunnable")]
    public void FatalOutcomes_AreIncludedInFailureAndRecurrenceEvidence(string outcome)
    {
        using var fixture = new Fixture();
        foreach (var day in new[] { 1, 2 })
        {
            fixture.Accepted(day);
            fixture.Trx(day, "alpha", 0, ("Failure", outcome));
        }
        var report = fixture.Report();
        Assert.All(Rows(report), row => Assert.Equal(outcome, Assert.Single(row.GetProperty("tests").EnumerateArray()).GetProperty("firstRunOutcome").GetString()));
        Assert.Equal(2, Assert.Single(report.GetProperty("recurringTests").EnumerateArray()).GetProperty("attempts").GetInt32());
    }

    [Fact]
    public void MixedAttributions_DoNotCallTheWholeAttemptEnvironmental()
    {
        using var fixture = new Fixture();
        fixture.Result(1, new { kind = "accepted", acceptance = new { passed = false, failedChecks = new[] { "alpha", "beta" },
            checkAttributions = new[] { new { checkName = "alpha", origin = 2, cause = 1, evidence = "environment" },
                new { checkName = "beta", origin = 1, cause = 0, evidence = "candidate" } } } });
        fixture.Log(1, "");
        fixture.Trx(1, "alpha", 0, ("Failure", "Failed"));
        var row = Assert.Single(Rows(fixture.Report()));
        Assert.Equal("candidate", row.GetProperty("cause").GetString());
        Assert.Contains("checkName=beta", row.GetProperty("decisiveEvidence").ToString());
    }

    [Theory]
    [InlineData(".alpha.trx", "<broken", "TRX")]
    [InlineData(".out.log", null, "out.log")]
    public void RequiredArtifactProblems_AreUnattributed(string suffix, string? contents, string gap)
    {
        using var fixture = new Fixture();
        fixture.Accepted(1, origin: 1);
        fixture.Trx(1, "alpha", 0, ("Failure", "Failed"));
        if (contents is null) File.Delete(fixture.PathFor(1, suffix));
        else fixture.Artifact(1, suffix, contents);
        var row = Assert.Single(Rows(fixture.Report()));
        Assert.Equal("unattributed", row.GetProperty("cause").GetString());
        Assert.Contains(gap, row.GetProperty("evidenceGaps").ToString());
    }

    [Fact]
    public void Window_AppliesLimitBeforeExcludingPassesAndFocusedEvidence()
    {
        using var fixture = new Fixture();
        for (var day = 1; day <= 22; day++)
            fixture.Result(day, new { kind = "fault", faultKind = "exception" });
        fixture.Accepted(22, passed: true);
        fixture.Result(21, new { kind = "focused-evidence" });
        fixture.Artifact(20, ".attempt.json", "{\"kind\":\"pre-review-evidence\"}");
        var report = fixture.Report();
        Assert.Equal(17, Rows(report).Length); // 20 selected, less pass and two exclusions.
        Assert.Equal(2, report.GetProperty("excluded").GetArrayLength());
        Assert.DoesNotContain(Rows(report), row => row.GetProperty("attemptId").GetString() == Fixture.Id(2));
        Assert.Empty(Rows(fixture.Report("--last", "1")));
        Assert.Equal(2, Rows(fixture.Report("--last", "5")).Length);
    }

    [Fact]
    public void ResultDispatchKind_ExcludesPreReviewFaultsFromHomeRows()
    {
        using var fixture = new Fixture();
        fixture.Result(1, new { kind = "fault", faultKind = "identity-stale", dispatchKind = "pre-review-evidence" });
        var report = fixture.Report();
        Assert.Empty(Rows(report));
        Assert.Contains("dispatchKind=pre-review-evidence", Assert.Single(report.GetProperty("excluded").EnumerateArray()).GetString());
    }

    [Fact]
    public void TestIdentity_PreservesTheoryCasesAndCountsEachAttemptOnce()
    {
        using var fixture = new Fixture();
        foreach (var day in new[] { 1, 2 })
        {
            fixture.Accepted(day);
            fixture.Trx(day, "alpha", 0, ("Theory(value: 1)", "Failed"), ("Theory(value: 2)", "Passed"));
            fixture.Trx(day, "alpha", 1, ("Theory(value: 1)", "Failed"), ("Theory(value: 2)", "Failed"));
            fixture.Trx(day, "alpha", 2, ("Theory(value: 1)", "Passed"), ("Theory(value: 2)", "Passed"));
        }
        var report = fixture.Report();
        Assert.Equal(2, report.GetProperty("recurringTests").GetArrayLength());
        Assert.All(report.GetProperty("recurringTests").EnumerateArray(), test => Assert.Equal(2, test.GetProperty("attempts").GetInt32()));
        var tests = Rows(report)[0].GetProperty("tests").EnumerateArray().ToArray();
        Assert.Equal(2, tests.Length);
        Assert.Equal("Passed", tests.Single(test => test.GetProperty("test").GetString()!.EndsWith("(value: 2)")).GetProperty("firstRunOutcome").GetString());
        Assert.All(tests, test => Assert.Equal(2, test.GetProperty("reruns").GetArrayLength()));
    }

    [Fact]
    public void DiscoveryFailure_IsReportedRatherThanAnEmptySuccessfulCensus()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Workspace.OrchestratorDirectory);
        File.WriteAllText(Path.Combine(fixture.Workspace.OrchestratorDirectory, "acceptance-gate-attempts"), "not a directory");
        Assert.NotEmpty(fixture.Report().GetProperty("readProblems").EnumerateArray());
    }

    [Theory]
    [InlineData("--last", "0")]
    [InlineData("--last", "NaN")]
    [InlineData("--top", "1")]
    [InlineData("--lane", "alpha")]
    [InlineData("--json", "--json")]
    public void Arguments_RejectUnsupportedOrInvalidOptions(string first, string second)
    {
        using var fixture = new Fixture();
        var error = Assert.Throws<ArgumentException>(() => CliGateStatsQueryCommand.Execute(["gate-stats", "failures", first, second], fixture.Workspace));
        Assert.Equal(CliCommandHelp.GateStatsUsage, error.Message);
    }

    [Fact]
    public void EmptyWindow_HasAllZeroCountsAndHelpDocumentsTheForm()
    {
        using var fixture = new Fixture();
        var report = fixture.Report();
        Assert.Empty(Rows(report));
        Assert.Equal(6, report.GetProperty("causeCounts").GetArrayLength());
        Assert.All(report.GetProperty("causeCounts").EnumerateArray(), count => Assert.Equal(0, count.GetProperty("count").GetInt32()));
        Assert.Contains("gate-stats failures [--last <n>] [--json]", CliCommandHelp.GateStatsUsage);
    }

    private static JsonElement[] Rows(JsonElement report) => report.GetProperty("rows").EnumerateArray().ToArray();

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = CreateTempDirectory();
        internal OrchestratorWorkspace Workspace => OrchestratorWorkspace.ForDirectory(_root);
        internal static string Id(int day) => $"12345678-0-202610{day:00}000000000-00000000000000000000000000000000";
        internal string PathFor(int day, string suffix) => Path.Combine(Workspace.OrchestratorDirectory, "acceptance-gate-attempts", "123456789abcdef", Id(day) + suffix);

        internal void Artifact(int day, string suffix, string contents)
        {
            var path = PathFor(day, suffix);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }
        internal void Result(int day, object value) => Artifact(day, ".result.json", JsonSerializer.Serialize(value));
        internal void Log(int day, string value) => Artifact(day, ".out.log", value);
        internal void Accepted(int day, int origin = 2, int cause = 0, string[]? checks = null, bool passed = false)
        {
            checks ??= ["alpha"];
            Result(day, new { kind = "accepted", acceptance = new { passed, failedChecks = passed ? [] : checks,
                checkAttributions = checks.Select(check => new { checkName = check, origin, cause, evidence = "recorded fixture evidence" }).ToArray() } });
            Log(day, "");
        }

        internal void Trx(int day, string lane, int run, params (string Name, string Outcome)[] tests)
        {
            var suffix = run == 0 ? "" : $"-run-{run.ToString(CultureInfo.InvariantCulture)}";
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            var xml = new XDocument(new XElement(ns + "TestRun", new XElement(ns + "Results", tests.Select((test, index) =>
                new XElement(ns + "UnitTestResult", new XAttribute("testId", index), new XAttribute("testName", $"Namespace.Tests.{test.Name}"),
                    new XAttribute("outcome", test.Outcome), new XAttribute("duration", "00:00:01")))),
                new XElement(ns + "TestDefinitions", tests.Select((test, index) => new XElement(ns + "UnitTest", new XAttribute("id", index),
                    new XElement(ns + "TestMethod", new XAttribute("className", "Namespace.Tests"), new XAttribute("name", test.Name.Split('(')[0])))))));
            Artifact(day, $".{lane}{suffix}.trx", xml.ToString());
        }

        internal JsonElement Report(params string[] args)
        {
            var table = Run(["gate-stats", "failures", .. args]);
            using var json = JsonDocument.Parse(Run(["gate-stats", "failures", .. args, "--json"]));
            var report = json.RootElement;
            var expected = new List<string> { $"form=failures\tattempts={report.GetProperty("attempts").GetInt32()}" };
            foreach (var name in new[] { "rows", "causeCounts", "recurringTests" })
                foreach (var row in report.GetProperty(name).EnumerateArray())
                    expected.Add(string.Join('\t', row.EnumerateObject().Select(property =>
                        $"{property.Name}={Cell(property.Value.ValueKind == JsonValueKind.Null ? "-" : property.Value.ToString())}")));
            foreach (var name in new[] { "readProblems", "excluded" })
                foreach (var problem in report.GetProperty(name).EnumerateArray()) expected.Add($"{name}={Cell(problem.GetString()!)}");
            expected.Add($"skipped={report.GetProperty("skipped").GetInt32()}");
            Assert.Equal(expected, table.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
            return report.Clone();
        }

        internal string RunJsonWithoutSnapshot() => CaptureConsole(() => CliGateStatsQueryCommand.Execute(["gate-stats", "failures", "--json"], Workspace));

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
        private static string Cell(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
