using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: copied operator projections, invocation-local fakes and private report output.
public sealed class ImpactReplayRetainedHistoryParityTests
{
    private const string FixtureName = "impact-replay-retained-94-regression.json";
    private const string FixtureHash = "788cf989bcc013f960fd712771c2c76422b376c4d533a481852d401965984503";
    private const string SourceHash = "54c820df31da09770d865112421eaad29b38a1b5a3adc4dbb6a8825f7bca4ca2";

    [Fact]
    public void RetainedInventory_IncludesEveryCaseAndExplicitEvidenceLimits()
    {
        using var document = ReadFixture();
        var rows = document.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(94, rows.Length);
        Assert.Equal(94, rows.Select(row => row[0].GetString()).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(258, rows.Sum(row => row[3].GetArrayLength()));
        Assert.Equal(162, rows.Sum(row => row[4].GetArrayLength()));
        Assert.Equal(3, rows.Sum(row => row[5].GetArrayLength()));
        Assert.Equal(84, rows.Count(row => row[2].GetBoolean()));
        Assert.Equal(2, rows.Sum(row => row[3].EnumerateArray().Count(failure => failure[4].GetBoolean())));
        Assert.Equal(119, rows.Sum(row => row[3].EnumerateArray().Count(failure => failure[5].GetBoolean())));
        Assert.Equal(1, rows.Count(row => row[11].GetBoolean())); // Other 93 normalized mappings remain unproved.
        foreach (var row in rows)
        {
            Assert.Equal(12, row.GetArrayLength());
            Assert.Empty(row[8].EnumerateArray()); // No absent source fields in this supplied projection.
            Assert.Equal(row[3].GetArrayLength(), row[9].GetArrayLength());
            Assert.All(row[9].EnumerateArray(), fields => Assert.Empty(fields.EnumerateArray()));
            Assert.Equal(7, row[10].GetArrayLength());
            Assert.All(row[10].EnumerateArray(), unknown => Assert.True(unknown.GetBoolean()));
        }

        // Link the amendment to the original projection without manufacturing native evidence.
        var oldBytes = File.ReadAllBytes(FixturePath("impact-replay-retained-3d6a5860.json"));
        Assert.Equal("3e1fe4b38f293a571e1c402776be7ef361ec78c281182c57c5dc957ef9cbd9c6",
            Convert.ToHexStringLower(SHA256.HashData(oldBytes)));
        using var oldDocument = JsonDocument.Parse(oldBytes);
        var oldRows = oldDocument.RootElement.GetProperty("rows").EnumerateArray()
            .ToDictionary(row => row[0].GetString()!, StringComparer.Ordinal);
        Assert.Equal(85, oldRows.Count);
        foreach (var old in oldRows.Values)
        {
            var current = Assert.Single(rows, row => row[0].GetString() == old[0].GetString());
            for (var field = 0; field < 3; field++) Assert.True(JsonElement.DeepEquals(old[field], current[field]));
            Assert.Equal(old[3].GetArrayLength(), current[3].GetArrayLength());
            for (var failure = 0; failure < old[3].GetArrayLength(); failure++)
                for (var field = 0; field < 5; field++)
                    Assert.True(JsonElement.DeepEquals(old[3][failure][field], current[3][failure][field]));
        }
        var added = rows.Where(row => !oldRows.ContainsKey(row[0].GetString()!)).ToArray();
        Assert.Equal(9, added.Length);
        Assert.All(added, row => { Assert.Empty(row[3].EnumerateArray()); Assert.True(row[2].GetBoolean()); });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RetainedCases_AdjudicateWithCoverageAndPreserveDiagnostics(bool aggregateAvailable)
    {
        using var document = ReadFixture();
        var rows = document.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        var attempts = new List<ImpactReplayAttemptRecord>();
        var historyQueries = 0;
        foreach (var row in rows)
        {
            var retained = row[3].EnumerateArray().Select(failure => new ImpactReplayFailingClass(
                failure[0].GetString()!, failure[1].GetString(), NullableBool(failure[2]), failure[3].GetString())).ToArray();
            var identities = new Dictionary<(string ClassName, string Lane), IReadOnlyList<string>>();
            var history = new Dictionary<string, GateStatsReport.FlakeRow>(StringComparer.Ordinal);
            var inputs = retained.Select(failure =>
            {
                if (failure.FlakeEvidence is not { } evidence || !evidence.StartsWith("gate-stats-history:", StringComparison.Ordinal))
                    return failure; // Raw observed IsMiss is not confirmed-hard provenance.
                var match = Regex.Match(evidence, @"^gate-stats-history: (.+) attempts=(\d+) flake=(\d+) hard=(\d+)$");
                Assert.True(match.Success, "Retained historical aggregate has an unsupported label");
                var test = match.Groups[1].Value;
                var count = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                var flake = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                var hard = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
                history.Add(test, new(test, count, count - flake - hard, flake, hard, (double)flake / count));
                identities.Add((failure.ClassName, failure.Lane!), [test]);
                return failure with { FlakeEvidence = null };
            }).ToArray();
            var query = new ImpactReplayHistoryQuery(tests =>
            {
                historyQueries++;
                Assert.Equal(history.Keys.Order(StringComparer.Ordinal), tests);
                // Only the supplied exact aggregate is available here, not native/rerun or global coverage.
                return new(history, aggregateAvailable, aggregateAvailable ? [] : ["Retained aggregate coverage unavailable"]);
            });
            var sourceWarnings = row[4].EnumerateArray().Select(value => value.GetString()!).ToArray();
            var warnings = sourceWarnings.ToList();
            var classified = query.Classify(inputs, identities, warnings);
            Assert.Equal(retained.Length, classified.Count);
            Assert.Equal(sourceWarnings, warnings.Take(sourceWarnings.Length));
            for (var index = 0; index < retained.Length; index++)
            {
                var expected = retained[index];
                var actual = classified[index];
                var withheld = !aggregateAvailable && identities.ContainsKey((expected.ClassName, expected.Lane!));
                var rawObservedIsMiss = row[3][index][4].GetBoolean();
                Assert.Equal(expected.ClassName, actual.ClassName);
                Assert.Equal(expected.Lane, actual.Lane);
                Assert.Equal(expected.WouldRun, actual.WouldRun);
                Assert.Equal(withheld ? null : expected.FlakeEvidence, actual.FlakeEvidence);
                Assert.Equal(rawObservedIsMiss || withheld, actual.IsMiss);
                Assert.Equal(!withheld && row[3][index][5].GetBoolean(), actual.IsFlakyFailureInSkippedLane);
                Assert.False(actual.ConfirmedHardFailure); // Every row explicitly lacks that provenance.
                if (actual.IsMiss)
                    Assert.Contains(warnings, warning => warning.Contains($"Historical evidence incomplete for {actual.ClassName}:", StringComparison.Ordinal));
            }
            if (!aggregateAvailable && history.Count > 0)
                Assert.Contains("Retained aggregate coverage unavailable", warnings);
            if (retained.Length == 0) Assert.Empty(query.QueriedIdentities);

            var unresolved = row[5].Deserialize<ImpactReplayUnresolvedFailure[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            attempts.Add(new(row[0].GetString()!, "retained-projection", DateTimeOffset.UnixEpoch,
                "", row[1].GetString()!, row[6].GetString()!, null, row[7].GetString(),
                [], [], [], [], [], [], 0, [], classified,
                row[2].GetBoolean() && warnings.Count == 0, warnings, UnresolvedFailures: unresolved));
        }
        Assert.Equal(1, historyQueries);
        Assert.Equal(aggregateAvailable ? 2 : 3, attempts.Sum(attempt => attempt.FailingClasses.Count(failure => failure.IsMiss)));
        Assert.Equal(aggregateAvailable ? 119 : 118, attempts.Sum(attempt => attempt.FailingClasses.Count(failure => failure.IsFlakyFailureInSkippedLane)));
        Assert.Equal(new[]
        {
            ("1bdf4ee5-0-20261108215028452-c2da37fb8c1845b08193d28c50c84cf2", "IsolatedProcessSpawningRemainderChainTests"),
            ("1b13a849-0-20261107160248704-c511e0f2334143ec8aba51ed77a5e96e", "PreTesterTestSourceGuardClassesTests")
        }, attempts.SelectMany(attempt => attempt.FailingClasses.Where(failure => failure.IsMiss &&
            !failure.ClassName.Equals("ProviderHoldAcrossRestartScenarioTests", StringComparison.Ordinal))
            .Select(failure => (attempt.AttemptId, failure.ClassName))));

        AssertWrittenDiagnostics(rows, attempts);
    }

    private static void AssertWrittenDiagnostics(JsonElement[] rows, IReadOnlyList<ImpactReplayAttemptRecord> attempts)
    {
        var output = Path.Combine(Path.GetTempPath(), "impact-retained-" + Guid.NewGuid().ToString("N"));
        try
        {
            ImpactReplayReportWriter.Write(output, new("retained-projection", [], attempts, [], [], []));
            using var written = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "impact-replay.json")));
            var actual = written.RootElement.GetProperty("attempts");
            Assert.Equal(94, actual.GetArrayLength());
            for (var index = 0; index < rows.Length; index++)
            {
                var row = rows[index];
                var attempt = actual[index];
                Assert.Equal(row[0].GetString(), attempt.GetProperty("attemptId").GetString());
                Assert.Equal(row[1].GetString(), attempt.GetProperty("branchHeadSha").GetString());
                Assert.Equal(row[6].GetString(), attempt.GetProperty("selectionOutcome").GetString());
                Assert.True(JsonElement.DeepEquals(row[7], attempt.GetProperty("selectionReason")));
                Assert.True(JsonElement.DeepEquals(row[5], attempt.GetProperty("unresolvedFailures")));
                var warnings = attempt.GetProperty("evidenceWarnings").EnumerateArray().Select(value => value.GetString()).ToArray();
                Assert.Equal(row[4].EnumerateArray().Select(value => value.GetString()), warnings.Take(row[4].GetArrayLength()));
                Assert.Equal(attempts[index].EvidenceWarnings, warnings);
                Assert.Equal(row[2].GetBoolean() && warnings.Length == 0, attempt.GetProperty("missCheckComplete").GetBoolean());
                if (row[5].GetArrayLength() > 0 || !row[2].GetBoolean())
                    Assert.False(attempt.GetProperty("missCheckComplete").GetBoolean());
            }
            Assert.Contains("Unresolved failing classes", File.ReadAllText(Path.Combine(output, "impact-replay.md")));
        }
        finally { if (Directory.Exists(output)) SharedTestSupport.RemoveTempDirectory(output); }
    }

