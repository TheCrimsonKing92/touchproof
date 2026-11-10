using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: discovery is injected; sources and TRX receipts use a private temporary root.
// The pre-correction characterization receipt is retained in goal/deferred-no-change-receipts.
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
    public void Parse_CollidingRows_RetainsDistinctUidsAndOccurrences()
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
        Assert.Equal(27, mainSnapshot.Rows!.Select(row => row.Uid).Distinct().Count());
        Assert.Equal(main.Select(row => row.Uid), mainSnapshot.Rows.Skip(1).Select(row => row.Uid));
        Assert.All(mainSnapshot.Rows.Skip(1), row =>
        {
            Assert.Equal("AdmissionRows", row.ClassName);
            Assert.Equal(Method, row.MethodName);
            Assert.True(row.IsSingleStringArgument);
        });
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
    public async Task Compare_PartialCollisions_CreditsOnlyTwoRemovedRows(bool customName)
    {
        var coverage = await CompareAsync(truncated: true, customName: customName);

        Assert.True(coverage.Passed, coverage.Summary);
        AssertCredit(coverage, 2);
        Assert.Equal(new TestCoverageCountComparison(25, 25, 27, 2, false), coverage.CountComparison);
        Assert.Equal(25, coverage.ExecutedTests.Count);
        foreach (var group in Rows(true, customName, true).GroupBy(row => row.DisplayName))
            Assert.Equal(12, coverage.ExecutedTests.Count(test => test == group.Key));
        Assert.Contains(Survivor, coverage.ExecutedTests);
        Assert.Empty(coverage.EmptyPartitions);
        Assert.Empty(coverage.MissingTests);
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
        AssertCredit(coverage, 2);
        Assert.Equal(new TestCoverageCountComparison(25, 25, 27, 2, false), coverage.CountComparison);
        Assert.Equal(24, coverage.ExecutedTests.Count);
        Assert.Single(coverage.MissingTests);
        Assert.Contains(DisplayName(PathPrefix + RemovedFile, truncated: true, customName: false),
            coverage.MissingTests);
        Assert.Contains(Survivor, coverage.ExecutedTests);
    }

    [Theory]
    [InlineData("retained-source")]
    [InlineData("missing-main-source")]
    [InlineData("missing-candidate-source")]
    [InlineData("dynamic-producer")]
    [InlineData("source-overclaim")]
    [InlineData("discovery-overclaim")]
    [InlineData("retained-row-substitution")]
    [InlineData("duplicate-uid")]
    [InlineData("missing-uid")]
    [InlineData("uid-churn")]
    [InlineData("uid-case")]
    [InlineData("wrong-method")]
    [InlineData("wrong-source")]
    [InlineData("wrong-assembly")]
    [InlineData("source-outside-root")]
    [InlineData("missing-assembly-path")]
    [InlineData("undeclared")]
    public async Task Compare_UnprovenCollisionDelta_CreditsZero(string fault)
    {
        var coverage = await CompareAsync(truncated: true, customName: false, fault: fault);

        Assert.False(coverage.Passed);
        Assert.Equal(0, coverage.CountComparison!.DeletedCount);
        Assert.Equal(27, coverage.CountComparison.MinimumCandidateCount);
        Assert.True(coverage.CountComparison.IsShortfall);
        if (fault != "undeclared") AssertCredit(coverage, 0);
    }

    [Fact]
    public async Task Compare_NonFollowerMissing_KeepsIndependentCountShortfall()
    {
        var coverage = await CompareAsync(truncated: true, customName: false, fault: "missing-non-follower");

        Assert.False(coverage.Passed);
        AssertCredit(coverage, 2);
        Assert.Equal(new TestCoverageCountComparison(24, 25, 27, 2, true), coverage.CountComparison);
        Assert.Equal("cross-generation-count:candidate=24,minimum=25,main=27,deleted=2",
            Assert.Single(coverage.MissingTests));
    }

    [Fact]
    public async Task Compare_LinqPathProducer_CreditsExactSourceRows()
    {
        var coverage = await CompareAsync(truncated: true, customName: false, fault: "linq-producer");

        Assert.True(coverage.Passed, coverage.Summary);
        AssertCredit(coverage, 2);
        Assert.Equal(new TestCoverageCountComparison(25, 25, 27, 2, false), coverage.CountComparison);
    }

    [Fact]
    public async Task Compare_ContainedBaseline_RebindsSourceAndDiscoveryIdentities()
    {
        var coverage = await CompareAsync(truncated: true, customName: false, fault: "contained-baseline");

        Assert.True(coverage.Passed, coverage.Summary);
        AssertCredit(coverage, 2);
        Assert.Contains("disposition=integration-stale", coverage.Summary);
    }

    private async Task<TestCoverageInvariantResult> CompareAsync(
        bool truncated, bool customName, bool removeFollower = true, bool omitCollisionReceipt = false,
        string? fault = null)
    {
        var mainRoot = Path.Combine(_root, "main");
        var candidateRoot = Path.Combine(_root, "candidate");
        var containedRoot = Path.Combine(_root, "contained");
        Directory.CreateDirectory(mainRoot);
        Directory.CreateDirectory(candidateRoot);
        var mainSource = Source(customName, removeFollower: false);
        var candidateSource = Source(customName, fault == "retained-source" ? false : removeFollower);
        if (fault == "dynamic-producer") candidateSource = candidateSource.Replace("ToUpperInvariant()", "ToUpper()");
        if (fault == "source-overclaim") candidateSource = candidateSource.Replace($"\"{PathPrefix + Files[0]}\",", "");
        if (fault == "linq-producer")
        {
            mainSource = LinqSource(mainSource);
            candidateSource = LinqSource(candidateSource);
        }
        if (fault == "contained-baseline")
        {
            Directory.CreateDirectory(containedRoot);
            File.WriteAllText(Path.Combine(containedRoot, SourceFile), mainSource);
            mainSource = mainSource.Replace("ToUpperInvariant()", "ToUpper()");
        }
        if (fault != "missing-main-source") File.WriteAllText(Path.Combine(mainRoot, SourceFile), mainSource);
        if (fault != "missing-candidate-source") File.WriteAllText(Path.Combine(candidateRoot, SourceFile), candidateSource);
        var mainAssembly = Path.Combine(mainRoot, "Fixture.dll");
        var candidateAssembly = Path.Combine(candidateRoot, "Fixture.dll");
        var main = Rows(truncated, customName, removeFollower: false, mainAssembly);
        var candidate = Rows(truncated, customName, removeFollower, candidateAssembly);
        var containedAssembly = Path.Combine(containedRoot, "Fixture.dll");
        var containedRows = fault == "contained-baseline" ? Rows(truncated, customName, false, containedAssembly) : [];
        // Framework-generated keys are independently reconstructed from source by production.
        // Separate assembly paths deliberately change every UID between generations.
        Assert.Empty(main.Select(row => row.Uid).Intersect(candidate.Select(row => row.Uid)));
        if (fault == "retained-row-substitution") candidate = Rows(truncated, customName, false, candidateAssembly)
            .Where(row => !row.Uid.Equals(FrameworkUid(candidateAssembly, PathPrefix + Files[0])) &&
                !row.Uid.Equals(FrameworkUid(candidateAssembly, (PathPrefix + Files[0]).Replace('/', '\\').ToUpperInvariant())))
            .ToArray();
        if (fault == "discovery-overclaim") candidate = candidate.Skip(2).ToArray();
        if (fault == "duplicate-uid") candidate[0] = candidate[0] with { Uid = candidate[1].Uid };
        if (fault == "missing-uid") candidate[0] = candidate[0] with { Uid = "" };
        if (fault == "uid-churn") candidate[0] = candidate[0] with { Uid = "unbound-key" };
        if (fault == "uid-case") candidate[0] = candidate[0] with { Uid = candidate[0].Uid.ToUpperInvariant() };
        var trx = Path.Combine(candidateRoot, "results.trx");
        string[] executed = [Survivor, .. candidate.Skip(omitCollisionReceipt ? 1 : 0)
            .Select(row => row.DisplayName)];
        new XDocument(new XElement("TestRun", new XElement("Results", executed.Select((test, index) =>
            new XElement("UnitTestResult", new XAttribute("testId", index), new XAttribute("testName", test),
                new XAttribute("outcome", "Passed")))))).Save(trx);

        var discoveryCalls = new List<string>();
        var evaluator = new AcceptanceStructuralCoverageEvaluator((_, directory, _, _) =>
        {
            Assert.Contains(directory, new[] { mainRoot, candidateRoot, containedRoot });
            discoveryCalls.Add(directory);
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0,
                Discovery(directory == mainRoot ? main : directory == containedRoot ? containedRows : candidate, directory,
                    directory == candidateRoot ? fault : null)));
        });
        var request = new AcceptanceStructuralCoverageRequest(
            CandidateDiscoveryArguments: fault == "missing-assembly-path" ? ["candidate"] : ["dotnet", candidateAssembly],
            CandidateWorktreePath: candidateRoot,
            DiscoveryTimeout: TimeSpan.FromMinutes(1), BareTestList: false,
            ResolvePartitions: () => [new TestPartitionCoverage("fixture", true, [trx])],
            ResolveDeletedTestFiles: () => [], CurrentAttemptId: null,
            SanctionedRemovedTests: fault == "undeclared" ? [] : [Declaration],
            PrepareBaseline: _ => Task.FromResult<AcceptanceStructuralCoverageBaseline?>(
                new(["dotnet", mainAssembly], mainRoot, mainRoot, false, false)),
            PrepareContainedBaseline: fault == "contained-baseline" ? _ =>
            {
                var contained = new AcceptanceContainedGenerationBaseline("aaaaaaaa", "bbbbbbbb", containedRoot,
                    useObservedBaseline: false, unresolvedReason: null);
                contained.SetBaseline(new(["dotnet", containedAssembly], containedRoot, containedRoot, false, false));
                return Task.FromResult(contained);
            } : null);
        var prepared = await evaluator.PrepareAsync(request, CancellationToken.None);
        Assert.Equal(27, prepared.BaselineSnapshot!.Tests.Count);
        Assert.Contains(SourceFile, prepared.BaselineSnapshot.SourceFilesByTest!.Values);
        // The actual resolver must see baseline source. An omitted mapping would make the alias
        // control credit zero, so it cannot silently exercise only the qualified-method fallback.
        Assert.NotNull(prepared.DeclaredRemovalDisplayNames);
        if (customName && fault != "undeclared")
            Assert.Equal(Alias, Assert.Single(prepared.DeclaredRemovalDisplayNames[Declaration]));
        else if (fault != "undeclared")
            Assert.Empty(prepared.DeclaredRemovalDisplayNames[Declaration]);

        if (fault is null && removeFollower)
        {
            var candidateSnapshot = TestCoverageInvariant.ParseDiscovery(Discovery(candidate, candidateRoot), false, candidateRoot);
            var proof = SourceConfirmedTheoryRowRemovals.Resolve(prepared.BaselineSnapshot, candidateSnapshot,
                mainRoot, candidateRoot, [Declaration], ["dotnet", mainAssembly], ["dotnet", candidateAssembly]);
            var removedRows = proof[Declaration].Select(index => prepared.BaselineSnapshot.Rows![index]).ToArray();
            var path = PathPrefix + RemovedFile;
            var expected = new[] { FrameworkUid(mainAssembly, path),
                FrameworkUid(mainAssembly, path.Replace('/', '\\').ToUpperInvariant()) };
            Assert.Equal(expected.Order(StringComparer.Ordinal), removedRows.Select(row => row.Uid).Order(StringComparer.Ordinal));
            Assert.Equal(2, removedRows.Length);
            Assert.Equal(24, prepared.BaselineSnapshot.Rows!.Skip(1).Count(row => !expected.Contains(row.Uid)));
        }

        var evaluation = await evaluator.CompareAsync(request, prepared, CancellationToken.None);
        Assert.Equal(fault == "contained-baseline" ? 3 : 2, discoveryCalls.Count);
        Assert.Equal(1, discoveryCalls.Count(directory => directory == mainRoot));
        Assert.Equal(1, discoveryCalls.Count(directory => directory == candidateRoot));
        return Assert.IsType<TestCoverageInvariantResult>(evaluation.Coverage);
    }

    private static Row[] Rows(bool truncated, bool customName, bool removeFollower, string? assemblyPath = null) =>
        Files.Where(file => !removeFollower || file != RemovedFile)
            .SelectMany(file => new[] { false, true }.Select(upper =>
            {
                var path = PathPrefix + file;
                if (upper) path = path.Replace('/', '\\').ToUpperInvariant();
                return new Row(assemblyPath is null ? $"fixture:{file}:{upper}" : FrameworkUid(assemblyPath, path),
                    DisplayName(path, truncated, customName));
            })).ToArray();

    private static string DisplayName(string path, bool truncated, bool customName)
    {
        var argument = path.Replace("\\", "\\\\");
        if (truncated) argument = argument[..50];
        return $"{(customName ? Alias : Declaration)}(path: \"{argument}\"{(truncated ? "···" : "")})";
    }

    private static string Discovery(Row[] rows, string root, string? fault = null) => JsonSerializer.Serialize(new
    {
        schemaVersion = "1.0",
        tests = (fault == "missing-non-follower" ? rows : new[] { new Row("fixture:survivor", Survivor) }.Concat(rows))
            .Select(row => new
        {
            uid = row.Uid,
            displayName = row.DisplayName,
            type = new
            {
                assemblyFullName = fault == "wrong-assembly" ? "Wrong, Version=1.0.0.0" :
                    "Fixture, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
                typeName = row.DisplayName == Survivor ? "OtherTests" : "AdmissionRows",
                methodName = fault == "wrong-method" ? "OtherMethod" : row.DisplayName == Survivor ? "NonFollowerSurvives" : Method,
                methodArity = 0,
                returnTypeFullName = "System.Void",
                parameterTypeFullNames = row.DisplayName == Survivor ? Array.Empty<string>() : ["System.String"]
            },
            location = new { file = fault == "source-outside-root" ? Path.Combine(root, "..", SourceFile) :
                Path.Combine(root, fault == "wrong-source" ? "Other.cs" : SourceFile), lineStart = 31, lineEnd = 31 }
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

    private static string FrameworkUid(string assemblyPath, string argument)
    {
        using var typeIdentity = new Xunit.Sdk.UniqueIDGenerator();
        typeIdentity.Add("Fixture");
        typeIdentity.Add("AdmissionRows");
        var collection = Xunit.Sdk.UniqueIDGenerator.ForTestCollection(
            Xunit.Sdk.UniqueIDGenerator.ForAssembly(assemblyPath, null),
            $"Test collection for AdmissionRows (id: {typeIdentity.Compute()})", null);
        var type = Xunit.Sdk.UniqueIDGenerator.ForTestClass(collection, "AdmissionRows");
        var method = Xunit.Sdk.UniqueIDGenerator.ForTestMethod(type, Method)!;
        return Xunit.Sdk.UniqueIDGenerator.ForTestCase(method, null, [argument]);
    }

    private static string LinqSource(string source) => source.Replace("string[] paths", "string[] appFiles")
        .Replace($"\"{PathPrefix}", "\"").Replace($"\"{Files[^1]}\"", "")
        .Replace("foreach (var path in paths)", $$"""
            var paths = appFiles.Select(name => "{{PathPrefix}}" + name)
                .Concat(new[] { "{{Files[^1]}}" }.Select(name => "{{PathPrefix}}" + name));
            foreach (var path in paths)
            """);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
