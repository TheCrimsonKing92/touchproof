using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure classifier inputs and individually owned source roots and census files; fixed clock.
public sealed class ApparatusRedClassifierSourceReadingGuardTests
{
    private const string Identity = "Sample.Tests.GuardTests.FilesAreValid";
    private const string TestPath = "tests/Sample.Tests/GuardTests.cs";
    private const string ChangedPath = "src/Changed.cs";
    private const string SlotsBusy = "DotnetBuildSlotsBusyException";
    private const string PlainSource =
        "namespace Sample.Tests; public sealed class GuardTests { object subject = new object(); }";
    private const string ReadingSource = """
        namespace Sample.Tests;
        public sealed class GuardTests
        {
            public void FilesAreValid()
            {
                var root = VerifiedRepositoryRoot.Find();
                foreach (var path in Directory.EnumerateFiles(root, "*.cs"))
                    Assert.Empty(File.ReadAllText(path));
            }
        }
        """;

    [Theory]
    [InlineData(SlotsBusy, true)]
    [InlineData(SlotsBusy, false)]
    [InlineData("BuildLockBlockedException", true)]
    [InlineData("BuildLockBlockedException", false)]
    [InlineData("AcceptanceInfrastructureDeferredException", true)]
    [InlineData("AcceptanceInfrastructureDeferredException", false)]
    public void BaselineNotExecuted_RespectsSourceReadingFlag(string signature, bool readsSources)
    {
        var baselineEvidence = $"focused baseline execution failed: {signature}";
        var failure = FailingTest(readsSources) with
        {
            BaselineAttribution = new AcceptanceTestFailureAttribution(
                Identity, AcceptanceTestFailureOrigin.Unattributed, baselineEvidence)
        };

        var disposition = Classify(failure);

        if (readsSources)
        {
            var genuine = Assert.IsType<ApparatusRedDisposition.Genuine>(disposition);
            AssertGuardReason(genuine);
            Assert.Contains(baselineEvidence, genuine.Reason, StringComparison.Ordinal);
        }
        else
        {
            var regate = Assert.IsType<ApparatusRedDisposition.Regate>(disposition);
            Assert.Equal("baseline-not-executed", regate.EvidenceKind);
        }
    }

    [Theory]
    [InlineData(SlotsBusy, true)]
    [InlineData(SlotsBusy, false)]
    [InlineData("BuildLockBlockedException", true)]
    [InlineData("BuildLockBlockedException", false)]
    [InlineData("AcceptanceInfrastructureDeferredException", true)]
    [InlineData("AcceptanceInfrastructureDeferredException", false)]
    public void CandidateRerunNotExecuted_RespectsSourceReadingFlag(string signature, bool readsSources)
    {
        var failure = FailingTest(readsSources) with
        {
            CandidateRerunNotExecuted = true,
            CandidateRerunInfrastructureSignature = signature
        };

        var disposition = Classify(failure);

        if (readsSources)
        {
            var genuine = Assert.IsType<ApparatusRedDisposition.Genuine>(disposition);
            AssertGuardReason(genuine);
            Assert.Contains("candidate rerun did not execute", genuine.Reason, StringComparison.Ordinal);
            Assert.Contains(signature, genuine.Reason, StringComparison.Ordinal);
        }
        else
        {
            var regate = Assert.IsType<ApparatusRedDisposition.Regate>(disposition);
            Assert.Equal("candidate-rerun-not-executed", regate.EvidenceKind);
        }
    }

