using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: private artifact trees, explicit observation clock, no processes or live stores.
public sealed class GateFailureClassificationInProcessAttemptTests : CliTaskQueryTestSupport
{
    [Fact]
    public void ResultBearingKinds_SameEvidence_HaveIdenticalClassificationFields()
    {
        using var fixture = new Fixture();
        var parallel = fixture.Id("0");
        var inProcess = fixture.Id("gate");
        foreach (var id in new[] { parallel, inProcess })
        {
            fixture.Accepted(id);
            fixture.Trx(id, "alpha", "Failed");
            fixture.Trx(id, "alpha-run-1", "Passed");
            fixture.Artifact(id, ".out.log", "LANE_RERUN lane=\"alpha\" outcome=flake\n" +
                "LANE_RERUN_CREDIT_DENIED lane=\"alpha\" reason=no-cross-goal-history\n");
        }

        var rows = fixture.CliReport().GetProperty("rows").EnumerateArray().ToArray();

        Assert.Equal(2, rows.Length);
        var expected = rows.Single(row => row.GetProperty("attemptId").GetString() == parallel);
        var actual = rows.Single(row => row.GetProperty("attemptId").GetString() == inProcess);
        Assert.Equal("flake-denied", actual.GetProperty("cause").GetString());
        Assert.Equal(expected.EnumerateObject().Select(property => property.Name),
            actual.EnumerateObject().Select(property => property.Name));
        foreach (var field in expected.EnumerateObject().Where(property => property.Name is not ("attemptId" or "startedAt" or "decisiveEvidence")))
            Assert.Equal(field.Value.GetRawText(), actual.GetProperty(field.Name).GetRawText());
        // TRX references include the attempt id; their content must otherwise match too.
        Assert.Equal(expected.GetProperty("decisiveEvidence").GetRawText().Replace(parallel, inProcess),
            actual.GetProperty("decisiveEvidence").GetRawText());
    }

    [Theory]
    [InlineData("12345678")]
    [InlineData("operator")]
    public void OwnerMintedRecord_NoWrapperLog_ClassifiesWithoutLogGap(string owner)
    {
        using var fixture = new Fixture();
        var id = fixture.Id("gate", owner);
        fixture.Accepted(id);
        fixture.Trx(id, "alpha", "Failed");

        var row = Assert.Single(GateFailureClassificationReport.Read(fixture.Directory, 40).Rows);

        Assert.Equal("accepted", row.ResultKind);
        Assert.Equal("candidate", row.Cause);
        Assert.Empty(row.EvidenceGaps);
        Assert.Equal("Failed", Assert.Single(row.Tests).FirstRunOutcome);
    }

