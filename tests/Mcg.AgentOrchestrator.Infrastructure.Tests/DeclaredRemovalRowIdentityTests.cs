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

    [Theory]
    [InlineData("format-lf")]
    [InlineData("format-crlf")]
    [InlineData("format-compact")]
    public async Task Compare_ProducerFormatting_CreditsExactSourceRows(string fault)
    {
        var coverage = await CompareAsync(truncated: true, customName: false, fault: fault);

        Assert.True(coverage.Passed, coverage.Summary);
        AssertCredit(coverage, 2);
        Assert.Equal(new TestCoverageCountComparison(25, 25, 27, 2, false), coverage.CountComparison);
        Assert.Equal(25, coverage.ExecutedTests.Count);
        Assert.Contains(Survivor, coverage.ExecutedTests);
        Assert.Empty(coverage.MissingTests);
    }

    [Fact]
    public async Task Compare_ContainedBaseline_RebindsSourceAndDiscoveryIdentities()
    {
        var coverage = await CompareAsync(truncated: true, customName: false, fault: "contained-baseline");

        Assert.True(coverage.Passed, coverage.Summary);
        AssertCredit(coverage, 2);
        Assert.Contains("disposition=integration-stale", coverage.Summary);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Compare_CachedBaselineOrigin_PreservesRowCredit(
        bool relocate, bool explicitIdentity)
    {
        // Reuse the recorded identity even after A disappears and B's sidecar changes.
        var evaluation = await CompareCachedAsync(relocate, explicitIdentity);
        var coverage = Assert.IsType<TestCoverageInvariantResult>(evaluation.Coverage);

        Assert.True(coverage.Passed);
        AssertCredit(coverage, 2);
        Assert.Equal(new TestCoverageCountComparison(25, 25, 27, 2, false), coverage.CountComparison);
        Assert.Equal(25, coverage.ExecutedTests.Count);
        Assert.Contains(Survivor, coverage.ExecutedTests);
        foreach (var group in Rows(true, false, true).GroupBy(row => row.DisplayName))
            Assert.Equal(12, coverage.ExecutedTests.Count(test => test == group.Key));
        Assert.Empty(coverage.EmptyPartitions);
        Assert.Empty(coverage.MissingTests);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Compare_CachedBaseline_MissingSurvivorStillFails(
        bool relocate, bool explicitIdentity)
    {
        var evaluation = await CompareCachedAsync(relocate, explicitIdentity, omitCollisionReceipt: true);
        var coverage = Assert.IsType<TestCoverageInvariantResult>(evaluation.Coverage);

        Assert.False(coverage.Passed);
        AssertCredit(coverage, 2);
        Assert.Equal(24, coverage.ExecutedTests.Count);
        Assert.Contains(Survivor, coverage.ExecutedTests);
        Assert.Contains(DisplayName(PathPrefix + Files[0], true, false), coverage.MissingTests);
        Assert.Single(coverage.MissingTests);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("stale")]
    public async Task Compare_InvalidBaselineCache_RejectsEntryAndDiscoveryFailure(string fault)
    {
        var evaluation = await CompareCachedAsync(relocate: true, explicitIdentity: false, cacheFault: fault);

        Assert.Equal(0, evaluation.CandidateDiscovery.ExitCode);
        Assert.Equal(1, evaluation.BaselineDiscovery!.ExitCode);
        Assert.Equal("fresh baseline discovery failed", evaluation.BaselineDiscovery.Output);
        Assert.Null(evaluation.Coverage);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong")]
    [InlineData("empty")]
    public async Task Compare_UnprovenCacheOrigin_GrantsNoRowCredit(string originFault)
    {
        var evaluation = await CompareCachedAsync(relocate: true, explicitIdentity: false, originFault: originFault);
        var coverage = Assert.IsType<TestCoverageInvariantResult>(evaluation.Coverage);

        Assert.False(coverage.Passed);
        AssertCredit(coverage, 0);
        Assert.Equal(new TestCoverageCountComparison(25, 27, 27, 0, true), coverage.CountComparison);
    }

    private async Task<AcceptanceStructuralCoverageEvaluation> CompareCachedAsync(
        bool relocate, bool explicitIdentity, bool omitCollisionReceipt = false, string? cacheFault = null,
        string? originFault = null)
    {
        var mainRoot = Path.Combine(_root, "main");
        var candidateRoot = Path.Combine(_root, "candidate");
        Directory.CreateDirectory(mainRoot);
        Directory.CreateDirectory(candidateRoot);
        File.WriteAllText(Path.Combine(mainRoot, SourceFile), Source(false, false));
        File.WriteAllText(Path.Combine(candidateRoot, SourceFile), Source(false, true));
        // One source generation and manifest for both cache requests; only artifacts move.
        var manifestPath = Path.Combine(mainRoot, "acceptance-manifest.json");
        File.WriteAllText(manifestPath, "{\"testProjects\":[\"Fixture.csproj\"]}");
        string SourceDigest() => MainBaselineDiscoveryCache.Hash(
            File.ReadAllText(Path.Combine(mainRoot, SourceFile)) + File.ReadAllText(manifestPath));
        var digest = SourceDigest();
        var outputA = Path.Combine(_root, "outputA");
        var outputB = relocate ? Path.Combine(_root, "outputB") : outputA;
        var assemblyA = Path.Combine(outputA, "Fixture.dll");
        var assemblyB = Path.Combine(outputB, "Fixture.dll");
        var candidateAssembly = Path.Combine(candidateRoot, "Fixture.dll");
        const string assemblyIdentity = "fixture-explicit-assembly-identity";
        foreach (var assembly in new[] { assemblyA, assemblyB }.Distinct())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(assembly)!);
            if (explicitIdentity) File.WriteAllText(Path.ChangeExtension(assembly, ".uniqueid"), assemblyIdentity);
            else Assert.False(File.Exists(Path.ChangeExtension(assembly, ".uniqueid")));
        }
        string[] Arguments(string assembly) => ["dotnet", assembly, "--list-tests", "json"];
        var argumentsA = Arguments(assemblyA);
        var argumentsB = Arguments(assemblyB);
        var main = Rows(true, false, false, assemblyA, explicitIdentity ? assemblyIdentity : null);
        var candidate = Rows(true, false, true, candidateAssembly);
        var clock = new CacheClock();
        var cache = new MainBaselineDiscoveryCache(Path.Combine(_root, "cache"), clock: clock);
        var keyA = MainBaselineDiscoveryCache.CreateKey(digest, mainRoot, "Fixture.csproj", "Debug", argumentsA, outputA);
        var trustedDiscovery = new GoalAcceptanceVerifier.CommandResult(0, Discovery(main, mainRoot));
        var originIdentities = XunitStringTheoryIdentity.CaptureAssemblyIdentities(argumentsA, mainRoot);
        Assert.Single(originIdentities);
        cache.TryWrite(keyA, trustedDiscovery, originFault switch
        {
            "missing" => null,
            "wrong" => new Dictionary<string, string> { ["Fixture"] = "wrong-assembly-identity" },
            "empty" => new Dictionary<string, string>(),
            _ => originIdentities
        });
        Assert.Equal(trustedDiscovery, cache.TryRead(keyA));
        if (relocate)
        {
            var resolvedOrigin = Path.GetFullPath(outputA);
            Assert.StartsWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar, resolvedOrigin);
            Directory.Delete(resolvedOrigin, recursive: true);
            Assert.False(Directory.Exists(outputA));
            File.WriteAllText(Path.ChangeExtension(assemblyB, ".uniqueid"), "changed-at-output-B");
        }
        var keyB = MainBaselineDiscoveryCache.CreateKey(SourceDigest(), mainRoot, "Fixture.csproj", "Debug", argumentsB, outputB);
        Assert.Equal(digest, SourceDigest());
        Assert.Equal(keyA, keyB);
        if (cacheFault == "malformed")
            File.WriteAllText(Path.Combine(_root, "cache", keyA.EntryName + ".json"), "{");
        if (cacheFault == "stale") clock.Now += TimeSpan.FromDays(8);
        var cached = cache.TryRead(keyB, out var cachedIdentities);
        if (cacheFault is null)
        {
            Assert.NotNull(cached);
            Assert.Equal(Discovery(main, mainRoot), cached.Output);
            if (originFault is null)
                Assert.Equal(originIdentities["Fixture"], cachedIdentities!["Fixture"]);
        }
        else Assert.Null(cached);

        var trx = Path.Combine(candidateRoot, "cached-results.trx");
        string[] executed = [Survivor, .. candidate.Skip(omitCollisionReceipt ? 1 : 0).Select(row => row.DisplayName)];
        new XDocument(new XElement("TestRun", new XElement("Results", executed.Select((test, index) =>
            new XElement("UnitTestResult", new XAttribute("testId", index), new XAttribute("testName", test),
                new XAttribute("outcome", "Passed")))))).Save(trx);
        var calls = new List<string>();
        var evaluator = new AcceptanceStructuralCoverageEvaluator((arguments, directory, _, _) =>
        {
            calls.Add(directory);
            if (directory == candidateRoot)
            {
                Assert.Equal(Arguments(candidateAssembly), arguments);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, Discovery(candidate, candidateRoot)));
            }
            Assert.Equal(mainRoot, directory);
            Assert.Equal(argumentsB, arguments);
            Assert.NotNull(cacheFault); // A hit must bypass fresh baseline discovery entirely.
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(1, "fresh baseline discovery failed"));
        });
        var request = new AcceptanceStructuralCoverageRequest(
            Arguments(candidateAssembly), candidateRoot, TimeSpan.FromMinutes(1), false,
            () => [new TestPartitionCoverage("fixture", true, [trx])], () => [], null, [Declaration],
            _ => Task.FromResult<AcceptanceStructuralCoverageBaseline?>(
                new(argumentsB, mainRoot, mainRoot, false, false, CachedDiscovery: cached,
                    CachedAssemblyIdentities: cachedIdentities)));
        var prepared = await evaluator.PrepareAsync(request, CancellationToken.None);
        if (cacheFault is null)
        {
            Assert.Equal(argumentsB, prepared.BaselineDiscoveryArguments);
            Assert.Same(cachedIdentities, prepared.BaselineAssemblyIdentities);
            var snapshot = Assert.IsType<TestDiscoverySnapshot>(prepared.BaselineSnapshot);
            Assert.Equal(27, snapshot.Rows!.Count);
            Assert.Equal(27, snapshot.Rows.Select(row => row.Uid).Distinct().Count());
            var candidateSnapshot = TestCoverageInvariant.ParseDiscovery(Discovery(candidate, candidateRoot), false, candidateRoot);
            var proof = SourceConfirmedTheoryRowRemovals.Resolve(snapshot, candidateSnapshot,
                mainRoot, candidateRoot, [Declaration], argumentsB, Arguments(candidateAssembly),
                prepared.BaselineAssemblyIdentities);
            if (originFault is not null) Assert.Empty(proof);
            else
            {
                var removedPath = PathPrefix + RemovedFile;
                var expected = new[] { removedPath, removedPath.Replace('/', '\\').ToUpperInvariant() }
                    .Select(path => FrameworkUid(assemblyA, path, explicitIdentity ? assemblyIdentity : null)).ToArray();
                Assert.Equal(expected.Order(StringComparer.Ordinal),
                    proof[Declaration].Select(index => snapshot.Rows[index].Uid).Order(StringComparer.Ordinal));
                Assert.Equal(24, snapshot.Rows.Skip(1).Count(row => !expected.Contains(row.Uid)));
            }
        }
        var result = await evaluator.CompareAsync(request, prepared, CancellationToken.None);
        Assert.Equal(cacheFault is null ? new[] { candidateRoot } : new[] { candidateRoot, mainRoot }, calls);
        Assert.Equal(digest, SourceDigest());
        return result;
    }

    private sealed class CacheClock : TimeProvider
    {
        internal DateTimeOffset Now = new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
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
        if (fault is "format-lf" or "format-crlf" or "format-compact")
        {
            mainSource = mainSource.ReplaceLineEndings("\r\n");
            candidateSource = candidateSource.ReplaceLineEndings(fault switch
            {
                "format-lf" => "\n",
                "format-crlf" => "\r\n",
                _ => " "
            });
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

    private static Row[] Rows(bool truncated, bool customName, bool removeFollower, string? assemblyPath = null,
        string? assemblyIdentity = null) =>
        Files.Where(file => !removeFollower || file != RemovedFile)
            .SelectMany(file => new[] { false, true }.Select(upper =>
            {
                var path = PathPrefix + file;
                if (upper) path = path.Replace('/', '\\').ToUpperInvariant();
                return new Row(assemblyPath is null ? $"fixture:{file}:{upper}" : FrameworkUid(assemblyPath, path, assemblyIdentity),
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

    private static string FrameworkUid(string assemblyPath, string argument, string? assemblyIdentity = null)
    {
        using var typeIdentity = new Xunit.Sdk.UniqueIDGenerator();
        typeIdentity.Add("Fixture");
        typeIdentity.Add("AdmissionRows");
        var collection = Xunit.Sdk.UniqueIDGenerator.ForTestCollection(
            assemblyIdentity ?? Xunit.Sdk.UniqueIDGenerator.ForAssembly(assemblyPath, null),
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
