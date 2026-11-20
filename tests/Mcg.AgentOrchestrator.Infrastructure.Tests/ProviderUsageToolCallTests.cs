using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: reads shared fixtures without modifying them.
public sealed class ProviderUsageToolCallTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    [Fact]
    public void CodexCountsCompletedTools()
    {
        var fixture = Path.Combine(Fixtures, "CodexJsonl", "tool-call-count.jsonl");

        var result = CodexJsonlUsageParser.Parse(File.ReadAllText(fixture));

        Assert.NotNull(result.Usage);
        Assert.Equal(4L, result.Usage.ToolCallCount);
        Assert.Equal(1L, result.Usage.TurnCount);
    }

    [Fact]
    public void CodexCountsMcpAndWebCompletionsPerEvent()
    {
        var result = CodexJsonlUsageParser.Parse("""
            {"type":"item.completed","item":{"id":"tool","type":"mcp_tool_call","status":"failed"}}
            {"type":"item.completed","item":{"id":"tool","type":"mcp_tool_call","status":"failed"}}
            {"type":"item.completed","item":{"id":"search","type":"web_search"}}
            {"type":"item.completed","item":{"id":"other","type":"unknown_kind"}}
            {"type":"turn.completed","usage":{"input_tokens":1}}
            """);

        Assert.NotNull(result.Usage);
        Assert.Equal(3L, result.Usage.ToolCallCount);
    }

    [Fact]
    public void ClaudeCountsDistinctMessageTools()
    {
        var reader = new ClaudeTranscriptUsageReader([Path.Combine(Fixtures, "ClaudeTranscript")]);

        var result = reader.Read("tool-call-count-session");

        Assert.NotNull(result.Usage);
        Assert.Equal(3L, result.Usage.ToolCallCount);
        Assert.Equal(2L, result.Usage.TurnCount);
    }

    [Fact]
    public void ClaudeCountsDistinctToolsAcrossSplitMessageLines()
    {
        var reader = new ClaudeTranscriptUsageReader([Path.Combine(Fixtures, "ClaudeTranscript")]);

        var result = reader.Read("split-tool-call-count-session");

        Assert.Equal(new ProviderReportedUsage(13, 5, 3, TurnCount: 1, ToolCallCount: 2), result.Usage);
    }

    [Fact]
    public void LegacyReceiptPreservesUsage()
    {
        var input = ProviderUsageValue.Reported(80);
        var cached = ProviderUsageValue.Unknown("legacy-cache-unavailable");
        var output = ProviderUsageValue.Reported(20);
        var turns = ProviderUsageValue.Reported(2);
        var json = JsonSerializer.Serialize(new
        {
            SemanticPackageId = "legacy-fixture",
            Sections = Array.Empty<WorkerContextSectionReceipt>(),
            InputTokens = input,
            CachedInputTokens = cached,
            OutputTokens = output,
            Turns = turns
        });

        var receipt = JsonSerializer.Deserialize<WorkerContextPackageReceipt>(json);

        Assert.NotNull(receipt);
        Assert.Equal("legacy-fixture", receipt.SemanticPackageId);
        Assert.Equal(input, receipt.InputTokens);
        Assert.Equal(cached, receipt.CachedInputTokens);
        Assert.Equal(output, receipt.OutputTokens);
        Assert.Equal(turns, receipt.Turns);
        Assert.Equal(ProviderUsageValue.Unknown("not-recorded"), receipt.ToolCalls);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(4L)]
    public void ReportedToolCountsPersistInBothUsageRecords(long count)
    {
        var usage = new ProviderReportedUsage(80, 20, 50, ToolCallCount: count);
        var receipt = ContextUsageTestFixture.Receipt(80, 50, 20, 0).WithProviderUsage(usage);
        var record = new TaskDispatchRecord("test-worker", "test command", "work", DateTimeOffset.UnixEpoch,
            ContextPackageReceipt: receipt,
            ProviderUsage: DispatchProviderUsage.From(usage, null));

        var stored = JsonSerializer.Deserialize<TaskDispatchRecord>(JsonSerializer.Serialize(record));

        var expected = ProviderUsageValue.Reported(count);
        Assert.NotNull(stored);
        Assert.NotNull(stored.ContextPackageReceipt);
        Assert.Equal(expected, stored.ContextPackageReceipt.ToolCalls);
        Assert.Equal(expected, stored.ContextPackageReceipt.WithProviderUsage(
            (ProviderReportedUsage?)null, "unavailable").ToolCalls);
        Assert.Equal(expected, stored.ProviderUsage?.ToolCalls);
    }
}