    [Fact]
    public void ParallelFixture_AllFieldsAndCauseCounts_RetainRecordedSnapshot()
    {
        using var fixture = new Fixture();
        var id = fixture.Id("0");
        fixture.Accepted(id);
        fixture.Trx(id, "alpha", "Failed");
        fixture.Artifact(id, ".out.log", "");

        var report = fixture.CliReport();
        var actual = Assert.Single(report.GetProperty("rows").EnumerateArray());
        var expected = JsonSerializer.SerializeToElement(new
        {
            attemptId = id, goalPrefix = "12345678", startedAt = Fixture.Start,
            resultKind = "accepted", cause = "candidate", failedChecks = new[] { "alpha" },
            tests = new[] { new { lane = "alpha", test = "Namespace.Tests.Fixture", firstRunOutcome = "Failed",
                rerunOutcome = "not-recorded", reruns = Array.Empty<object>() } },
            denials = Array.Empty<object>(),
            decisiveEvidence = new[] { "checkName=alpha; origin=1; cause=0; evidence=recorded fixture evidence" },
            evidenceGaps = Array.Empty<string>()
        });
        // Apply the same JSON string escaping to both snapshots, including the UTC offset's '+'.
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
        Assert.Equal(1, report.GetProperty("attempts").GetInt32());
        var counts = report.GetProperty("causeCounts").EnumerateArray().ToArray();
        Assert.Equal(new[] { "identity-stale", "early-outcome", "apparatus-or-environment", "flake-denied", "candidate", "unattributed" },
            counts.Select(count => count.GetProperty("cause").GetString()));
        Assert.Equal(new[] { 0, 0, 0, 0, 1, 0 }, counts.Select(count => count.GetProperty("count").GetInt32()));
        Assert.Empty(report.GetProperty("readProblems").EnumerateArray());
        Assert.Empty(report.GetProperty("excluded").EnumerateArray());
        Assert.Equal(0, report.GetProperty("skipped").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteTrx_StaleHeartbeat_ReportsFinishedWithoutRecord(bool hasMetadata)
    {
        using var fixture = new Fixture();
        var id = fixture.Id("gate", "operator");
        fixture.Plan(id);
        fixture.Trx(id, "infrastructure-tests-alpha", "Failed");
        fixture.Trx(id, "infrastructure-tests-beta", "Passed");
        fixture.Heartbeat(id, Fixture.Now.AddDays(-1));
        if (hasMetadata) fixture.Artifact(id, ".attempt.json", "{}");

        var attempt = fixture.Read(id);

        Assert.Contains(attempt.ResultProblems, problem => problem.StartsWith("finished-without-result-record:"));
        Assert.DoesNotContain(attempt.Problems.Concat(attempt.ResultProblems), problem => problem.Contains("may still be running"));
        if (!hasMetadata) Assert.DoesNotContain(attempt.ResultProblems, problem => problem.Contains("FileNotFoundException"));
        Assert.False(File.Exists(fixture.Prefix(id) + ".result.json")); // observation never writes
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingScheduledTrx_HeartbeatEitherAge_ReportsPossiblyRunning(bool fresh)
    {
        using var fixture = new Fixture();
        var id = fixture.Id("gate");
        fixture.Plan(id);
        fixture.Trx(id, "infrastructure-tests-alpha", "Failed");
        // A similarly named lane cannot substitute for beta's exact TRX.
        fixture.Trx(id, "infrastructure-tests-beta-extra", "Passed");
        fixture.Heartbeat(id, fresh ? Fixture.Now : Fixture.Now.AddDays(-1));

        Assert.Equal("possibly-running: scheduled lane TRX is missing or incomplete; attempt may still be running",
            Assert.Single(fixture.Read(id).ResultProblems));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidFirstRun_StaleHeartbeat_DoesNotEstablishCompletion(bool rerunOnly)
    {
        using var fixture = new Fixture();
        var id = fixture.Id("gate");
        fixture.Plan(id);
        fixture.Trx(id, "infrastructure-tests-alpha", "Passed");
        if (rerunOnly) fixture.Trx(id, "infrastructure-tests-beta-run-1", "Passed");
        else fixture.Artifact(id, ".infrastructure-tests-beta.trx", "<malformed");
        fixture.Heartbeat(id, Fixture.Now.AddDays(-1));

        Assert.StartsWith("possibly-running:", Assert.Single(fixture.Read(id).ResultProblems));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void CompleteTrx_HeartbeatBoundary_UsesExplicitObservationClock(int ticksPast, bool fresh)
    {
        using var fixture = new Fixture();
        var id = fixture.Id("gate");
        fixture.Plan(id);
        fixture.Trx(id, "infrastructure-tests-alpha", "Passed");
        fixture.Trx(id, "infrastructure-tests-beta", "Passed");
        fixture.Heartbeat(id, Fixture.Now - GateLoadContextProbe.LiveGateHeartbeatFreshness - TimeSpan.FromTicks(ticksPast));

        var reason = Assert.Single(fixture.Read(id).ResultProblems);

        Assert.StartsWith(fresh ? "possibly-running:" : "finished-without-result-record:", reason);
        Assert.DoesNotContain("may still be running", reason);
        if (fresh) Assert.Contains("finalizing", reason);
    }

    [Fact]
    public void NoLanePlan_StaleHeartbeat_DoesNotInferRunningFromMissingRecord()
    {
        using var fixture = new Fixture();
        var id = fixture.Id("gate");
        fixture.Trx(id, "alpha", "Passed");
        fixture.Heartbeat(id, Fixture.Now.AddDays(-1));

        var reason = Assert.Single(fixture.Read(id).ResultProblems);

        Assert.StartsWith("completion-unknown:", reason);
        Assert.DoesNotContain("may still be running", reason);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(40)]
    public void MixedKinds_IncludingPassingAttempt_SelectSameIdsAsWaits(int last)
    {
        using var fixture = new Fixture();
        var passing = fixture.Id("gate");
        fixture.Accepted(passing, passed: true);
        fixture.Trx(passing, "alpha", "Passed");
        var failed = fixture.Id("0");
        fixture.Accepted(failed);
        fixture.Trx(failed, "alpha", "Failed");
        fixture.Artifact(failed, ".out.log", "");
        var historical = fixture.Id("gate", "operator");
        fixture.Plan(historical);
        fixture.Trx(historical, "infrastructure-tests-alpha", "Failed");
        fixture.Trx(historical, "infrastructure-tests-beta", "Passed");

        var problems = new List<string>();
        var failures = GateFailureClassificationReader.Read(fixture.Directory, last, problems, out var skipped, Fixture.Now);
        var waits = GateStatsWaitsReader.Read(fixture.Directory, last, out var waitSkipped);

        Assert.Equal(Math.Min(last, 3), failures.Count);
        Assert.Empty(problems);
        Assert.Equal(0, skipped);
        Assert.Equal(0, waitSkipped);
        Assert.Equal(waits.Select(row => row.Id).Order(StringComparer.Ordinal), failures.Select(row => row.Id).Order(StringComparer.Ordinal));
        if (last > 1) Assert.Contains(failures, attempt => attempt.Id == passing && attempt.Passed == true);
    }

    private sealed class Fixture : IDisposable
    {
        internal static DateTimeOffset Start => new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        internal static DateTimeOffset Now => Start.AddDays(1);
        private readonly string _root = CreateTempDirectory();
        internal string Directory => Workspace.OrchestratorDirectory;
        private OrchestratorWorkspace Workspace => OrchestratorWorkspace.ForDirectory(_root);
        internal string Id(string kind, string owner = "12345678") =>
            $"{owner}-{kind}-20261001000000000-00000000000000000000000000000000";
        internal string Prefix(string id) => Path.Combine(Directory, "acceptance-gate-attempts", "fixture", id);
        internal void Artifact(string id, string suffix, string value)
        {
            var path = Prefix(id) + suffix;
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, value);
        }
        internal void Accepted(string id, bool passed = false) => Artifact(id, ".result.json", JsonSerializer.Serialize(new
        {
            kind = "accepted", dispatchKind = "gate", acceptance = new
            {
                passed, failedChecks = passed ? Array.Empty<string>() : new[] { "alpha" },
                checkAttributions = new[] { new { checkName = "alpha", origin = 1, cause = 0, evidence = "recorded fixture evidence" } }
            }
        }));
        internal void Plan(string id) => Artifact(id, ".lane-plan.json",
            new AcceptanceGateLanePlan(AcceptanceGateLanePlan.CurrentSchemaVersion, id, ["alpha", "beta"], []).ToJson());
        internal void Heartbeat(string id, DateTimeOffset observed) =>
            Artifact(id, ".alpha-deadbeef.gate-heartbeat.json", JsonSerializer.Serialize(new { lastObservedAt = observed }));
        internal void Trx(string id, string lane, string outcome)
        {
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            var xml = new XDocument(new XElement(ns + "TestRun", new XElement(ns + "Results",
                new XElement(ns + "UnitTestResult", new XAttribute("testId", "1"),
                    new XAttribute("testName", "Namespace.Tests.Fixture"), new XAttribute("outcome", outcome),
                    new XAttribute("duration", "00:00:01"))),
                new XElement(ns + "TestDefinitions", new XElement(ns + "UnitTest", new XAttribute("id", "1"),
                    new XElement(ns + "TestMethod", new XAttribute("className", "Namespace.Tests"), new XAttribute("name", "Fixture"))))));
            Artifact(id, $".{lane}.trx", xml.ToString());
        }
        internal GateFailureClassificationReader.Attempt Read(string id)
        {
            var problems = new List<string>();
            var attempts = GateFailureClassificationReader.Read(Directory, 40, problems, out var skipped, Now);
            Assert.Empty(problems);
            Assert.Equal(0, skipped);
            return Assert.Single(attempts, attempt => attempt.Id == id);
        }
        internal JsonElement CliReport()
        {
            var files = System.IO.Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
                .ToDictionary(path => path, File.ReadAllText);
            using var document = JsonDocument.Parse(AsyncLocalConsoleRouter.Capture(() =>
                CliGateStatsQueryCommand.Execute(["gate-stats", "failures", "--json"], Workspace)));
            Assert.Equal(files.Keys.Order(StringComparer.Ordinal),
                System.IO.Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
            foreach (var (path, value) in files) Assert.Equal(value, File.ReadAllText(path));
            return document.RootElement.Clone();
        }
        public void Dispose() => System.IO.Directory.Delete(_root, recursive: true);
    }
}
