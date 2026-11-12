using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: invocation-local fakes, absolute synthetic paths, no process or shared state.
public sealed class ImpactReplayHistoryQueryTests
{
    private const string Test = "Sample.GuardTests.Works(value: 1)";
    private const string Other = "Sample.OtherTests.Works";
    private static readonly ImpactReplayFailingClass Failure = new("Sample.GuardTests", "skipped", false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyOrPassingSelection_DoesNotOpenHistory(bool passing)
    {
        var calls = 0;
        var query = new ImpactReplayHistoryQuery(_ => { calls++; throw new InvalidOperationException("History opened"); });
        var warnings = new List<string>();
        var result = query.Classify(passing ? [Failure with { WouldRun = true }] : [], Identities(), warnings);
        Assert.Equal(passing ? 1 : 0, result.Count);
        Assert.Equal(0, calls);
        Assert.Empty(query.QueriedIdentities);
        Assert.Empty(warnings);
    }

    [Fact]
    public void SkippedUnadjudicatedFailures_QueryExactIdentitiesOnce()
    {
        var requests = new List<string[]>();
        var query = new ImpactReplayHistoryQuery(tests =>
        {
            requests.Add(tests.ToArray());
            return new(new Dictionary<string, GateStatsReport.FlakeRow>(StringComparer.Ordinal)
                { [Test] = new(Test, 400, 399, 1, 0, 1d / 400) }, true, []);
        });
        var warnings = new List<string>();
        var existing = Failure with { ClassName = "Sample.ReceiptTests", FlakeEvidence = "lane-flakes" };
        var hard = Failure with { ClassName = "Sample.HardTests", ConfirmedHardFailure = true };
        var selected = Failure with { ClassName = "Sample.SelectedTests", WouldRun = true };
        var result = query.Classify([Failure, existing, hard, selected], Identities(), warnings);
        Assert.Equal(new[] { Test }, Assert.Single(requests));
        Assert.Equal($"gate-stats-history: {Test} attempts=400 flake=1 hard=0", result[0].FlakeEvidence);
        Assert.Same(existing, result[1]);
        Assert.Same(hard, result[2]);
        Assert.True(result[2].IsMiss);
        Assert.Same(selected, result[3]);
        Assert.Equal(result[0], Assert.Single(query.Classify([Failure], Identities(), warnings)));
        Assert.Single(requests);
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingOrHardSibling_NeverExcusesWholeClass(bool missing)
    {
        var query = new ImpactReplayHistoryQuery(_ => new(new Dictionary<string, GateStatsReport.FlakeRow>
        {
            [Test] = new(Test, 1, 0, 1, 0, 1),
            [Other] = new(Other, 1, 0, 0, 1, 0)
        }, true, []));
        var identities = Identities();
        identities[(Failure.ClassName, Failure.Lane!)] = [Test, missing ? "Sample.MissingTests.Works" : Other];
        Assert.True(Assert.Single(query.Classify([Failure], identities, new List<string>())).IsMiss);
    }

    [Theory]
    [InlineData("sample.GuardTests.Works(value: 1)")]
    [InlineData("Sample.GuardTests.Works(value: 2)")]
    [InlineData("Sample.GuardTests")]
    public void NonExactIdentity_DoesNotSupplyPositiveEvidence(string historyIdentity)
    {
        var query = new ImpactReplayHistoryQuery(_ => new(new Dictionary<string, GateStatsReport.FlakeRow>
            { [historyIdentity] = new(historyIdentity, 1, 0, 1, 0, 1) }, true, []));
        Assert.True(Assert.Single(query.Classify([Failure], Identities(), new List<string>())).IsMiss);
    }

    [Fact]
    public void PositiveFlakeWithIncompleteCoverage_RemainsUnknown()
    {
        var warnings = new List<string> { "retained source warning" };
        var query = new ImpactReplayHistoryQuery(_ => new(new Dictionary<string, GateStatsReport.FlakeRow>
            { [Test] = new(Test, 1, 0, 1, 0, 1) }, false, ["missing older index"]));
        var failure = Assert.Single(query.Classify([Failure], Identities(), warnings));
        Assert.True(failure.IsMiss);
        Assert.False(failure.ConfirmedHardFailure);
        Assert.Null(failure.FlakeEvidence);
        Assert.Equal("retained source warning", warnings[0]);
        Assert.Contains("missing older index", warnings);
        Assert.Contains(warnings, value => value.Contains("Historical evidence incomplete", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingIdentity_RetainsObservedMissWithoutInventingHardProvenance()
    {
        var calls = 0;
        var query = new ImpactReplayHistoryQuery(_ =>
        {
            calls++;
            throw new InvalidOperationException("An unknown identity must not open unrelated history");
        });
        var warnings = new List<string> { "retained source warning" };
        var failure = Assert.Single(query.Classify([Failure],
            new Dictionary<(string ClassName, string Lane), IReadOnlyList<string>>(), warnings));
        Assert.Same(Failure, failure);
        Assert.True(failure.IsMiss);
        Assert.False(failure.ConfirmedHardFailure);
        Assert.Null(failure.FlakeEvidence);
        Assert.Equal(0, calls);
        Assert.Empty(query.QueriedIdentities);
        Assert.Equal("retained source warning", warnings[0]);
        Assert.Contains(warnings, value => value.Contains("exact failing identity unavailable", StringComparison.Ordinal));
        Assert.Contains(warnings, value => value.Contains("Recover the selected original/rerun receipts", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfirmedRepeatedFailure_PreservesHardVetoAndExistingWarnings()
    {
        var receipt = Receipt("selected", Test) with
        {
            LaneName = Failure.Lane!, Outcome = AcceptanceLaneRerunEvidence.ConfirmedFailure,
            FirstPredicate = AcceptanceShardCompletionPredicates.FailingTrx,
            RerunPredicate = AcceptanceShardCompletionPredicates.FailingTrx,
            RerunFailingTestIdentities = [Test]
        };
        var warnings = new List<string> { "retained source warning" };
        var local = ImpactReplayRecordedFlakeClassifier.Classify("selected", [Failure],
            [new(Failure.Lane!, "Failed", [])], [], [new(Failure.ClassName, null)], [receipt], warnings);
        Assert.True(Assert.Single(local).ConfirmedHardFailure); // Provenance, not observed IsMiss, supplies the veto.
        var calls = 0;
        var query = new ImpactReplayHistoryQuery(_ =>
        {
            calls++;
            return new(new Dictionary<string, GateStatsReport.FlakeRow>
                { [Test] = new(Test, 1, 0, 1, 0, 1) }, true, []);
        });
        var failure = Assert.Single(query.Classify(local, Identities(), warnings));
        Assert.True(failure.ConfirmedHardFailure);
        Assert.True(failure.IsMiss);
        Assert.Null(failure.FlakeEvidence);
        Assert.Equal(0, calls);
        Assert.Equal(new[] { "retained source warning" }, warnings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableMetadata_RetainsFailureAndNamesRecovery(bool unreadable)
    {
        var reads = new List<string>();
        var reader = new ImpactReplayHistoricalEvidenceReader(() => unreadable ? throw new IOException("missing index") : [],
            path => { reads.Add(path); return []; });
        var warnings = new List<string>();
        var query = new ImpactReplayHistoryQuery(reader.Read);
        Assert.Same(Failure, Assert.Single(query.Classify([Failure], Identities(), warnings)));
        Assert.Equal(1, reader.MetadataReads);
        Assert.Empty(reads);
        Assert.Empty(reader.ParsedPaths);
        Assert.Contains(warnings, value => value.Contains("no exhaustive source-bound exact-identity index", StringComparison.Ordinal));
        Assert.Contains(warnings, value => value.Contains("authorize a separate diagnostic history scan", StringComparison.Ordinal));
        if (unreadable) Assert.Contains(warnings, value => value.Contains("metadata unavailable: IOException", StringComparison.Ordinal));
    }

    [Fact]
    public void RelevantOldHistory_IsNotCappedAndUnrelatedReceiptsAreNotParsed()
    {
        var old = Receipt("old", Test);
        var unrelated = Enumerable.Range(0, 500).Select(index => Receipt("new" + index, Other)).ToArray();
        var paths = new List<string>();
        var reader = new ImpactReplayHistoricalEvidenceReader(() => [.. unrelated, old], path =>
        {
            paths.Add(path);
            return [new("Sample.GuardTests", Test, path.EndsWith("-run-12.trx", StringComparison.Ordinal) ? "Passed" : "Failed", 1)];
        });
        var evidence = reader.Read([Test]);
        var row = Assert.Single(evidence.Rows).Value;
        Assert.Equal(1, row.Flake);
        Assert.Equal(0, row.Hard);
        Assert.Equal(old.FirstTestResultPaths.Concat(old.RerunTestResultPaths), paths);
        Assert.Equal(2, reader.ParsedPaths.Count);
        Assert.False(evidence.Complete); // Adjudication-only receipts never prove absence of other hard history.
        reader.Read([Test]);
        Assert.Equal(2, paths.Count);
        Assert.Equal(1, reader.MetadataReads);

        var query = new ImpactReplayHistoryQuery(reader.Read);
        Assert.True(Assert.Single(query.Classify([Failure], Identities(), new List<string>())).IsMiss);
        var report = new ImpactReplayReport("default", [], [], [], [], [], HistoricalEvidence:
            new(query.QueriedIdentities, reader.MetadataReads, reader.ParsedPaths.Count, reader.ParsedPaths));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var jsonText = JsonSerializer.Serialize(report, options);
        using var json = JsonDocument.Parse(jsonText);
        var history = json.RootElement.GetProperty("historicalEvidence");
        Assert.Equal(new[] { "metadataReads", "parsedPaths", "queriedIdentities", "trxParses" },
            history.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(1, history.GetProperty("metadataReads").GetInt32());
        Assert.Equal(2, history.GetProperty("trxParses").GetInt32());
        Assert.Equal(new[] { Test }, history.GetProperty("queriedIdentities").EnumerateArray().Select(value => value.GetString()));
        var expectedPaths = old.FirstTestResultPaths.Concat(old.RerunTestResultPaths).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(expectedPaths, history.GetProperty("parsedPaths").EnumerateArray().Select(value => value.GetString()));
        var roundTrip = JsonSerializer.Deserialize<ImpactReplayReport>(jsonText, options)!.HistoricalEvidence!;
        Assert.Equal(1, roundTrip.MetadataReads);
        Assert.Equal(2, roundTrip.TrxParses);
        Assert.Equal(new[] { Test }, roundTrip.QueriedIdentities);
        Assert.Equal(expectedPaths, roundTrip.ParsedPaths);
        Assert.Equal(2, paths.Count);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void MultiFileReceipt_WithoutPerPathProvenance_DoesNotParseAnyPath(bool multipleFirst, bool multipleRerun)
    {
        var receipt = Receipt("multi-file", Test);
        var ambiguous = receipt with
        {
            FirstTestResultPaths = multipleFirst ? [.. receipt.FirstTestResultPaths, PathFor("unrelated-first.trx")] : receipt.FirstTestResultPaths,
            RerunTestResultPaths = multipleRerun ? [.. receipt.RerunTestResultPaths, PathFor("unrelated-rerun.trx")] : receipt.RerunTestResultPaths
        };
        var reads = new List<string>();
        var reader = new ImpactReplayHistoricalEvidenceReader(() => [ambiguous], path =>
        {
            reads.Add(path);
            throw new InvalidOperationException("A receipt-wide identity cannot authorize this path");
        });
        var warnings = new List<string>();
        var query = new ImpactReplayHistoryQuery(reader.Read);
        var failure = Assert.Single(query.Classify([Failure], Identities(), warnings));
        Assert.True(failure.IsMiss);
        Assert.Null(failure.FlakeEvidence);
        Assert.Equal(new[] { Test }, query.QueriedIdentities);
        Assert.Equal(1, reader.MetadataReads);
        Assert.Empty(reads);
        Assert.Empty(reader.ParsedPaths);
        Assert.Contains(warnings, value => value.Contains("lacks per-path identity provenance: multi-file, lane", StringComparison.Ordinal));
        Assert.Contains(warnings, value => value.Contains("requires separate operator authorization", StringComparison.Ordinal));
        Assert.Contains(warnings, value => value.Contains("Historical evidence incomplete", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("repeated", true)]
    [InlineData("timeout", false)]
    [InlineData("unavailable", false)]
    [InlineData("different-method", false)]
    [InlineData("different-parameter", false)]
    [InlineData("missing-paths", false)]
    [InlineData("unrooted-path", false)]
    [InlineData("missing-invocation", false)]
    [InlineData("same-invocation", false)]
    [InlineData("missing-candidate", false)]
    public void ConfirmedLabel_RequiresExactRepeatedFailureAndProvenance(string scenario, bool hard)
    {
        var receipt = Receipt("selected", Test) with
        {
            LaneName = Failure.Lane!, Outcome = AcceptanceLaneRerunEvidence.ConfirmedFailure,
            FirstPredicate = AcceptanceShardCompletionPredicates.FailingTrx,
            RerunPredicate = AcceptanceShardCompletionPredicates.FailingTrx,
            RerunFailingTestIdentities = [Test]
        };
        receipt = scenario switch
        {
            "timeout" => receipt with { RerunPredicate = AcceptanceShardCompletionPredicates.TimedOut, RerunFailingTestIdentities = [] },
            "unavailable" => receipt with { RerunFailingTestIdentities = [] },
            "different-method" => receipt with { RerunFailingTestIdentities = ["Sample.GuardTests.Other"] },
            "different-parameter" => receipt with { RerunFailingTestIdentities = ["Sample.GuardTests.Works(value: 2)"] },
            "missing-paths" => receipt with { RerunTestResultPaths = [] },
            "unrooted-path" => receipt with { RerunTestResultPaths = ["relative.trx"] },
            "missing-invocation" => receipt with { RerunInvocationId = "" },
            "same-invocation" => receipt with { RerunInvocationId = receipt.FirstInvocationId },
            "missing-candidate" => receipt with { CandidateTreeSha = "" },
            _ => receipt
        };
        var warnings = new List<string>();
        var local = ImpactReplayRecordedFlakeClassifier.Classify("selected", [Failure],
            [new(Failure.Lane!, "Failed", [])], [], [new(Failure.ClassName, null)], [receipt], warnings);
        var calls = 0;
        var query = new ImpactReplayHistoryQuery(tests =>
        {
            calls++;
            Assert.Equal(new[] { Test }, tests);
            return new(new Dictionary<string, GateStatsReport.FlakeRow>(), false, ["missing coverage"]);
        });
        var failure = Assert.Single(query.Classify(local, Identities(), warnings));
        Assert.Equal(hard, failure.ConfirmedHardFailure);
        Assert.True(failure.IsMiss);
        Assert.Null(failure.FlakeEvidence);
        Assert.Equal(hard ? 0 : 1, calls);
        if (hard) Assert.Empty(warnings);
        else
        {
            Assert.Contains(warnings, value => value.Contains("Selected rerun evidence incomplete", StringComparison.Ordinal));
            Assert.Contains("missing coverage", warnings);
        }
    }

    [Fact]
    public void SharedRelevantPaths_AreParsedOnceAcrossIdentityQueries()
    {
        var receipt = Receipt("shared", Test) with { FirstFailingTestIdentities = [Test, Other] };
        var reads = 0;
        var reader = new ImpactReplayHistoricalEvidenceReader(() => [receipt], path =>
        {
            reads++;
            var outcome = path.EndsWith("-run-12.trx", StringComparison.Ordinal) ? "Passed" : "Failed";
            return [new("Sample.GuardTests", Test, outcome, 1), new("Sample.OtherTests", Other, outcome, 1)];
        });
        Assert.Equal(Test, Assert.Single(reader.Read([Test]).Rows).Key);
        Assert.Equal(Other, Assert.Single(reader.Read([Other]).Rows).Key);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void MissingRerun_ReportsIncompleteInsteadOfUsingPartialHistory()
    {
        var receipt = Receipt("missing-rerun", Test);
        var reader = new ImpactReplayHistoricalEvidenceReader(() => [receipt], path =>
            path.EndsWith("-run-12.trx", StringComparison.Ordinal) ? null : [new("Sample.GuardTests", Test, "Failed", 1)]);
        var warnings = new List<string>();
        var result = new ImpactReplayHistoryQuery(reader.Read).Classify([Failure], Identities(), warnings);
        Assert.True(Assert.Single(result).IsMiss);
        Assert.Contains(warnings, value => value.Contains("Historical TRX unavailable:", StringComparison.Ordinal));
        Assert.Contains(warnings, value => value.Contains("Historical evidence incomplete", StringComparison.Ordinal));
        Assert.Equal(2, reader.ParsedPaths.Count);
    }

    [Fact]
    public void MixedHistory_PreservesNumericRunOrderAndHardLanePrecedence()
    {
        var flake = Receipt("same-attempt", Test);
        var hard = Receipt("same-attempt", Test) with { LaneName = "other lane",
            FirstTestResultPaths = [PathFor("other.trx")], RerunTestResultPaths = [PathFor("other-run-12.trx")] };
        var reader = new ImpactReplayHistoricalEvidenceReader(() => [flake, hard], path =>
            [new("Sample.GuardTests", Test, path.Contains("other", StringComparison.Ordinal) ||
                !path.EndsWith("-run-12.trx", StringComparison.Ordinal) ? "Failed" : "Passed", 1)]);
        var row = Assert.Single(reader.Read([Test]).Rows).Value;
        Assert.Equal(1, row.Attempts);
        Assert.Equal(1, row.Hard);
        Assert.Equal(0, row.Flake);
    }

    private static Dictionary<(string ClassName, string Lane), IReadOnlyList<string>> Identities() =>
        new() { [(Failure.ClassName, Failure.Lane!)] = [Test] };

    private static string PathFor(string name) => Path.Combine(Path.GetTempPath(), "impact-history-query-fixture", name);
    private static AcceptanceLaneFlakeRow Receipt(string attempt, string test) => new("goal", attempt, "lane", new string('a', 40),
        "first", [PathFor(attempt + ".trx")], [test], "failed", "rerun", [PathFor(attempt + "-run-12.trx")], [], null,
        AcceptanceLaneRerunEvidence.Flake, DateTimeOffset.UnixEpoch);
}
