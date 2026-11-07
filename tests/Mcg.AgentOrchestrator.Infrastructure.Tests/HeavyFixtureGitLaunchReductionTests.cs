using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private receipt files, no git, test-body invocation or process-wide state changes.
public sealed class HeavyFixtureGitLaunchReductionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "heavy-fixture-bound-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Baselines_CurrentDiscovery_PinsSelectionWithoutExecutingBodies()
    {
        Type[] classes = [typeof(AcceptanceCohortWorkflowTestsTrainRedAttribution),
            typeof(MergeTrainFocusedRecheckReceiptAcceptanceTests), typeof(AcceptanceCohortWorkflowTestsMergeTrain),
            typeof(PullRequestTrunkDriftTests), typeof(CliCommandTestsTerminalSweepCommands)];
        Assert.Equal(classes.Select(type => type.Name), HeavyFixtureGitLaunchBoundCheck.Baselines.Select(entry => entry.ClassName));
        foreach (var entry in HeavyFixtureGitLaunchBoundCheck.Baselines)
        {
            var type = Assert.Single(classes, candidate => candidate.Name == entry.ClassName);
            var rows = 0;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                var fact = method.GetCustomAttribute<FactAttribute>(inherit: true);
                if (fact is null) continue;
                Assert.Null(fact.Skip);
                var inline = method.GetCustomAttributesData().Count(attribute => attribute.AttributeType == typeof(InlineDataAttribute));
                Assert.False(fact is TheoryAttribute && inline == 0,
                    $"The launch census must explicitly support data discovery for {type.Name}.{method.Name}.");
                rows += Math.Max(1, inline);
            }
            Assert.Equal(entry.Rows, rows); // A smaller selection must never masquerade as savings.
        }
        Assert.Equal(169, HeavyFixtureGitLaunchBoundCheck.Baselines.Sum(entry => entry.Rows));
        Assert.Equal(5810, HeavyFixtureGitLaunchBoundCheck.Baselines.Sum(entry => entry.Launches));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(4357, true)]
    [InlineData(4358, false)]
    public void Evaluate_CompleteCoverage_EnforcesHistoricalBound(int launches, bool passed)
    {
        var result = Evaluate(Write("complete", Rows(), launches));
        Assert.Equal(passed, result.Passed);
        Assert.False(result.Advisory);
        Assert.Equal(passed ? 0 : 1, result.ExitCode);
        Assert.StartsWith(passed ? "status=passed" : "status=failed", result.ResultSummary);
        Assert.Contains($"launches={launches}; historical=5810; maximum=4357; rows=169", result.ResultSummary);
        foreach (var baseline in HeavyFixtureGitLaunchBoundCheck.Baselines)
            Assert.Contains(baseline.ClassName, result.OutputTail);
    }

    [Fact]
    public void Evaluate_MissingClass_SkipsEvenAboveBound()
    {
        var missing = HeavyFixtureGitLaunchBoundCheck.Baselines[^1];
        var result = Evaluate(Write("four-classes", Rows().Where(row => !row.StartsWith(missing.ClassName + ".", StringComparison.Ordinal)), 9000));
        AssertSkipped(result, missing.ClassName);
        Assert.Contains($"{missing.ClassName} (0/{missing.Rows})", result.ResultSummary);
    }

    [Fact]
    public void Evaluate_DuplicateRows_CannotReplaceMissingCoverage()
    {
        var rows = Rows().ToArray();
        var result = Evaluate(Write("duplicate", rows.Skip(1).Append(rows[1]), 0));
        AssertSkipped(result, "missing rows");
        Assert.Contains("rows=168", result.ResultSummary);
    }

    [Fact]
    public void Evaluate_ExtraDistinctRow_FailsSelectionDrift()
    {
        var result = Evaluate(Write("excess", Rows().Append(Rows().First().Replace("row: 0", "row: 999")), 0));
        AssertMalformed(result, "excess distinct rows");
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("empty")]
    [InlineData("missing-field")]
    [InlineData("missing-row")]
    [InlineData("wrong-method")]
    public void Evaluate_IncompleteCensus_NeverPasses(string defect)
    {
        var path = Write("incomplete", Rows(), 0);
        var sidecar = LaneSlotTimingReader.PathFor(path);
        var lines = File.ReadAllLines(sidecar);
        switch (defect)
        {
            case "absent": File.Delete(sidecar); break;
            case "empty": File.WriteAllText(sidecar, ""); break;
            case "missing-field": lines[0] = lines[0].Replace(",\"git_launches\":0", ""); break;
            case "missing-row": lines = lines.Skip(1).ToArray(); break;
            case "wrong-method": lines[0] = lines[0].Replace(".Scenario", ".Other"); break;
        }
        if (defect is not "absent" and not "empty") File.WriteAllLines(sidecar, lines);
        AssertSkipped(Evaluate(path), defect == "absent" ? "unreadable slot timing" : "missing");
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"bad\"")]
    [InlineData("null")]
    [InlineData("9223372036854775808")]
    public void Evaluate_InvalidLaunchCount_FailsMeasurement(string value)
    {
        var path = Write("invalid-count", Rows(), 0);
        var sidecar = LaneSlotTimingReader.PathFor(path);
        var lines = File.ReadAllLines(sidecar);
        lines[0] = lines[0].Replace("\"git_launches\":0", "\"git_launches\":" + value);
        File.WriteAllLines(sidecar, lines);
        AssertMalformed(Evaluate(path), "invalid git_launches");
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("null")]
    public void Evaluate_MalformedRelevantSidecar_FailsMeasurement(string invalid)
    {
        var path = Write("malformed", Rows(), 0);
        File.AppendAllText(LaneSlotTimingReader.PathFor(path), invalid + Environment.NewLine);
        AssertMalformed(Evaluate(path), "malformed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Evaluate_Overflow_FailsMeasurement(bool acrossClasses)
    {
        var path = Write("overflow", Rows(), long.MaxValue);
        File.AppendAllText(LaneSlotTimingReader.PathFor(path), TimingRow(acrossClasses ? Rows().Last() : Rows().First(), 1)
            + Environment.NewLine);
        AssertMalformed(Evaluate(path), acrossClasses ? "aggregate launch count overflow" : "OverflowException");
    }

    [Fact]
    public void Evaluate_Reruns_CountsEveryAttemptAndDeduplicatesReceiptPaths()
    {
        var first = Write("first", Rows(), 4000);
        var rerun = Write("rerun", Rows().Take(1), 200);
        var within = Write("within", Rows().Take(1), 158);
        var check = Check(first) with
        {
            LaneRerun = new("first", [first], [], "failing-trx", "rerun", [rerun], [], null, "flake"),
            WithinAttemptRerun = new("partition", "failing-trx", "first", "within", 1, [within])
        };
        var result = Assert.IsType<AcceptanceCheckResult>(HeavyFixtureGitLaunchBoundCheck.Evaluate([check, Check(rerun)]));
        Assert.False(result.Passed);
        Assert.False(result.Advisory);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("launches=4358", result.ResultSummary);
        Assert.Contains("rows=169", result.ResultSummary);
    }

    [Fact]
    public void Evaluate_RetryOfIncompleteSelection_CannotFillCoverageGap()
    {
        var first = Write("partial", Rows().Skip(1), 0);
        var retry = Write("partial-retry", Rows().Skip(1), 0);
        var result = Assert.IsType<AcceptanceCheckResult>(HeavyFixtureGitLaunchBoundCheck.Evaluate([Check(first), Check(retry)]));
        AssertSkipped(result, "missing rows");
        Assert.Contains("rows=168", result.ResultSummary);
    }

    [Fact]
    public void Evaluate_ReusedPriorGate_CannotSupplyCoverageOrLaunches()
    {
        var current = Write("current", Rows().Skip(1), 0);
        var prior = Write("prior", Rows(), 9000);
        var result = Assert.IsType<AcceptanceCheckResult>(HeavyFixtureGitLaunchBoundCheck.Evaluate(
            [Check(current), Check(prior) with { TestResultIsExplicitCrossAttemptReuse = true }]));
        AssertSkipped(result, "missing rows");
        Assert.Contains("launches=0", result.ResultSummary);
    }

    [Fact]
    public void Evaluate_Crosswalk_UsesNormalizedTrx()
    {
        var path = Write("normalized", Rows(), 4357);
        var check = Check(Path.Combine(root, "native.json")) with
        {
            CrosswalkDiagnostics = [], NormalizedTestResultPaths = [path]
        };
        var result = Assert.IsType<AcceptanceCheckResult>(HeavyFixtureGitLaunchBoundCheck.Evaluate([check]));
        Assert.True(result.Passed);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unreadable")]
    public void Evaluate_UnreadableTrx_SkipsCompleteOtherEvidence(string defect)
    {
        var good = Write("good", Rows(), 0);
        var broken = Path.Combine(root, "broken.trx");
        if (defect == "unreadable") File.WriteAllText(broken, "<broken");
        var result = Assert.IsType<AcceptanceCheckResult>(HeavyFixtureGitLaunchBoundCheck.Evaluate([Check(good), Check(broken)]));
        AssertSkipped(result, "unreadable TRX");
    }

    [Theory]
    [InlineData("Skipped")]
    [InlineData("NotExecuted")]
    public void Evaluate_UnexecutedRows_DoNotCountAsCoverage(string outcome)
    {
        var path = Write("unexecuted", Rows(), 0);
        var trx = XDocument.Load(path);
        trx.Descendants("UnitTestResult").First().SetAttributeValue("outcome", outcome);
        trx.Save(path);
        AssertSkipped(Evaluate(path), "missing rows");
    }

    [Fact]
    public void Evaluate_SimilarClassAndUnrelatedInvalidCounts_AreIgnored()
    {
        var unrelated = Rows().First().Replace("TestsTrainRedAttribution", "TestsTrainRedAttributionExtra");
        var other = Write("other", [unrelated], long.MaxValue);
        File.AppendAllText(LaneSlotTimingReader.PathFor(other), "{bad json" + Environment.NewLine);
        Assert.Null(HeavyFixtureGitLaunchBoundCheck.Evaluate([Check(other)]));
        var good = Write("selected", Rows(), 4357);
        var result = Assert.IsType<AcceptanceCheckResult>(HeavyFixtureGitLaunchBoundCheck.Evaluate([Check(good), Check(other)]));
        Assert.True(result.Passed);
        Assert.Contains("launches=4357", result.ResultSummary);
        Assert.Null(HeavyFixtureGitLaunchBoundCheck.Evaluate([]));
    }

    private static IEnumerable<string> Rows() => HeavyFixtureGitLaunchBoundCheck.Baselines.SelectMany(entry =>
        Enumerable.Range(0, entry.Rows).Select(index => $"{entry.ClassName}.Scenario(row: {index})"));

    private string Write(string name, IEnumerable<string> rows, long launches)
    {
        Directory.CreateDirectory(root);
        var identities = rows.ToArray();
        Assert.NotEmpty(identities);
        var path = Path.Combine(root, name + ".trx");
        new XDocument(new XElement("TestRun", new XElement("Results", identities.Select((identity, index) =>
            new XElement("UnitTestResult", new XAttribute("testId", index), new XAttribute("testName", identity),
                new XAttribute("outcome", "Passed")))))).Save(path);
        File.WriteAllLines(LaneSlotTimingReader.PathFor(path), identities.Select((identity, index) =>
            TimingRow(identity, index == 0 ? launches : 0)));
        return path;
    }

    private static string TimingRow(string identity, long launches) => JsonSerializer.Serialize(new
    {
        test_name = identity[..identity.IndexOf('(')], git_launches = launches, slot_count = 0
    });

    private static AcceptanceCheckResult Check(string path) => new("lane", true, 0, null, TestResultPaths: [path]);
    private static AcceptanceCheckResult Evaluate(string path) =>
        Assert.IsType<AcceptanceCheckResult>(HeavyFixtureGitLaunchBoundCheck.Evaluate([Check(path)]));

    private static void AssertSkipped(AcceptanceCheckResult result, string reason)
    {
        Assert.False(result.Passed);
        Assert.True(result.Advisory);
        Assert.Null(result.ExitCode);
        Assert.StartsWith("status=skipped;", result.ResultSummary);
        Assert.Contains(reason, result.ResultSummary);
    }

    private static void AssertMalformed(AcceptanceCheckResult result, string reason)
    {
        Assert.False(result.Passed);
        Assert.False(result.Advisory);
        Assert.Equal(1, result.ExitCode);
        Assert.StartsWith("status=failed; reason=malformed:", result.ResultSummary);
        Assert.Contains(reason, result.OutputTail);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