    private static JsonDocument ReadFixture()
    {
        var document = ReadSourcePreservingJson(FixtureName,
            "27c5c88f8df116a32ee965e6328597a27abe0b2ec2fc33f55aa04ceaba7e3d65", FixtureHash, 82920);
        using var provenance = ReadSourcePreservingJson("impact-replay-retained-94-regression.provenance.json",
            "9c159ed50b1b97b1668a7a509b520d650cf424e2fda09d9428601822f7d40bfa",
            "82e6fa686008a798576cd2c11395d1005a058af406654867dc43e210e2555f63", 2800);
        Assert.Equal(FixtureHash, provenance.RootElement.GetProperty("fixtureSha256").GetString());
        Assert.Equal(82920, provenance.RootElement.GetProperty("fixtureBytes").GetInt32());
        Assert.Equal(SourceHash, provenance.RootElement.GetProperty("sourceReportSha256").GetString());
        var root = document.RootElement;
        Assert.Equal(SourceHash, root.GetProperty("sourceSha256").GetString());
        Assert.Equal("599721fd7", root.GetProperty("sourceRuntime").GetString());
        Assert.Equal(new[] { "attemptId", "candidateSha", "completeMissCheck", "failures", "evidenceWarnings", "unresolvedFailures",
            "selectionOutcome", "selectionReason", "absentSourceFields", "failureAbsentFieldIndices", "unknownEvidenceFlags",
            "pinnedNormalizedComparisonAvailable" }, Strings(root.GetProperty("rowSchema")));
        Assert.Equal(new[] { "className", "lane", "wouldRun", "flakeEvidence", "isMiss", "isFlakyFailureInSkippedLane" },
            Strings(root.GetProperty("failureSchema")));
        Assert.Equal(new[] { "nativeTrxBytesAndHash", "nativeOutcomeSpelling", "subMillisecondDurationPrecision",
            "duplicateDisplayIdentityMultiplicity", "originalTimeGlobalLedgerSnapshot", "currentCandidateAdjudication",
            "confirmedHardFailureNotRecorded" }, Strings(root.GetProperty("unknownEvidenceSchema")));
        return document;
    }

