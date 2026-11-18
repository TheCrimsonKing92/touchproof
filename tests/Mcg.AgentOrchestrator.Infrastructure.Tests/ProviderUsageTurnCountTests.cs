using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: copied fixtures are read-only and temporary paths are owned by each test.
public sealed class ProviderUsageTurnCountTests
{
    private const string ClaudeSession = "turn-count-fixture-session";
    private static readonly string ClaudeRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ClaudeTranscript");
    private static readonly string CodexFixture = Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "CodexJsonl", "codex-three-turns.jsonl");
    private static readonly ProviderUsageValue Unreported = ProviderUsageValue.Unknown("not-yet-reported");

    [Fact]
    public void ClaudeFixture_RecordsDistinctAssistantTurnsAndTokenTotals()
    {
        var result = new ClaudeTranscriptUsageReader([ClaudeRoot]).Read(ClaudeSession);
        Assert.Equal(new ProviderReportedUsage(6360, 23, 6000, TurnCount: 3), result.Usage);
        AssertRecorded(result.Usage, result.UnavailableReason);
    }

    [Fact]
    public void CodexFixture_RecordsAllCompletedTurnsWhenUsageExists()
    {
        var result = CodexJsonlUsageParser.Parse(File.ReadAllText(CodexFixture));
        Assert.Equal(new ProviderReportedUsage(80, 20, 50, TurnCount: 3), result.Usage);
        AssertRecorded(result.Usage, result.UsageUnavailableReason);
    }

    [Fact]
    public void ClaudeFixture_UnreadableTranscriptKeepsUnknownReason()
    {
        string? readPath = null;
        var reader = new ClaudeTranscriptUsageReader([ClaudeRoot], path =>
        {
            readPath = path;
            throw new UnauthorizedAccessException("fixture");
        });

        var result = reader.Read(ClaudeSession);

        Assert.Equal(Path.Combine(ClaudeRoot, ClaudeSession + ".jsonl"), readPath);
        Assert.StartsWith("claude-transcript-unreadable:", result.UnavailableReason);
        AssertUnavailable(result.Usage, result.UnavailableReason);
    }

    [Fact]
    public void CodexFixture_UnreadableLogKeepsUnknownReason()
    {
        string? readPath = null;
        var dispatch = CodexDispatch(Path.GetDirectoryName(CodexFixture)!);
        var result = BackgroundDispatchRunner.NormalizeStructuredCodexOutput(dispatch, CodexFixture, path =>
        {
            readPath = path;
            throw new IOException("fixture");
        });

        Assert.Equal(CodexFixture, readPath);
        Assert.NotNull(result);
        Assert.Equal("unreadable", result.UsageUnavailableReason);
        AssertUnavailable(result.Usage, result.UsageUnavailableReason);
    }

    [Fact]
    public void CodexMissingLogKeepsUnknownReason() => ClaudeTranscriptUsageReaderTests.WithRoot(root =>
    {
        var result = new BackgroundDispatchRunner().ResolveDispatchProviderUsage(
            CodexDispatch(root), Path.Combine(root, "missing.out.log"));

        Assert.Equal("unreadable", result.UnavailableReason);
        AssertUnavailable(result.Usage, result.UnavailableReason);
    });

    [Theory]
    [InlineData("""{"type":"turn.completed"}""", "absent")]
    [InlineData("""{"type":"turn.completed","usage":{}}""", "absent")]
    [InlineData("""{"type":"turn.completed","usage":{"input_tokens":null}}""", "malformed")]
    public void CodexCompletedTurnWithoutUsableUsageKeepsUnknownReason(string jsonl, string reason)
    {
        var result = CodexJsonlUsageParser.Parse(jsonl);

        Assert.True(result.Recognized);
        Assert.Equal(reason, result.UsageUnavailableReason);
        AssertUnavailable(result.Usage, result.UsageUnavailableReason);
    }

    [Fact]
    public void ReportedTokensWithoutTurnCountKeepTurnsUnknown()
    {
        var usage = new ProviderReportedUsage(80, 20, 50);
        var dispatch = DispatchProviderUsage.From(usage, "turn-count-omitted");
        var receipt = EmptyReceipt().WithProviderUsage(usage, "turn-count-omitted");

        Assert.Equal(ProviderUsageValue.Reported(80), dispatch.InputTokens);
        Assert.Equal(ProviderUsageValue.Reported(80), receipt.InputTokens);
        Assert.Equal(ProviderUsageValue.Unknown("turn-count-omitted"), dispatch.Turns);
        Assert.Equal(ProviderUsageValue.Unknown("turn-count-omitted"), receipt.Turns);
    }

    [Fact]
    public void LegacyDispatchJsonLoadsWithUnknownTurns()
    {
        var usage = DispatchProviderUsage.From(new ProviderReportedUsage(80, 20, 50), null);
        var json = JsonSerializer.Serialize(new
        {
            WorkerName = "developer",
            Command = "worker",
            WorkingDirectory = "work",
            DispatchedAt = DateTimeOffset.UnixEpoch,
            ProviderUsage = new { usage.InputTokens, usage.CachedInputTokens, usage.OutputTokens },
            ContextPackageReceipt = EmptyReceipt()
        });

        Assert.DoesNotContain("\"Turns\"", json);
        var dispatch = JsonSerializer.Deserialize<TaskDispatchRecord>(json)!;
        Assert.NotNull(dispatch.ProviderUsage);
        Assert.Equal(ProviderUsageValue.Unknown("not-recorded"), dispatch.ProviderUsage.Turns);
        Assert.Equal(usage.InputTokens, dispatch.ProviderUsage.InputTokens);
        Assert.NotNull(dispatch.ContextPackageReceipt);
        Assert.Null(dispatch.ContextPackageReceipt.Turns);
        Assert.Equal(Unreported, dispatch.ContextPackageReceipt.InputTokens);
    }

    private static void AssertRecorded(ProviderReportedUsage? usage, string reason)
    {
        var receipt = EmptyReceipt().WithProviderUsage(usage, reason);
        var receiptless = DispatchProviderUsage.From(usage, reason);
        var record = CodexDispatch("work");
        var storedReceipt = RoundTrip(record with { ContextPackageReceipt = receipt }).ContextPackageReceipt!;
        var storedReceiptless = RoundTrip(record with { ProviderUsage = receiptless }).ProviderUsage!;

        var expected = ProviderUsageValue.Reported(3);
        Assert.Equal(expected, storedReceipt.Turns);
        Assert.Equal(expected, storedReceiptless.Turns);
        Assert.Equal(expected, storedReceipt.WithProviderUsage((ProviderReportedUsage?)null, "missing").Turns);
    }

    private static void AssertUnavailable(ProviderReportedUsage? usage, string reason)
    {
        Assert.Null(usage);
        var receipt = EmptyReceipt().WithProviderUsage(usage, reason);
        var dispatch = DispatchProviderUsage.From(usage, reason);
        var expected = ProviderUsageValue.Unknown(reason);
        Assert.Equal(expected, receipt.Turns);
        Assert.Equal(expected, dispatch.Turns);
        Assert.Equal(expected, receipt.InputTokens);
        Assert.Equal(expected, dispatch.InputTokens);
    }

    private static TaskDispatchRecord RoundTrip(TaskDispatchRecord dispatch) =>
        JsonSerializer.Deserialize<TaskDispatchRecord>(JsonSerializer.Serialize(dispatch))!;

    private static TaskDispatchRecord CodexDispatch(string root) =>
        new("developer", "codex exec --json", root, DateTimeOffset.UnixEpoch,
            WorkerProviderKind: ProviderKind.OpenAICodexCli);

    private static WorkerContextPackageReceipt EmptyReceipt() => new("fixture", [], Unreported, Unreported, Unreported);
}