    [Theory]
    [InlineData("cross-goal-flake")]
    [InlineData("candidate-rerun-pass")]
    [InlineData("infrastructure-exception")]
    public void SourceReadingFlag_PreservesEarlierApparatusBranches(string evidenceKind)
    {
        var failure = FailingTest(true) with
        {
            BaselineAttribution = new AcceptanceTestFailureAttribution(
                Identity, AcceptanceTestFailureOrigin.Unattributed, $"focused baseline execution failed: {SlotsBusy}"),
            CandidateRerunNotExecuted = true,
            CandidateRerunInfrastructureSignature = SlotsBusy
        };
        failure = evidenceKind switch
        {
            "cross-goal-flake" => failure with { HasCrossGoalOccurrence = true },
            "candidate-rerun-pass" => failure with { CandidateRerunPassed = true },
            "infrastructure-exception" => failure with { ExceptionSignature = SlotsBusy },
            _ => throw new ArgumentOutOfRangeException(nameof(evidenceKind))
        };

        var regate = Assert.IsType<ApparatusRedDisposition.Regate>(Classify(failure));

        Assert.Equal(evidenceKind, regate.EvidenceKind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GateRecordsUnchangedGuard_AndRoutesBySourceReading(bool readsSources)
    {
        using var fixture = new Fixture(readsSources ? ReadingSource : PlainSource);
        var check = new AcceptanceCheckResult(
            "guard tests", false, 1, "Assert.Empty() Failure",
            FailingTestIdentities: [Identity], TestProjectPath: "tests/Sample.Tests/Sample.Tests.csproj",
            FailingTestAttributions:
            [new AcceptanceTestFailureAttribution(
                Identity, AcceptanceTestFailureOrigin.Unattributed, $"focused baseline execution failed: {SlotsBusy}")]);
        var reading = Assert.IsType<ApparatusRedGateReading>(fixture.Gate.RecordGateCompletion(
            fixture.Goal, new AcceptanceVerificationSummary(false, [check], BranchHeadSha: "candidate-a"),
            new Lazy<IReadOnlyList<string>>(() => [ChangedPath])));

        var failure = Assert.Single(reading.FailingTests);
        Assert.Equal(new[] { TestPath }, failure.ResolvedSourcePaths);
        Assert.False(failure.InsideChangedPaths);
        Assert.Empty(failure.ReferencedChangedTypes!);
        Assert.Null(failure.ExceptionSignature);
        Assert.False(failure.HasCrossGoalOccurrence);
        Assert.False(failure.CandidateRerunPassed);
        Assert.False(failure.CandidateRerunFailed);
        Assert.False(failure.CandidateRerunNotExecuted);
        Assert.Equal(readsSources, failure.ReadsRepositorySources);
        Assert.Equal(1, reading.SourceEnumerationCount);
        Assert.Equal(2, reading.SourceReadCount);

        var disposition = fixture.Gate.Classify(fixture.Goal, reading);

        if (readsSources)
            AssertGuardReason(Assert.IsType<ApparatusRedDisposition.Genuine>(disposition));
        else
            Assert.Equal("baseline-not-executed", Assert.IsType<ApparatusRedDisposition.Regate>(disposition).EvidenceKind);
    }

    [Theory]
    [InlineData("VerifiedRepositoryRoot.Find()")]
    [InlineData("Directory.EnumerateFiles(root)")]
    [InlineData("File.ReadAllText(path)")]
    [InlineData("File.ReadAllLines(path)")]
    public void Detector_MatchesEachIndependentMarker(string expression)
    {
        using var fixture = new Fixture(
            $"namespace Sample.Tests; class GuardTests {{ object Read() => {expression}; }}");

        Assert.True(SourceReadingGuardTestDetector.ReadsRepositorySources(fixture.Root, [TestPath]));
    }

    [Fact]
    public void Detector_ReadingGuardMatches_AndObjectConstructionDoesNot()
    {
        using var reading = new Fixture(ReadingSource);
        using var plain = new Fixture(PlainSource);

        Assert.True(SourceReadingGuardTestDetector.ReadsRepositorySources(reading.Root, [TestPath]));
        Assert.False(SourceReadingGuardTestDetector.ReadsRepositorySources(plain.Root, [TestPath]));
    }

    [Fact]
    public void Detector_EmptyNullAndMissingPathsDoNotMatch()
    {
        using var fixture = new Fixture(ReadingSource);

        Assert.False(SourceReadingGuardTestDetector.ReadsRepositorySources(fixture.Root, []));
        Assert.False(SourceReadingGuardTestDetector.ReadsRepositorySources(fixture.Root, null));
        Assert.False(SourceReadingGuardTestDetector.ReadsRepositorySources(fixture.Root, ["tests/Missing.cs"]));
        Assert.True(SourceReadingGuardTestDetector.ReadsRepositorySources(fixture.Root, ["tests/Missing.cs", TestPath]));
    }

    [Fact]
    public void Detector_BorrowsResolvedSourceCacheWithoutAnotherReadOrWalk()
    {
        using var fixture = new Fixture(ReadingSource);
        var reads = 0;
        var request = new AcceptanceTestSourceResolver.Request(fixture.Root, read: path =>
        {
            reads++;
            return File.ReadAllText(path);
        });
        var paths = request.ResolveSourcePaths("tests/Sample.Tests/Sample.Tests.csproj", Identity);
        Assert.Equal(new[] { TestPath }, paths);
        Assert.Equal(1, reads);
        File.Delete(Path.Combine(fixture.Root, TestPath));

        Assert.True(SourceReadingGuardTestDetector.ReadsRepositorySources(fixture.Root, paths, request));
        Assert.Equal(1, reads);
        Assert.Equal(1, request.ReadCount);
        Assert.Equal(1, request.EnumerationCount);
    }

    private static ApparatusRedFailingTest FailingTest(bool readsSources) => new(
        "guard tests", Identity, null, [TestPath], false, false, ReadsRepositorySources: readsSources);

    private static ApparatusRedDisposition Classify(ApparatusRedFailingTest failure) =>
        ApparatusRedClassifier.Classify(new ApparatusRedEvidence([ChangedPath], [failure], true, 0, 2));

    private static void AssertGuardReason(ApparatusRedDisposition.Genuine genuine)
    {
        Assert.Contains(Identity, genuine.Reason, StringComparison.Ordinal);
        Assert.Contains("source-reading guard test", genuine.Reason, StringComparison.Ordinal);
        Assert.Contains("without a baseline that executed", genuine.Reason, StringComparison.Ordinal);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = ConductorDriverTests.CreateTempDirectory();
        internal Goal Goal { get; } = ConductorDriverTests.SimpleGoal().Goal;
        internal ApparatusRedGate Gate { get; }

        internal Fixture(string source)
        {
            var fullPath = Path.Combine(Root, TestPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, source);
            Directory.CreateDirectory(Path.Combine(Root, "src"));
            File.WriteAllText(Path.Combine(Root, ChangedPath), "namespace Sample; public sealed class Changed { }");
            var index = new AcceptanceFailingTestIndex(Path.Combine(Root, AcceptanceFailingTestIndex.FileName));
            Gate = new ApparatusRedGate(index, _ => Root, () => DateTimeOffset.Parse("2026-11-09T08:00:00Z"));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
