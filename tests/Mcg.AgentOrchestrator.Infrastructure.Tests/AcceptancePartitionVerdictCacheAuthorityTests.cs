using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: each case owns all journal, index and result files; no processes or global hooks.
public sealed class AcceptancePartitionVerdictCacheAuthorityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mcg-cache-authority-{Guid.NewGuid():N}");
    private static readonly GoalId Source = new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    private static readonly GoalId Other = new("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Reuse_JournalOrderOverridesTiedOrReversedClocks_BlocksRed(bool core, bool tied)
    {
        var check = Lane(core);
        var first = Execute(Source, "green", check, true);
        var red = Execute(Source, "red", check, false);
        RewriteJournal(red.JournalPath, entry => entry with
        {
            At = entry.PartitionAttemptId == "green" || tied
                ? DateTimeOffset.UnixEpoch.AddDays(2) : DateTimeOffset.UnixEpoch
        });

        var lookup = Cache(Source, "lookup", check);
        Assert.Null(lookup.TryReuse(check));
        Assert.Equal(PartitionVerdictMissReasons.NonreusableAuthoritativeVerdict, Assert.Single(lookup.Misses).Reason);
        // A requesting pair without history must still validate the proposed source's own red.
        var other = Cache(Other, "other", check);
        Assert.Null(other.TryReuse(check));
        Assert.Equal(PartitionVerdictMissReasons.ClosureSourceNotAuthoritative, Assert.Single(other.Misses).Reason);
        Assert.Equal(first.PairKey, red.PairKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reuse_DerivedIndexEvictedAndRebuilt_StillBlocksFailedPair(bool core)
    {
        var check = Lane(core);
        Execute(Source, "green", check, true);
        var indexPath = Path.Combine(_root, ".orchestrator", "acceptance-closure-verdicts.jsonl");
        var oldIndex = File.ReadAllText(indexPath);
        Execute(Source, "red", check, false);
        File.Delete(indexPath);
        Assert.Null(Cache(Source, "evicted", check).TryReuse(check));
        File.WriteAllText(indexPath, oldIndex);
        Assert.Null(Cache(Source, "rebuilt", check).TryReuse(check));
        Assert.Null(Cache(Other, "cross-goal", check).TryReuse(check));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reuse_NewExecutedGreenSupersedesRed_PreservesPairAndClosure(bool core)
    {
        var check = Lane(core);
        Execute(Source, "green", check, true);
        Execute(Source, "red", check, false);
        Execute(Source, "recovered", check, true);
        var pair = Assert.IsType<AcceptanceCheckResult>(Cache(Source, "pair", check).TryReuse(check));
        var closure = Assert.IsType<AcceptanceCheckResult>(Cache(Other, "closure", check).TryReuse(check));
        Assert.Equal("recovered", pair.TestResultAttemptId);
        Assert.Equal("recovered", closure.TestResultAttemptId);
        Assert.Contains("reuse_rule=closure", closure.ResultSummary);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("malformed")]
    [InlineData("missing")]
    [InlineData("zero")]
    [InlineData("failed")]
    public void Reuse_InvalidGreenCannotSupersedeRed_EvenWithoutCoverageSwitch(string artifact)
    {
        var check = Lane(false);
        Execute(Source, "green", check, true);
        Execute(Source, "red", check, false);
        var result = Result(check, "invalid-green", true);
        var path = Assert.Single(result.TestResultPaths!);
        File.WriteAllText(path, artifact switch
        {
            "empty" => "",
            "malformed" => "<broken",
            "zero" => Trx(true, executed: 0),
            _ => Trx(false)
        });
        if (artifact == "missing") File.Delete(path);
        var invalid = Cache(Source, "invalid-green", check);
        invalid.RecordExecution(check, result);
        Assert.NotNull(invalid.CompleteAttempt());

        Assert.Null(Cache(Source, "pair", check, enforceCoverage: false).TryReuse(check));
        Assert.Null(Cache(Other, "closure", check, enforceCoverage: false).TryReuse(check));
    }

    [Fact]
    public void RecordExecution_CachedReuseAfterRed_DoesNotCreateExecutedGreen()
    {
        var check = Lane(false);
        Execute(Source, "green", check, true);
        var reuseSession = Cache(Source, "cached", check);
        var reused = Assert.IsType<AcceptanceCheckResult>(reuseSession.TryReuse(check));
        Execute(Source, "red", check, false);
        reuseSession.RecordExecution(check, reused);
        Assert.NotNull(reuseSession.CompleteAttempt());
        var entries = ReadJournal(reuseSession.JournalPath);
        Assert.DoesNotContain(entries, entry => entry.Operation == "acceptance:partition-verdict" &&
            entry.PartitionAttemptId == "cached");
        Assert.Null(Cache(Source, "lookup", check).TryReuse(check));
    }

    [Fact]
    public void Reuse_SourceChangesAfterDiscovery_RevalidatesOwningJournal()
    {
        var check = Lane(false);
        Execute(Source, "green", check, true);
        var lookup = Cache(Other, "lookup", check);
        Assert.Equal("green", Assert.IsType<AcceptanceCheckResult>(lookup.TryReuse(check)).TestResultAttemptId);
        Execute(Source, "red", check, false);
        Assert.Null(lookup.TryReuse(check));
        Assert.Equal(PartitionVerdictMissReasons.ClosureSourceNotAuthoritative, Assert.Single(lookup.Misses).Reason);
    }

    [Fact]
    public void Reuse_FailedRequestCannotBorrowDifferentValidSource_UnrelatedPairCan()
    {
        var check = Lane(false);
        Execute(Source, "request-red", check, false);
        Execute(Other, "independent-green", check, true);
        Assert.Null(Cache(Source, "failed-request", check).TryReuse(check));
        var unrelated = Cache(new GoalId("cccccccccccccccccccccccccccccccc"), "unrelated", check);
        Assert.Equal("independent-green", Assert.IsType<AcceptanceCheckResult>(unrelated.TryReuse(check)).TestResultAttemptId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reuse_NongreenApparatusOrUnclassified_OnlyBlocksItsExactScope(bool apparatus)
    {
        var check = Lane(false);
        Execute(Source, "green", check, true);
        var failure = Cache(Other, "apparatus-or-unclassified", check);
        var result = new AcceptanceCheckResult(check.Name, false, 1, "fixture failure");
        if (apparatus)
        {
            result = failure.ObserveSharedApparatusEvidence(check, result with
            {
                CompletionDecision = new AcceptanceShardCompletionDecision(false,
                    AcceptanceShardCompletionPredicates.IncompleteVstestOutput, false, 1, null, null, "incomplete")
            });
            Assert.Equal(AcceptanceFailureClassifications.SharedGateApparatusInvalidated, result.FailureClassification);
            Assert.NotNull(failure.SharedApparatusInvalidation);
        }
        else Assert.Null(result.FailureClassification); // Nonzero exit alone supplies no cause.
        failure.RecordExecution(check, result);
        Assert.NotNull(failure.CompleteAttempt());
        Assert.Null(Cache(Other, "blocked", check).TryReuse(check));
        var unrelated = Cache(new GoalId("cccccccccccccccccccccccccccccccc"), "allowed", check);
        Assert.Equal("green", Assert.IsType<AcceptanceCheckResult>(unrelated.TryReuse(check)).TestResultAttemptId);
    }

    [Theory]
    [InlineData("supported-v1")]
    [InlineData("missing-source")]
    [InlineData("ambiguous-source")]
    [InlineData("broken-journal")]
    [InlineData("unsupported-version")]
    [InlineData("wrong-artifacts")]
    public void Reuse_LegacyIndexRequiresUnambiguousAuthority_PreservesValidReads(string scenario)
    {
        var check = Lane(false);
        var source = Execute(Source, "green", check, true);
        var indexPath = Path.Combine(_root, ".orchestrator", "acceptance-closure-verdicts.jsonl");
        if (scenario == "missing-source") File.Delete(source.JournalPath);
        if (scenario == "ambiguous-source")
            File.Copy(source.JournalPath, Path.Combine(Path.GetDirectoryName(source.JournalPath)!, $"{Other.Value}.jsonl"));
        if (scenario == "broken-journal") File.AppendAllText(source.JournalPath, "{broken\n");
        if (scenario is "unsupported-version" or "wrong-artifacts")
        {
            var entry = JsonSerializer.Deserialize<AcceptanceClosureVerdictRecord>(File.ReadAllText(indexPath), Json)!;
            entry = scenario == "unsupported-version" ? entry with { Version = 99 } : entry with { TestResultPaths = ["different.trx"] };
            File.WriteAllText(indexPath, JsonSerializer.Serialize(entry, Json) + "\n");
        }

        var reused = Cache(Other, "lookup", check).TryReuse(check);
        if (scenario == "supported-v1")
        {
            Assert.Equal("green", Assert.IsType<AcceptanceCheckResult>(reused).TestResultAttemptId);
            Assert.Contains("reuse_rule=closure", reused!.ResultSummary);
        }
        else Assert.Null(reused);
    }

    private AcceptancePartitionVerdictCache Execute(GoalId goal, string attempt, Check check, bool passed)
    {
        var cache = Cache(goal, attempt, check);
        cache.RecordExecution(check, Result(check, attempt, passed));
        Assert.NotNull(cache.CompleteAttempt());
        return cache;
    }

    private AcceptancePartitionVerdictCache Cache(GoalId goal, string attempt, Check check, bool enforceCoverage = true) =>
        Assert.IsType<AcceptancePartitionVerdictCache>(AcceptancePartitionVerdictCache.Create(
            new AcceptancePartitionVerdictCacheOptions(goal, _root, [check], 100, false,
                _ => "tree-a", _ => "main-a", _ => "commit-a", () => attempt, () => "manifest-a",
                () => enforceCoverage, ResolveClosureHash: _ => "closure-a", IdenticalTreeChecks: [check])));

    private AcceptanceCheckResult Result(Check check, string attempt, bool passed)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, $"{attempt}.trx");
        File.WriteAllText(path, Trx(passed));
        return new AcceptanceCheckResult(check.Name, passed, passed ? 0 : 1, null, TestResultPaths: [path]);
    }

    private static string Trx(bool passed, int executed = 1) => $"""
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results><UnitTestResult testName="CacheProbe" outcome="{(passed ? "Passed" : "Failed")}" /></Results>
          <ResultSummary outcome="{(passed ? "Passed" : "Failed")}"><Counters total="{executed}" executed="{executed}" passed="{(passed ? executed : 0)}" failed="{(passed ? 0 : executed)}" notExecuted="0" /></ResultSummary>
        </TestRun>
        """;

    private static Check Lane(bool core) => new()
    {
        Name = core ? "core tests" : "infrastructure tests: Cache authority",
        Type = "dotnet-test",
        Project = core ? "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj"
            : "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
        Arguments = core ? [] : ["--filter", "FullyQualifiedName~CacheAuthorityProbe"]
    };

    private static PartitionVerdictJournalEntry[] ReadJournal(string path) => File.ReadAllLines(path)
        .Select(line => JsonSerializer.Deserialize<PartitionVerdictJournalEntry>(line, Json)!).ToArray();

    private static void RewriteJournal(string path, Func<PartitionVerdictJournalEntry, PartitionVerdictJournalEntry> change) =>
        File.WriteAllLines(path, ReadJournal(path).Select(entry => JsonSerializer.Serialize(change(entry), Json)));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
