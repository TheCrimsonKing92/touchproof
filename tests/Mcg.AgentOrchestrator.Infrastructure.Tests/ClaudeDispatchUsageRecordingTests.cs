using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its files and kernel, injects transcript roots and process liveness,
// and launches no processes or changes to the environment.
public sealed class ClaudeDispatchUsageRecordingTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 18, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Sweep_TranscriptWithTwoMessages_RecordsTotalsWithOrWithoutReceipt(bool withReceipt) =>
        ClaudeTranscriptUsageReaderTests.WithRoot(root =>
        {
            var goal = CompleteDispatch(root, "reported", withReceipt);
            var dispatch = goal.Tasks.First().LastDispatch!;
            Assert.Equal(withReceipt, dispatch.ContextPackageReceipt is not null);
            var usage = withReceipt
                ? new DispatchProviderUsage(dispatch.ContextPackageReceipt!.InputTokens,
                    dispatch.ContextPackageReceipt.CachedInputTokens, dispatch.ContextPackageReceipt.OutputTokens)
                : Assert.IsType<DispatchProviderUsage>(dispatch.ProviderUsage);

            Assert.Equal(ProviderUsageValue.Reported(3330), usage.InputTokens);
            Assert.Equal(ProviderUsageValue.Reported(3000), usage.CachedInputTokens);
            Assert.Equal(ProviderUsageValue.Reported(12), usage.OutputTokens);
            var round = Assert.Single(WorkerRoundLedger.FromGoals([goal]));
            Assert.True(round.UsageReported);
            Assert.Equal(3330, round.InputTokens);
            Assert.Equal(3000, round.CachedInputTokens);
            Assert.Equal(12, round.OutputTokens);
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Sweep_MissingTranscript_KeepsUnknownAndFullReason(bool withReceipt) =>
        ClaudeTranscriptUsageReaderTests.WithRoot(root =>
        {
            var goal = CompleteDispatch(root, "missing", withReceipt, writeTranscript: false);
            var dispatch = goal.Tasks.First().LastDispatch!;
            var usage = ContextUsageFigures.RecordedUsage(dispatch);
            Assert.NotNull(usage);
            var expected = new ClaudeTranscriptUsageReader([root]).Read("missing");
            Assert.Null(expected.Usage);
            Assert.StartsWith("claude-transcript-missing:", expected.UnavailableReason);
            Assert.All(new[] { usage.InputTokens, usage.CachedInputTokens, usage.OutputTokens }, value =>
            {
                Assert.Equal(ProviderUsageState.Unknown, value.State);
                Assert.Null(value.Value);
                Assert.Equal(expected.UnavailableReason, value.UnknownReason);
            });
            var round = Assert.Single(WorkerRoundLedger.FromGoals([goal]));
            Assert.False(round.UsageReported);
            Assert.Null(round.InputTokens);
            Assert.Null(round.CachedInputTokens);
            Assert.Null(round.OutputTokens);

            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(0, CliContextUsageCommand.Run(["context-usage", "--json"],
                () => [goal], output, error));
            Assert.Equal(string.Empty, error.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            var row = Assert.Single(document.RootElement.GetProperty("rows").EnumerateArray());
            Assert.Equal(0, row.GetProperty("reportedInputCount").GetInt32());
            Assert.Equal(1, row.GetProperty("missingUsageCount").GetInt32());
            foreach (var field in new[] { "input", "cached", "uncached", "output" })
            {
                Assert.Equal(JsonValueKind.Null, row.GetProperty(field).GetProperty("median").ValueKind);
                Assert.Equal(JsonValueKind.Null, row.GetProperty(field).GetProperty("p90").ValueKind);
            }
        });

    [Fact]
    public void ContextUsage_ReceiptlessCompletions_ReportsUsageAndMissingReason() =>
        ClaudeTranscriptUsageReaderTests.WithRoot(root =>
        {
            var reported = CompleteDispatch(root, "reported", withReceipt: false);
            var missing = CompleteDispatch(root, "missing", withReceipt: false, writeTranscript: false);
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(0, CliContextUsageCommand.Run(["context-usage", "--json"],
                () => [reported, missing], output, error));
            Assert.Equal(string.Empty, error.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            var row = Assert.Single(document.RootElement.GetProperty("rows").EnumerateArray());
            Assert.Equal(2, row.GetProperty("dispatchCount").GetInt32());
            Assert.Equal(1, row.GetProperty("reportedInputCount").GetInt32());
            Assert.Equal(1, row.GetProperty("missingUsageCount").GetInt32());
            var reason = Assert.Single(row.GetProperty("missingUsageReasons").EnumerateObject());
            Assert.Equal("claude-transcript-missing", reason.Name);
            Assert.Equal(1, reason.Value.GetInt32());
            Assert.Equal(3330, row.GetProperty("input").GetProperty("median").GetDouble());
            Assert.Equal(3000, row.GetProperty("cached").GetProperty("median").GetDouble());
            Assert.Equal(330, row.GetProperty("uncached").GetProperty("median").GetDouble());
            Assert.Equal(12, row.GetProperty("output").GetProperty("median").GetDouble());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("harnessOverhead").GetProperty("median").ValueKind);

            output.GetStringBuilder().Clear();
            Assert.Equal(0, CliContextUsageCommand.Run(["context-usage"],
                () => [reported, missing], output, error));
            Assert.Equal(string.Empty, error.ToString());
            Assert.Contains("Developer | claude-opus-5-5 | dispatches=2 reported-input=1", output.ToString());
            Assert.Contains("missing-usage=1 reasons=claude-transcript-missing=1", output.ToString());
        });

    [Fact]
    public void ContextUsage_DifferentMissingSessions_GroupsByStableReasonCode() =>
        ClaudeTranscriptUsageReaderTests.WithRoot(root =>
        {
            var first = CompleteDispatch(root, "first-missing", false, writeTranscript: false);
            var second = CompleteDispatch(root, "second-missing", false, writeTranscript: false);
            var row = Assert.Single(ContextUsageReport.Build([first, second]).Rows);
            Assert.Equal(2, row.MissingUsageCount);
            var reason = Assert.Single(row.MissingUsageReasons);
            Assert.Equal("claude-transcript-missing", reason.Key);
            Assert.Equal(2, reason.Value);
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ApplyRefresh_MissingTelemetryTarget_DoesNotThrow(bool receiptless) =>
        ClaudeTranscriptUsageReaderTests.WithRoot(root =>
        {
            var (kernel, goal) = ConductorDriverTests.SimpleGoal("Unavailable telemetry target");
            var task = goal.Tasks.First();
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
                "developer", "claude -p", root, Start, WorkerProviderKind: ProviderKind.AnthropicClaudeCli));
            var completedAt = Start.AddMinutes(1);
            var process = new TaskProcessRecord(4242, "claude -p", root,
                "out.log", "err.log", "exit.txt", Start, completedAt, 1);
            var verification = new TaskVerificationRecord("claude -p", root, 1,
                "failed", string.Empty, completedAt);
            // One case has no matching receiptless attempt; the other has a matching attempt
            // whose receipt is missing. Neither should turn optional telemetry into an exception.
            var outcome = new DispatchRefreshOutcome(process, verification,
                ProviderUsage: new ProviderReportedUsage(3330, 12, 3000),
                DispatchAttemptAt: receiptless ? null : Start,
                ReceiptlessUsageAttemptAt: receiptless ? Start.AddTicks(-1) : null);

            BackgroundDispatchRunner.ApplyRefreshOutcome(kernel, goal.Id, task.Id, outcome);

            Assert.NotNull(task.LastVerification);
            Assert.Equal(1, task.LastVerification.ExitCode);
            Assert.Equal(completedAt, task.LastProcess!.CompletedAt);
            Assert.Null(task.LastDispatch!.ProviderUsage);
            Assert.Null(task.LastDispatch.ContextPackageReceipt);
        });

    private static Goal CompleteDispatch(string root, string sessionId, bool withReceipt, bool writeTranscript = true)
    {
        if (writeTranscript)
            ClaudeTranscriptUsageReaderTests.Write(root, sessionId, """
                {"type":"assistant","message":{"id":"a","usage":{"input_tokens":10,"cache_creation_input_tokens":100,"cache_read_input_tokens":1000,"output_tokens":5}}}
                {"type":"assistant","message":{"id":"b","usage":{"input_tokens":20,"cache_creation_input_tokens":200,"cache_read_input_tokens":2000,"output_tokens":7}}}
                """);
        var (kernel, goal) = ConductorDriverTests.SimpleGoal("Claude usage fixture");
        var task = goal.Tasks.First();
        var receipt = withReceipt ? new WorkerContextPackageReceipt("fixture", [],
            ProviderUsageValue.Unknown("not-yet-reported"), ProviderUsageValue.Unknown("not-yet-reported"),
            ProviderUsageValue.Unknown("not-yet-reported")) : null;
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "developer", "claude -p", root, Start, ProviderName: "Anthropic", ModelName: "claude-opus-5-5",
            WorkerProviderKind: ProviderKind.AnthropicClaudeCli, ProviderSessionId: sessionId,
            ContextPackageReceipt: receipt));
        Assert.Equal(withReceipt, task.LastDispatch!.ContextPackageReceipt is not null);
        Assert.Null(task.LastDispatch.ProviderUsage);
        var stdout = Path.Combine(root, sessionId + ".out.log");
        var stderr = Path.Combine(root, sessionId + ".err.log");
        var exit = Path.Combine(root, sessionId + ".exit.txt");
        File.WriteAllText(stdout, "no WORKER_RESULT block was emitted");
        File.WriteAllText(stderr, string.Empty);
        File.WriteAllText(exit, "1");
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(
            4242, "claude -p", root, stdout, stderr, exit, Start, null, null, OwnedProcessIds: [4242]));
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "round exited");
        var livenessChecks = 0;
        var runner = new BackgroundDispatchRunner(isStillRunning: _ => { livenessChecks++; return false; })
        {
            ClaudeTranscriptUsage = new ClaudeTranscriptUsageReader([root])
        };

        Assert.Equal(1, runner.SweepExitedProcesses(kernel));
        Assert.True(livenessChecks > 0);
        Assert.NotNull(task.LastVerification);
        Assert.NotNull(task.LastProcess!.CompletedAt);
        return goal;
    }
}
