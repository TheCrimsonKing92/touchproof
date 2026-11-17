using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every test instance owns its temporary root and journal; no ambient state changes.
public sealed class AcceptanceRetryReceiptFailureTests : IDisposable
{
    private const string OriginalInvocationId = "attempt:partition:0";
    private const string RetryInvocationId = "attempt:partition:1";
    private const string RetryOperation = "acceptance:partition-within-attempt-retry";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "retry-receipt-" + Guid.NewGuid().ToString("N"));
    private readonly GoalAcceptanceVerifier.AcceptanceManifestCheck _partition = new()
    {
        Name = "infrastructure tests: Process spawning",
        Type = "dotnet-test",
        Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
        Arguments = ["--filter", "FullyQualifiedName~ProcessSpawning"]
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task RecordThenRerun_UntypedDecision_PreservesFailureWithoutRerun(string? predicate)
    {
        var cache = CreateCache();
        var original = OriginalFailure();
        original = original with
        {
            CompletionDecision = predicate is null ? null : original.CompletionDecision! with { FailedPredicate = predicate }
        };
        var diagnostic = RetainedDiagnostic();
        Exception? receiptException = null;
        var receiptCalls = 0;
        var rerunCalls = 0;

        var outcome = await AcceptanceRetryReceiptFailure.RecordThenRerunAsync(
            () =>
            {
                receiptCalls++;
                try
                {
                    cache.RecordWithinAttemptRetry(_partition, original, OriginalInvocationId, RetryInvocationId, diagnostic);
                }
                catch (Exception ex)
                {
                    receiptException = ex;
                    throw;
                }
            },
            () => { rerunCalls++; return Task.FromResult(original); },
            original, diagnostic, OriginalInvocationId);

        var failure = Assert.IsType<InvalidOperationException>(receiptException);
        Assert.Equal(1, receiptCalls);
        Assert.Equal(0, rerunCalls);
        Assert.False(outcome.RerunStarted);
        AssertPreservedFailure(original, outcome.Result, diagnostic, failure);
        Assert.False(File.Exists(cache.JournalPath));
    }

    [Fact]
    public async Task RecordThenRerun_JournalWriteFails_PreservesFailureWithoutRerun()
    {
        var cache = CreateCache();
        var original = OriginalFailure();
        var diagnostic = RetainedDiagnostic();
        var journalParent = Path.GetDirectoryName(cache.JournalPath)!;
        Directory.CreateDirectory(Path.GetDirectoryName(journalParent)!);
        File.WriteAllText(journalParent, "block journal directory creation");
        Assert.True(File.Exists(journalParent));
        Assert.False(Directory.Exists(journalParent));
        Assert.True(cache.ShouldRerunWithinAttempt(_partition, original.CompletionDecision));
        Exception? receiptException = null;
        var receiptCalls = 0;
        var rerunCalls = 0;

        var outcome = await AcceptanceRetryReceiptFailure.RecordThenRerunAsync(
            () =>
            {
                receiptCalls++;
                try
                {
                    cache.RecordWithinAttemptRetry(_partition, original, OriginalInvocationId, RetryInvocationId, diagnostic);
                }
                catch (Exception ex)
                {
                    receiptException = ex;
                    throw;
                }
            },
            () => { rerunCalls++; return Task.FromResult(original); },
            original, diagnostic, OriginalInvocationId);

        var failure = Assert.IsAssignableFrom<IOException>(receiptException);
        Assert.Equal(1, receiptCalls);
        Assert.Equal(0, rerunCalls);
        Assert.False(outcome.RerunStarted);
        AssertPreservedFailure(original, outcome.Result, diagnostic, failure);
        Assert.False(File.Exists(cache.JournalPath));
    }

    [Fact]
    public async Task RecordThenRerun_RecordedRetry_RerunsOnceAndReportsProbeEvidence()
    {
        var cache = CreateCache();
        var original = OriginalFailure();
        var diagnostic = RetainedDiagnostic();
        var probe = new AcceptanceCheckResult(
            _partition.Name, true, 0, "probe completed",
            TestResultPaths: ["probe.trx"], TestResultAttemptId: "attempt", TestResultRunOrdinal: 1,
            ExecutedTestCount: 351, DiscoveredTestCount: 351,
            CompletionDecision: new AcceptanceShardCompletionDecision(true, null, false, 0, 351, 351, "parsed"));
        Assert.True(cache.ShouldRerunWithinAttempt(_partition, original.CompletionDecision));
        var receiptCalls = 0;
        var rerunCalls = 0;

        var outcome = await AcceptanceRetryReceiptFailure.RecordThenRerunAsync(
            () =>
            {
                receiptCalls++;
                cache.RecordWithinAttemptRetry(_partition, original, OriginalInvocationId, RetryInvocationId, diagnostic);
            },
            () =>
            {
                rerunCalls++;
                var receipt = Assert.Single(File.ReadAllLines(cache.JournalPath),
                    line => line.Contains(RetryOperation, StringComparison.Ordinal));
                Assert.Contains(OriginalInvocationId, receipt);
                Assert.Contains(RetryInvocationId, receipt);
                Assert.Contains(diagnostic.Sha256, receipt);
                return Task.FromResult(probe);
            },
            original, diagnostic, OriginalInvocationId);

        Assert.Equal(1, receiptCalls);
        Assert.Equal(1, rerunCalls);
        Assert.True(outcome.RerunStarted);
        Assert.Same(probe, outcome.Result);
        var verdict = cache.SelectPartitionVerdict(_partition, original, outcome.Result);
        Assert.False(verdict.Passed);
        Assert.False(verdict.Advisory);
        var evidence = Assert.IsType<AcceptanceWithinAttemptRerunEvidence>(verdict.WithinAttemptRerun);
        Assert.Equal(AcceptanceShardCompletionPredicates.MissingTrx, evidence.FailedPredicate);
        Assert.Equal(OriginalInvocationId, evidence.OriginalInvocationId);
        Assert.Equal(RetryInvocationId, evidence.RetryInvocationId);
        Assert.Equal(351, evidence.ExecutedTestCount);
    }

    [Fact]
    public async Task RecordThenRerun_UnhandledReceiptException_PropagatesWithoutRerun()
    {
        var original = OriginalFailure();
        var exception = new UnauthorizedAccessException("receipt access denied");
        var rerunCalls = 0;

        var actual = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            AcceptanceRetryReceiptFailure.RecordThenRerunAsync(
                () => throw exception,
                () => { rerunCalls++; return Task.FromResult(original); },
                original, null, OriginalInvocationId));

        Assert.Same(exception, actual);
        Assert.Equal(0, rerunCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecordThenRerun_RerunException_PropagatesAfterReceipt(bool invalidOperation)
    {
        var original = OriginalFailure();
        Exception exception = invalidOperation ? new InvalidOperationException("rerun") : new IOException("rerun");
        var receiptCalls = 0;
        var rerunCalls = 0;

        var actual = await Record.ExceptionAsync(() => AcceptanceRetryReceiptFailure.RecordThenRerunAsync(
            () => receiptCalls++,
            () => { rerunCalls++; return Task.FromException<AcceptanceCheckResult>(exception); },
            original, null, OriginalInvocationId));

        Assert.Same(exception, actual);
        Assert.Equal(1, receiptCalls);
        Assert.Equal(1, rerunCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Build_MissingIdentityAndDiagnostic_ReportsUnavailableWithoutDecision(string? invocationId)
    {
        var original = OriginalFailure() with { OutputTail = null, ResultSummary = null, CompletionDecision = null };

        var result = AcceptanceRetryReceiptFailure.Build(original, null, invocationId, new Exception("receipt unavailable"));

        Assert.False(result.Passed);
        Assert.Null(result.CompletionDecision);
        Assert.Contains("original_invocation_id=unavailable", result.OutputTail);
        Assert.Contains("retained_diagnostic=unavailable", result.OutputTail);
        Assert.Equal(result.OutputTail, result.ResultSummary);
        Assert.Equal(original.ProcessStderrPath, result.ProcessStderrPath);
    }

    private AcceptancePartitionVerdictCache CreateCache()
    {
        Directory.CreateDirectory(_root);
        return Assert.IsType<AcceptancePartitionVerdictCache>(AcceptancePartitionVerdictCache.Create(
            new AcceptancePartitionVerdictCacheOptions(
                new GoalId("12345678123456781234567812345678"), _root, [_partition], 5, true,
                _ => "candidate", _ => "main", _ => "commit", () => "attempt", () => "manifest", () => false)));
    }

    private AcceptanceCheckResult OriginalFailure() => new(
        _partition.Name, false, 137, "original output",
        ArtifactsPath: Path.Combine(_root, "artifacts"), ResultSummary: "original failure",
        TestResultPaths: [Path.Combine(_root, "original.trx")],
        FailureClassification: AcceptanceShardCompletionPredicates.MissingTrx,
        TestResultAttemptId: "attempt", TestResultRunOrdinal: 0,
        ExecutedTestCount: 7, DiscoveredTestCount: 351,
        CompletionDecision: new AcceptanceShardCompletionDecision(
            false, AcceptanceShardCompletionPredicates.MissingTrx, false, 137, 351, 7, "missing", "original-policy", 344),
        ProcessStderrPath: Path.Combine(_root, "original.err"), ProcessStderr: "original stderr");

    private AcceptanceRetainedDiagnostic RetainedDiagnostic() => new(Path.Combine(_root, "retained.err"), "retained-sha");

    private static void AssertPreservedFailure(
        AcceptanceCheckResult original, AcceptanceCheckResult result,
        AcceptanceRetainedDiagnostic diagnostic, Exception exception)
    {
        Assert.False(result.Passed);
        Assert.False(result.Advisory);
        Assert.Equal(AcceptanceFailureClassifications.RetryReceiptFailure, result.FailureClassification);
        Assert.False(AcceptanceFailureClassifications.IsEnvironmentalApparatus(result.FailureClassification));
        Assert.Equal(original.ExitCode, result.ExitCode);
        Assert.Same(original.TestResultPaths, result.TestResultPaths);
        Assert.Equal(original.ExecutedTestCount, result.ExecutedTestCount);
        Assert.Equal(original.DiscoveredTestCount, result.DiscoveredTestCount);
        Assert.Equal(original.TestResultAttemptId, result.TestResultAttemptId);
        Assert.Equal(original.TestResultRunOrdinal, result.TestResultRunOrdinal);
        Assert.Equal(original.ProcessStderr, result.ProcessStderr);
        Assert.Equal(original.ProcessStderrPath, result.ProcessStderrPath);
        Assert.Equal(original.ArtifactsPath, result.ArtifactsPath);
        Assert.Equal(original.CompletionDecision?.FailedPredicate, result.CompletionDecision?.FailedPredicate);
        if (original.CompletionDecision is null)
            Assert.Null(result.CompletionDecision);
        else
        {
            Assert.Equal(original.CompletionDecision,
                result.CompletionDecision! with { PolicySignal = original.CompletionDecision.PolicySignal });
            Assert.Equal(AcceptanceFailureClassifications.RetryReceiptFailure, result.CompletionDecision.PolicySignal);
        }
        Assert.StartsWith(original.OutputTail + Environment.NewLine, result.OutputTail);
        var detail = result.OutputTail![(original.OutputTail!.Length + Environment.NewLine.Length)..];
        Assert.Contains(OriginalInvocationId, detail);
        Assert.Contains(diagnostic.Path, detail);
        Assert.Contains(exception.Message, detail);
        Assert.StartsWith(detail, result.ResultSummary);
        Assert.Contains(original.ResultSummary!, result.ResultSummary);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