    private static JsonDocument ReadSourcePreservingJson(string name, string repositoryHash, string sourceHash, int sourceBytes)
    {
        var bytes = File.ReadAllBytes(FixturePath(name));
        Assert.Equal(repositoryHash, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        // Retained diagnostic paths use JSON Unicode escapes for home-root separators in source.
        // Reverse only that representation change to prove the operator's exact bytes and hash;
        // provenance fixtureSha256/fixtureBytes describe that original supplied artifact.
        var source = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(@"\u005c", @"\\", StringComparison.Ordinal));
        Assert.Equal(sourceBytes, source.Length);
        Assert.Equal(sourceHash, Convert.ToHexStringLower(SHA256.HashData(source)));
        var document = JsonDocument.Parse(bytes);
        using var original = JsonDocument.Parse(source);
        Assert.True(JsonElement.DeepEquals(original.RootElement, document.RootElement),
            "Repository encoding must preserve every retained JSON value, including diagnostics and provenance keys");
        return document;
    }

    private static IEnumerable<string?> Strings(JsonElement array) => array.EnumerateArray().Select(value => value.GetString());
    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", "Fixtures", name);
    private static bool? NullableBool(JsonElement element) => element.ValueKind == JsonValueKind.Null ? null : element.GetBoolean();
}
