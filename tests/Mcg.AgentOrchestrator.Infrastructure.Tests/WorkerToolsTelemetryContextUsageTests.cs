using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: owns its files and kernel, with injected process liveness and no worker launch.
public sealed class WorkerToolsTelemetryContextUsageTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CompletedDispatch_ReportsWtCountsAlongsideProviderToolCalls(bool withReceipt)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var goal = CompleteDispatch(root, withReceipt);
            var dispatch = Assert.Single(Assert.Single(goal.Tasks).DispatchHistory);
            var recorded = withReceipt ? dispatch.ContextPackageReceipt?.WtCalls : dispatch.ProviderUsage?.WtCalls;
            var toolCalls = withReceipt ? dispatch.ContextPackageReceipt?.ToolCalls : dispatch.ProviderUsage?.ToolCalls;
            Assert.NotNull(recorded);
            Assert.Equal(2, recorded.Count);
            Assert.Equal(2L, recorded["read"]);
            Assert.Equal(1L, recorded["find"]);
            Assert.Equal(ProviderUsageValue.Reported(1), toolCalls);

            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(0, CliContextUsageCommand.Run(["context-usage", "--json"], () => [goal], output, error));
            Assert.Empty(error.ToString());
            using var json = JsonDocument.Parse(output.ToString());
            var row = Assert.Single(json.RootElement.GetProperty("rows").EnumerateArray());
            Assert.Equal("Developer", row.GetProperty("role").GetString());
            Assert.Equal("fixture-model", row.GetProperty("model").GetString());
            Assert.Equal(1, row.GetProperty("dispatchCount").GetInt32());
            var wtCalls = row.GetProperty("wtCalls");
            Assert.Equal(1, wtCalls.GetProperty("reportedDispatchCount").GetInt32());
            var subcommands = wtCalls.GetProperty("bySubcommand");
            Assert.Equal(2L, subcommands.GetProperty("read").GetInt64());
            Assert.Equal(1L, subcommands.GetProperty("find").GetInt64());
            Assert.Equal(3, wtCalls.GetProperty("total").GetProperty("median").GetDouble());
            Assert.Equal(3L, wtCalls.GetProperty("total").GetProperty("p90").GetInt64());
            Assert.Equal(1, row.GetProperty("toolCalls").GetProperty("median").GetDouble());
            Assert.Equal(1L, row.GetProperty("toolCalls").GetProperty("p90").GetInt64());

            output.GetStringBuilder().Clear();
            Assert.Equal(0, CliContextUsageCommand.Run(["context-usage"], () => [goal], output, error));
            Assert.Empty(error.ToString());
            Assert.Contains(" tool-calls=median=1,p90=1 ", output.ToString());
            Assert.Contains(" | wt-calls=median=3,p90=3 reported=1 subcommands=find=1,read=2", output.ToString());
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    private static Goal CompleteDispatch(string root, bool withReceipt)
    {
        var (kernel, goal) = ConductorDriverTests.SimpleGoal("Record worker-tools telemetry");
        var task = Assert.Single(goal.Tasks);
        var startedAt = ContextUsageTestFixture.Start;
        const string command = "codex exec --json";
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "developer", command, root, startedAt, ModelName: "fixture-model",
            WorkerProviderKind: ProviderKind.OpenAICodexCli,
            ContextPackageReceipt: withReceipt ? ContextUsageTestFixture.Receipt(0, 0, 0, 0) : null));

        var stdout = Path.Combine(root, "worker.out.log");
        var stderr = Path.Combine(root, "worker.err.log");
        var exit = Path.Combine(root, "worker.exit.txt");
        File.WriteAllText(stdout, """
            {"type":"item.completed","item":{"id":"command","type":"command_execution","status":"completed"}}
            {"type":"turn.completed","usage":{"input_tokens":80,"cached_input_tokens":50,"output_tokens":20}}
            """);
        File.WriteAllText(stderr, string.Empty);
        File.WriteAllText(exit, "1");
        var telemetry = WorkerToolsTelemetry.PathFor(stdout);
        Directory.CreateDirectory(Path.GetDirectoryName(telemetry)!);
        File.WriteAllText(telemetry, """
            {"subcommand":"read"}
            {"subcommand":"find"}
            {"subcommand":"read"}
            """);
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(
            4242, command, root, stdout, stderr, exit, startedAt, null, null, OwnedProcessIds: [4242]));
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "fixture worker exited");

        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);
        Assert.Equal(1, runner.SweepExitedProcesses(kernel, goal.Id));
        Assert.Equal(1, task.LastVerification?.ExitCode);
        Assert.NotNull(task.LastProcess?.CompletedAt);
        return goal;
    }
}
