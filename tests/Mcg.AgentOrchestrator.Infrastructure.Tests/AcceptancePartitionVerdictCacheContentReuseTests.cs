using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Security.Cryptography;
using System.Text.Json;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class AcceptancePartitionVerdictCacheContentReuseTests(ITestOutputHelper output) : GoalAcceptanceVerifierTestBase, IDisposable
{
    private static readonly GoalId ProbeGoal = new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    private const string ProbeTest = "AcceptancePartitionVerdictCacheContentReuseTests.ControlledProbe";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mcg-content-reuse-{Guid.NewGuid():N}");
    private readonly GoalAcceptanceVerifier.AcceptanceManifestCheck _lane = new()
    {
        Name = "infrastructure tests: Content reuse",
        Type = "dotnet-test",
        Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
        Arguments = ["--filter", "FullyQualifiedName~AcceptancePartitionVerdictCacheContentReuseTests"]
    };

    [Fact]
    public void Reuse_ForcedRedWithAvailableClosure_DoesNotResurrectGreen()
    {
        var (tree, main, closure) = CreateControlledRepository();
        var first = CreateControlledCache(ProbeGoal, "A-pass", tree, main);
        var green = FixtureResult("A-pass.trx", passed: true);
        first.RecordExecution(_lane, green);
        Assert.Contains("aggregate_verdict=GREEN", Assert.IsType<AcceptanceCheckResult>(first.CompleteAttempt()).ResultSummary);

        // Prove the real closure index is usable before introducing the red execution.
        var control = CreateControlledCache(new GoalId("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), "closure-control", tree, main);
        AssertClosureReuse(control.TryReuse(_lane), "A-pass", green.TestResultPaths!);

        var forced = CreateControlledCache(ProbeGoal, "B-forced-red", tree, main, fullRerunEveryN: 1);
        Assert.Equal(first.PairKey, forced.PairKey);
        Assert.True(forced.ForceFullRerun);
        Assert.Null(forced.TryReuse(_lane));
        var red = FixtureResult("B-forced-red.trx", passed: false);
        forced.RecordExecution(_lane, red);
        Assert.Contains("aggregate_verdict=RED", Assert.IsType<AcceptanceCheckResult>(forced.CompleteAttempt()).ResultSummary);

        var entries = File.ReadAllLines(forced.JournalPath)
            .Select(line => JsonSerializer.Deserialize<PartitionVerdictJournalEntry>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .Where(entry => entry.Operation == "acceptance:partition-verdict").ToArray();
        Assert.Equal(new[] { "GREEN", "RED" }, entries.Select(entry => entry.PartitionVerdict));
        Assert.Equal(entries[0].PartitionVerdictCacheKey, entries[1].PartitionVerdictCacheKey);
        Assert.Equal(entries[0].PartitionFilterHash, entries[1].PartitionFilterHash);
        Assert.Equal(red.TestResultPaths, entries[1].PartitionTestResultPaths);

        var lookup = CreateControlledCache(ProbeGoal, "C-lookup", tree, main);
        Assert.Equal(first.PairKey, lookup.PairKey);
        Assert.False(lookup.ForceFullRerun);
        Assert.Equal(closure, AcceptanceLaneClosureHasher.TryCompute(_root, _lane));
        var observed = lookup.TryReuse(_lane);
        output.WriteLine($"A-pass/B-forced-red/C-lookup pair={lookup.PairKey} candidate={lookup.CandidateTreeSha} main={lookup.MainSha} " +
            $"manifest={lookup.ManifestIdentity} filter={entries[1].PartitionFilterHash} closure={closure} C={observed?.ResultSummary ?? "cache-miss"}");
        Assert.True(observed is null,
            $"C must miss after B's same-pair/lane failed execution; observed {observed?.ResultSummary}");
    }

    [Fact]
    public void Reuse_UnchangedGreenClosure_PreservesArtifactsAndSource()
    {
        var (tree, main, closure) = CreateControlledRepository();
        var first = CreateControlledCache(ProbeGoal, "A-pass", tree, main);
        var green = FixtureResult("A-pass.trx", passed: true);
        first.RecordExecution(_lane, green);
        Assert.NotNull(first.CompleteAttempt());

        var other = CreateControlledCache(new GoalId("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), "control", tree, main);
        Assert.NotEqual(first.PairKey, other.PairKey);
        AssertClosureReuse(other.TryReuse(_lane), "A-pass", green.TestResultPaths!);
        Assert.Equal(closure, AcceptanceLaneClosureHasher.TryCompute(_root, _lane));
    }

    [Fact]
    public void Reuse_UnrelatedLaneFailed_StillReusesValidClosure()
    {
        var (tree, main, _) = CreateControlledRepository();
        var first = CreateControlledCache(ProbeGoal, "A-pass", tree, main);
        var green = FixtureResult("A-pass.trx", passed: true);
        first.RecordExecution(_lane, green);
        Assert.NotNull(first.CompleteAttempt());
        var unrelated = new GoalAcceptanceVerifier.AcceptanceManifestCheck
        {
            Name = "infrastructure tests: Other lane",
            Type = _lane.Type,
            Project = _lane.Project,
            Arguments = ["--filter", "FullyQualifiedName~OtherLaneTests"]
        };
        var otherGoal = new GoalId("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var failure = CreateControlledCache(otherGoal, "unrelated-red", tree, main, checks: [_lane, unrelated]);
        failure.RecordExecution(unrelated, FixtureResult("unrelated-red.trx", passed: false) with { Name = unrelated.Name });
        Assert.NotNull(failure.CompleteAttempt());

        var lookup = CreateControlledCache(otherGoal, "lookup", tree, main, checks: [_lane, unrelated]);
        AssertClosureReuse(lookup.TryReuse(_lane), "A-pass", green.TestResultPaths!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reuse_MissingOrEmptyClosureArtifact_Misses(bool empty)
    {
        var (tree, main, _) = CreateControlledRepository();
        var first = CreateControlledCache(ProbeGoal, "A-pass", tree, main);
        var green = FixtureResult("A-pass.trx", passed: true);
        first.RecordExecution(_lane, green);
        Assert.NotNull(first.CompleteAttempt());
        var control = CreateControlledCache(new GoalId("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), "control", tree, main);
        AssertClosureReuse(control.TryReuse(_lane), "A-pass", green.TestResultPaths!);
        var path = Assert.Single(green.TestResultPaths!);
        if (empty) File.WriteAllText(path, "");
        else File.Delete(path);

        var lookup = CreateControlledCache(new GoalId("cccccccccccccccccccccccccccccccc"), "lookup", tree, main);
        Assert.Null(lookup.TryReuse(_lane));
    }

    [Fact]
    public void GreenLane_IdenticalClosureAcrossDifferentCandidatesAndGoals_IsReused()
    {
        Directory.CreateDirectory(_root);
        CreateRepository();
        var first = CreateCache(new GoalId("11111111111111111111111111111111"), "attempt-one", "tree-a", "main-a");
        first.RecordExecution(_lane, PassedResult());
        Assert.NotNull(first.CompleteAttempt());

        var second = CreateCache(new GoalId("22222222222222222222222222222222"), "attempt-two", "tree-b", "main-b");

        // Against the pre-fix cache this assertion fails because the lookup is goal-scoped and
        // its key embeds both the candidate and main SHAs even though this worktree is unchanged.
        var reused = Assert.IsType<AcceptanceCheckResult>(second.TryReuse(_lane));
        Assert.Contains("source_attempt_id=attempt-one", reused.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("reuse_rule=closure", reused.ResultSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("closure_hash=not-applicable", reused.ResultSummary, StringComparison.Ordinal);

        File.AppendAllText(Path.Combine(_root, "src", "TestLibrary", "Probe.cs"), Environment.NewLine + "// changed inside closure");
        RunGit("add", "src/TestLibrary/Probe.cs");
        RunGit("commit", "-m", "change closure");
        var changed = CreateCache(new GoalId("33333333333333333333333333333333"), "attempt-three", "tree-c", "main-c");
        Assert.Null(changed.TryReuse(_lane));
    }

    [Fact]
    public void ScriptsOnlyChange_DoesNotReuseGreenLane()
    {
        Directory.CreateDirectory(_root);
        CreateRepository();
        var first = CreateCache(new GoalId("11111111111111111111111111111111"), "attempt-one", "tree-a", "main-a");
        first.RecordExecution(_lane, PassedResult());
        Assert.NotNull(first.CompleteAttempt());

        File.AppendAllText(Path.Combine(_root, "scripts", "probe.ps1"), Environment.NewLine + "# changed runtime input");
        RunGit("add", "scripts/probe.ps1");
        RunGit("commit", "-m", "change runtime script");

        var changed = CreateCache(new GoalId("22222222222222222222222222222222"), "attempt-two", "tree-b", "main-b");

        // Against the pre-fix closure this assertion fails because scripts are omitted from its hash.
        Assert.Null(changed.TryReuse(_lane));
    }

    [Fact]
    public void WithinAttemptRerun_PassingRecord_IsNeverExportedToClosureIndex()
    {
        Directory.CreateDirectory(_root);
        CreateRepository();
        var first = CreateCache(new GoalId("11111111111111111111111111111111"), "attempt-one", "tree-a", "main-a");
        var firstRun = new AcceptanceCheckResult(
            _lane.Name,
            false,
            1,
            "first run failed",
            CompletionDecision: new AcceptanceShardCompletionDecision(
                false,
                AcceptanceShardCompletionPredicates.NonzeroExit,
                false,
                1,
                1,
                1,
                "failed"));
        first.RecordWithinAttemptRetry(
            _lane,
            firstRun,
            "attempt-one:content-reuse:0",
            "attempt-one:content-reuse:1",
            new AcceptanceRetainedDiagnostic("first-run.err", "first-run-sha"));
        first.RecordExecution(_lane, PassedResult());
        Assert.NotNull(first.CompleteAttempt());

        var second = CreateCache(new GoalId("22222222222222222222222222222222"), "attempt-two", "tree-b", "main-b");

        Assert.Null(second.TryReuse(_lane));
    }

    [Fact]
    public void DisjointLandingFactsFromRealRepository()
    {
        Directory.CreateDirectory(_root);
        CreateRepository();
        string Revision(string expression)
        {
            var result = InfrastructureTestSupport.RunGitProbe(_root, ["rev-parse", expression]);
            Assert.True(result.Succeeded, result.StandardError);
            return result.StandardOutput.Trim();
        }
        const string landed = "src/TestLibrary/LandedWidget.cs";
        const string goal = "src/TestLibrary/GoalOwned.cs";
        var main1 = Revision("HEAD");
        RunGit("checkout", "-b", "landing-main");
        File.WriteAllText(Path.Combine(_root, landed), "internal class LandedWidget { }");
        RunGit("add", landed);
        RunGit("commit", "-m", "land independent file");
        var main2 = Revision("HEAD");
        RunGit("checkout", "-b", "goal-branch", main1);
        File.WriteAllText(Path.Combine(_root, goal), "internal class GoalOwned { }");
        RunGit("add", goal);
        RunGit("commit", "-m", "goal delta");
        var tree1 = Revision("HEAD^{tree}");
        RunGit("merge", "--no-ff", "--no-edit", "landing-main");
        var tree2 = Revision("HEAD^{tree}");

        var facts = Assert.IsType<DisjointLandingFacts>(AcceptanceDisjointLandingLaneReuse.ResolveFactsFromGit(
            new(_root, tree1, main1, tree2, main2)));

        Assert.True(facts.RecordMainIsAncestor);
        Assert.Equal(new[] { landed }, facts.LandedPaths);
        Assert.Equal(new[] { goal }, facts.RecordGoalDelta);
        Assert.Equal(new[] { goal }, facts.CurrentGoalDelta);
        Assert.Equal(new[] { landed }, facts.TreeDiff);

        File.AppendAllText(Path.Combine(_root, goal), " // goal changed again");
        RunGit("add", goal);
        RunGit("commit", "-m", "change goal after merge");
        var changed = Assert.IsType<DisjointLandingFacts>(AcceptanceDisjointLandingLaneReuse.ResolveFactsFromGit(
            new(_root, tree1, main1, Revision("HEAD^{tree}"), main2)));
        Assert.Equal(new[] { goal, landed }, changed.TreeDiff.Order(StringComparer.Ordinal));
    }

    public void Dispose()
    {
        DeleteDirectoryWithRetry(_root);
    }

    private AcceptancePartitionVerdictCache CreateCache(
        GoalId goalId,
        string attemptId,
        string candidateTreeSha,
        string mainSha,
        int fullRerunEveryN = 5,
        bool enforceStructuralCoverage = false,
        IReadOnlyList<GoalAcceptanceVerifier.AcceptanceManifestCheck>? checks = null,
        string? verifyingCommitSha = null) =>
        Assert.IsType<AcceptancePartitionVerdictCache>(AcceptancePartitionVerdictCache.Create(
            new AcceptancePartitionVerdictCacheOptions(
                goalId,
                _root,
                checks ?? [_lane],
                fullRerunEveryN,
                true,
                _ => candidateTreeSha,
                _ => mainSha,
                _ => verifyingCommitSha ?? $"commit-{attemptId}",
                () => attemptId,
                () => "manifest-a",
                () => enforceStructuralCoverage)));

    private AcceptancePartitionVerdictCache CreateControlledCache(
        GoalId goal, string attempt, string tree, string main, int fullRerunEveryN = 10,
        IReadOnlyList<GoalAcceptanceVerifier.AcceptanceManifestCheck>? checks = null) =>
        CreateCache(goal, attempt, tree, main, fullRerunEveryN, true, checks, main);

    private (string Tree, string Main, string Closure) CreateControlledRepository()
    {
        Directory.CreateDirectory(_root);
        CreateRepository();
        string Revision(string expression)
        {
            var revision = InfrastructureTestSupport.RunGitProbe(_root, ["rev-parse", expression]);
            Assert.True(revision.Succeeded, revision.StandardError);
            return revision.StandardOutput.Trim();
        }
        var closure = AcceptanceLaneClosureHasher.TryCompute(_root, _lane);
        Assert.False(string.IsNullOrWhiteSpace(closure), "The committed fixture must have a usable source closure.");
        return (Revision("HEAD^{tree}"), Revision("HEAD"), closure!);
    }

    private void AssertClosureReuse(AcceptanceCheckResult? result, string source, IReadOnlyList<string> paths)
    {
        var reused = Assert.IsType<AcceptanceCheckResult>(result);
        Assert.True(reused.Passed);
        Assert.Contains("reuse_rule=closure", reused.ResultSummary);
        Assert.Contains($"source_attempt_id={source}", reused.ResultSummary);
        Assert.Equal(paths, reused.TestResultPaths);
        Assert.True(reused.TestResultIsExplicitCrossAttemptReuse);
        Assert.Equal(source, reused.TestResultAttemptId);
        output.WriteLine($"closure-control: {reused.ResultSummary}");
    }

    // Synthetic, completed execution receipts are inputs to the cache; no child test host is launched.
    private AcceptanceCheckResult FixtureResult(string fileName, bool passed)
    {
        var directory = Path.Combine(_root, ".orchestrator", "fixture-trx");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        var outcome = passed ? "Passed" : "Failed";
        File.WriteAllText(path, $"""
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <TestDefinitions><UnitTest id="probe" name="{ProbeTest}"><TestMethod className="AcceptancePartitionVerdictCacheContentReuseTests" name="ControlledProbe" /></UnitTest></TestDefinitions>
              <Results><UnitTestResult testId="probe" testName="{ProbeTest}" outcome="{outcome}" /></Results>
              <ResultSummary outcome="{outcome}"><Counters total="1" executed="1" passed="{(passed ? 1 : 0)}" failed="{(passed ? 0 : 1)}" notExecuted="0" /></ResultSummary>
            </TestRun>
            """);
        var completion = AcceptanceShardCompletionAdjudicator.InspectTrxCompletion([path]);
        Assert.Equal(passed, completion.Passed);
        Assert.Equal(1, completion.DiscoveredTestCount);
        Assert.Equal(1, completion.ExecutedTestCount);
        Assert.Null(completion.FailedPredicate);
        var decision = AcceptanceShardCompletionAdjudicator.DecideTestShard(false, passed ? 0 : 1, completion);
        Assert.Equal(passed, decision.Passed);
        Assert.Equal(passed ? null : AcceptanceShardCompletionPredicates.FailingTrx, decision.FailedPredicate);
        var failures = AcceptanceTrxFailureReader.Read(path);
        Assert.Equal(AcceptanceTrxReadStatus.Readable, failures.Status);
        if (passed) Assert.Empty(failures.Failures);
        else Assert.Equal(ProbeTest, Assert.Single(failures.Failures).TestName);
        var bytes = File.ReadAllBytes(path);
        TestContext.Current.AddAttachment(fileName, bytes, "application/xml");
        output.WriteLine($"fixture-trx={fileName} sha256={Convert.ToHexString(SHA256.HashData(bytes))} executed=1 outcome={outcome}");
        return new AcceptanceCheckResult(_lane.Name, passed, passed ? 0 : 1, null,
            TestResultPaths: [path], ExecutedTestCount: 1, DiscoveredTestCount: 1,
            FailingTestIdentities: passed ? [] : [ProbeTest],
            CompletionDecision: decision);
    }

    private AcceptanceCheckResult PassedResult() => new(_lane.Name, true, 0, null, TestResultPaths: []);

    private void CreateRepository()
    {
        var testProject = Path.Combine(_root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        var libraryProject = Path.Combine(_root, "src", "TestLibrary");
        Directory.CreateDirectory(testProject);
        Directory.CreateDirectory(libraryProject);
        Directory.CreateDirectory(Path.Combine(_root, "scripts"));
        File.WriteAllText(Path.Combine(testProject, "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"../../src/TestLibrary/TestLibrary.csproj\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(libraryProject, "TestLibrary.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(libraryProject, "Probe.cs"), "internal sealed class Probe { }");
        File.WriteAllText(Path.Combine(_root, "scripts", "probe.ps1"), "Write-Output probe");
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        RunGit("init");
        RunGit("config", "user.email", "tests@example.invalid");
        RunGit("config", "user.name", "Tests");
        RunGit("add", ".");
        RunGit("commit", "-m", "initial closure");
    }

    private void RunGit(params string[] arguments)
    {
        var result = InfrastructureTestSupport.RunGitProbe(_root, arguments);
        Assert.True(result.Succeeded, $"git {string.Join(' ', arguments)} exit={result.ExitCode}: {result.StandardError}; {result}");
    }
}
