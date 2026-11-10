using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: discovery is injected; sources and TRX receipts use a private temporary root.
// Characterization only: Acceptance must execute this before a production correction is chosen.
public sealed class DeclaredRemovalRowIdentityTests : IDisposable
{
    private const string Method = "AdmissionFile_BothSlashStylesAndCase_Touches";
    private const string Declaration = "AdmissionRows." + Method;
    private const string Alias = "Admission path collisions";
    private const string SourceFile = "AdmissionRows.cs";
    private const string RemovedFile = "ConductorDriver.FollowerGates.cs";
    private const string Survivor = "OtherTests.NonFollowerSurvives";
    private const string PathPrefix = "src/Mcg.AgentOrchestrator.App/Orchestration/";
    private static readonly string[] Files =
    [
        "ConductorBatchLoop.LiveAcceptanceAdmission.cs",
        "ConductorBatchLoop.ParallelAcceptanceAdmission.cs",
        "ConductorDriver.AcceptanceCohortExecution.cs", RemovedFile,
        "ConductorDriver.GroupedGateAttempts.cs", "ConductorDriver.MergeTrains.cs",
        "ConductorGroupedGateAttemptHost.cs", "ConductorGroupedGateAttempts.cs",
        "ConductorLiveGroupedGateCount.cs", "ConductorOrphanGateAttemptAdoption.cs",
        "ConductorParallelAcceptanceAttempts.cs",
        "ConductorParallelAcceptanceAttempts.HolderLease.cs",
        "ConductorParallelAcceptanceAttempts.RunningExitPaths.cs"
    ];
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        nameof(DeclaredRemovalRowIdentityTests), Guid.NewGuid().ToString("N"));

    [Fact]
    public void Parse_CollidingRows_RetainsOccurrencesButLosesUidDistinctions()
    {
        var main = Rows(truncated: true, customName: false, removeFollower: false);
        var candidate = Rows(truncated: true, customName: false, removeFollower: true);
        Assert.Equal(26, main.Select(row => row.Uid).Distinct().Count());
        Assert.Equal(24, candidate.Select(row => row.Uid).Distinct().Count());
        Assert.Equal(24, main.Select(row => row.Uid).Intersect(candidate.Select(row => row.Uid)).Count());

        var mainSnapshot = TestCoverageInvariant.ParseDiscovery(Discovery(main, _root), false, _root);
        var candidateSnapshot = TestCoverageInvariant.ParseDiscovery(Discovery(candidate, _root), false, _root);

        Assert.Equal(27, mainSnapshot.Tests.Count);
        Assert.Equal(25, candidateSnapshot.Tests.Count);
        Assert.Equal(3, mainSnapshot.Tests.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var group in main.GroupBy(row => row.DisplayName))
        {
            Assert.Equal(13, group.Count());
            Assert.Equal(13, mainSnapshot.Tests.Count(test => test == group.Key));
            Assert.Equal(12, candidateSnapshot.Tests.Count(test => test == group.Key));
            Assert.Equal(SourceFile, mainSnapshot.SourceFilesByTest![group.Key]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compare_PartialCollisions_CreditsZeroAndKeepsMainFloor(bool customName)
    {
        var coverage = await CompareAsync(truncated: true, customName: customName);

        // This pins the current failure, not the desired final removal behavior.
        Assert.False(coverage.Passed);
        AssertCredit(coverage, 0);
        Assert.Equal(new TestCoverageCountComparison(25, 27, 27, 0, true), coverage.CountComparison);
        Assert.Equal(25, coverage.ExecutedTests.Count);
        Assert.Empty(coverage.EmptyPartitions);
        Assert.Equal("cross-generation-count:candidate=25,minimum=27,main=27,deleted=0",
            Assert.Single(coverage.MissingTests));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compare_UntruncatedRows_CreditsExactlyTwoAbsentVariants(bool customName)
    {
        // Paired control: identical source delta, UIDs and receipts; only names are untruncated.
        var coverage = await CompareAsync(truncated: false, customName: customName);

        Assert.True(coverage.Passed, coverage.Summary);
        AssertCredit(coverage, 2);
        Assert.Equal(new TestCoverageCountComparison(25, 25, 27, 2, false), coverage.CountComparison);
        Assert.Equal(25, coverage.ExecutedTests.Count);
        Assert.Contains(Survivor, coverage.ExecutedTests);
        Assert.Empty(coverage.MissingTests);
    }

    [Fact]
    public async Task Compare_AllCollidingRowsRetained_CreditsZero()
    {
        var coverage = await CompareAsync(truncated: true, customName: false, removeFollower: false);

        Assert.True(coverage.Passed, coverage.Summary);
        AssertCredit(coverage, 0);
        Assert.Equal(new TestCoverageCountComparison(27, 27, 27, 0, false), coverage.CountComparison);
        Assert.Equal(27, coverage.ExecutedTests.Count);
        Assert.Empty(coverage.MissingTests);
    }

    [Fact]
    public async Task Compare_OneCollisionReceiptMissing_DoesNotReuseAnotherReceipt()
    {
        var coverage = await CompareAsync(truncated: true, customName: false, omitCollisionReceipt: true);

        Assert.False(coverage.Passed);
        AssertCredit(coverage, 0);
        Assert.Equal(new TestCoverageCountComparison(25, 27, 27, 0, true), coverage.CountComparison);
        Assert.Equal(24, coverage.ExecutedTests.Count);
        Assert.Equal(2, coverage.MissingTests.Count);
        Assert.Contains(DisplayName(PathPrefix + RemovedFile, truncated: true, customName: false),
            coverage.MissingTests);
        Assert.Contains(Survivor, coverage.ExecutedTests);
    }

    private async Task<TestCoverageInvariantResult> CompareAsync(
        bool truncated, bool customName, bool removeFollower = true, bool omitCollisionReceipt = false)
    {
        var mainRoot = Path.Combine(_root, "main");
        var candidateRoot = Path.Combine(_root, "candidate");
        Directory.CreateDirectory(mainRoot);
        Directory.CreateDirectory(candidateRoot);
        File.WriteAllText(Path.Combine(mainRoot, SourceFile), Source(customName, removeFollower: false));
        File.WriteAllText(Path.Combine(candidateRoot, SourceFile), Source(customName, removeFollower));
        var main = Rows(truncated, customName, removeFollower: false);
        var candidate = Rows(truncated, customName, removeFollower);
        var trx = Path.Combine(candidateRoot, "results.trx");
        string[] executed = [Survivor, .. candidate.Skip(omitCollisionReceipt ? 1 : 0)
            .Select(row => row.DisplayName)];
        new XDocument(new XElement("TestRun", new XElement("Results", executed.Select((test, index) =>
            new XElement("UnitTestResult", new XAttribute("testId", index), new XAttribute("testName", test),
                new XAttribute("outcome", "Passed")))))).Save(trx);

        var discoveryCalls = new List<string>();
        var evaluator = new AcceptanceStructuralCoverageEvaluator((_, directory, _, _) =>
        {
            Assert.Contains(directory, new[] { mainRoot, candidateRoot });
            discoveryCalls.Add(directory);
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0,
                Discovery(directory == mainRoot ? main : candidate, directory)));
        });
        var request = new AcceptanceStructuralCoverageRequest(
            CandidateDiscoveryArguments: ["candidate"], CandidateWorktreePath: candidateRoot,
            DiscoveryTimeout: TimeSpan.FromMinutes(1), BareTestList: false,
            ResolvePartitions: () => [new TestPartitionCoverage("fixture", true, [trx])],
            ResolveDeletedTestFiles: () => [], CurrentAttemptId: null, SanctionedRemovedTests: [Declaration],
            PrepareBaseline: _ => Task.FromResult<AcceptanceStructuralCoverageBaseline?>(
                new(["main"], mainRoot, mainRoot, false, false)));
        var prepared = await evaluator.PrepareAsync(request, CancellationToken.None);
        Assert.Equal(27, prepared.BaselineSnapshot!.Tests.Count);
        Assert.Contains(SourceFile, prepared.BaselineSnapshot.SourceFilesByTest!.Values);
        // The actual resolver must see baseline source. An omitted mapping would make the alias
        // control credit zero, so it cannot silently exercise only the qualified-method fallback.
        Assert.NotNull(prepared.DeclaredRemovalDisplayNames);
        if (customName)
            Assert.Equal(Alias, Assert.Single(prepared.DeclaredRemovalDisplayNames[Declaration]));
        else
            Assert.Empty(prepared.DeclaredRemovalDisplayNames[Declaration]);

        var evaluation = await evaluator.CompareAsync(request, prepared, CancellationToken.None);
        Assert.Equal(2, discoveryCalls.Count);
        Assert.Equal(1, discoveryCalls.Count(directory => directory == mainRoot));
        Assert.Equal(1, discoveryCalls.Count(directory => directory == candidateRoot));
        return Assert.IsType<TestCoverageInvariantResult>(evaluation.Coverage);
    }

    private static Row[] Rows(bool truncated, bool customName, bool removeFollower) =>
        Files.Where(file => !removeFollower || file != RemovedFile)
            .SelectMany(file => new[] { false, true }.Select(upper =>
            {
                var path = PathPrefix + file;
                if (upper) path = path.Replace('/', '\\').ToUpperInvariant();
                // Synthetic row keys are stable by construction, not evidence of real UID stability.
                return new Row($"fixture:{file}:{upper}", DisplayName(path, truncated, customName));
            })).ToArray();

    private static string DisplayName(string path, bool truncated, bool customName)
    {
        var argument = path.Replace("\\", "\\\\");
        if (truncated) argument = argument[..50];
        return $"{(customName ? Alias : Declaration)}(path: \"{argument}\"{(truncated ? "···" : "")})";
    }

    private static string Discovery(Row[] rows, string root) => JsonSerializer.Serialize(new
    {
        schemaVersion = "1.0",
        tests = new[] { new Row("fixture:survivor", Survivor) }.Concat(rows).Select(row => new
        {
            uid = row.Uid,
            displayName = row.DisplayName,
            type = new
            {
                assemblyFullName = "Fixture, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
                typeName = row.DisplayName == Survivor ? "OtherTests" : "AdmissionRows",
                methodName = row.DisplayName == Survivor ? "NonFollowerSurvives" : Method,
                methodArity = 0,
                returnTypeFullName = "System.Void",
                parameterTypeFullNames = row.DisplayName == Survivor ? Array.Empty<string>() : ["System.String"]
            },
            location = new { file = Path.Combine(root, SourceFile), lineStart = 31, lineEnd = 31 }
        })
    });

    private static string Source(bool customName, bool removeFollower)
    {
        var paths = string.Join(",\n", Files.Where(file => !removeFollower || file != RemovedFile)
            .Select(file => $"\"{PathPrefix + file}\""));
        var attribute = customName ? $"[Xunit.Theory(DisplayName = \"{Alias}\")]" : "[Xunit.Theory]";
        return $$"""
            class AdmissionRows
            {
                public static System.Collections.Generic.IEnumerable<object[]> AdmissionPaths()
                {
                    string[] paths = [{{paths}}];
                    foreach (var path in paths)
                    {
                        yield return [path];
                        yield return [path.Replace('/', '\\').ToUpperInvariant()];
                    }
                }
                {{attribute}}
                [Xunit.MemberData(nameof(AdmissionPaths))]
                public void {{Method}}(string path) { }
            }
            """;
    }

    private static void AssertCredit(TestCoverageInvariantResult coverage, int credit) =>
        Assert.Contains($"cross-generation-attribution:source=declared-test-removal,identity={Declaration}," +
            $"status={(credit > 0 ? "corroborated" : "unmatched")},credit={credit}", coverage.Summary);

    private sealed record Row(string Uid, string DisplayName);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
