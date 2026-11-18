using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each arm owns its kernel, clock, artifacts and AsyncLocal console capture.
public sealed class PlannerRoundMeasurementReproductionTests(ITestOutputHelper output)
    : CliTaskQueryTestSupport
{
    private static readonly GoalId FixtureGoal = new("11111111111111111111111111111111");
    private static readonly TaskId FixtureTask = new("22222222222222222222222222222222");
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddMinutes(30);
    private static readonly string[] WindowArgs =
        ["round-value", "--since", "2026-11-01T00:00:00Z", "--until", "2026-11-02T00:00:00Z"];

    [Fact]
    public void NormalEntry_MatchedFailures_RecordGenericLedgerAndDistinctDiagnostics()
    {
        var rejected = RunFailure(contractRejected: true);
        var unrelated = RunFailure(contractRejected: false);

        // The logical task, model, provider usage and dispatch window match across isolated arms.
        Assert.Equal(rejected.TaskId, unrelated.TaskId);
        Assert.Equal(rejected.Role, unrelated.Role);
        Assert.Equal(rejected.ProviderName, unrelated.ProviderName);
        Assert.Equal(rejected.ModelName, unrelated.ModelName);
        Assert.Equal(rejected.DispatchLane, unrelated.DispatchLane);
        Assert.Equal(rejected.DispatchedAt, unrelated.DispatchedAt);
        Assert.Equal(rejected.EndedAt, unrelated.EndedAt);
        Assert.Equal(rejected.InputTokens, unrelated.InputTokens);
        Assert.Equal(rejected.CachedInputTokens, unrelated.CachedInputTokens);
        Assert.Equal(rejected.OutputTokens, unrelated.OutputTokens);
        Assert.Equal(WorkerRoundStopCause.Failed, rejected.StopCause);
        Assert.Equal(WorkerRoundStopCause.Failed, unrelated.StopCause);
    }

    private WorkerRoundRecord RunFailure(bool contractRejected)
    {
        var root = CreateTempDirectory();
        try
        {
            var clock = new FixtureClock(Start);
            var kernel = new AgentOrchestratorKernel(clock);
            var task = new TaskSpec(FixtureTask, "Inspect source and map the criterion.", AgentRole.Planner);
            var goal = kernel.CreateGoal(FixtureGoal, "Measure Planner failure attribution.", [task]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);

            // Same captured output and usage in both arms; only the process failure input varies.
            // No worker-authored classifier text is supplied. The runner must produce and persist it.
            const string workerOutput = """
                Summary only.
                WORKER_RESULT:
                files: none
                commands: source inspection
                tests: not-run - read-only Planner fixture
                commit: none
                blockers: none
                model_fit: OpenAI/fixture-model - adequate - deterministic Planner measurement
                skills: none
                confidence: high
                END_WORKER_RESULT
                """;
            var stdout = Path.Combine(root, "planner.out.log");
            var stderr = Path.Combine(root, "planner.err.log");
            var exit = Path.Combine(root, "planner.exit.txt");
            File.WriteAllText(stdout,
                JsonSerializer.Serialize(new
                {
                    type = "item.completed",
                    item = new { type = "agent_message", text = workerOutput }
                }) + Environment.NewLine +
                """{"type":"turn.completed","usage":{"input_tokens":100,"cached_input_tokens":10,"output_tokens":20}}""");
            File.WriteAllText(stderr, contractRejected ? string.Empty : "Fixture process returned an unrelated error.");
            File.WriteAllText(exit, contractRejected ? "0" : "17");
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
                "codex-cli", "codex exec --json fixture", root, Start,
                ProviderName: "OpenAI", ModelName: "fixture-model",
                WorkerProviderKind: ProviderKind.OpenAICodexCli, DispatchLane: "codex-cli"));
            kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(
                4245, "codex exec --json fixture", root, stdout, stderr, exit, Start, null, null));
            clock.UtcNow = End;

            // Exercise normal completion, including CompletedProcessOutcomeBuilder and kernel recording.
            // The process is already exited; this fixture never launches a worker or polls a live PID.
            new BackgroundDispatchRunner(clock, isStillRunning: _ => false, disableProcessStart: true,
                findBuildDaemons: _ => [])
                .RefreshLatestProcess(kernel, goal.Id, task.Id);

            Assert.Equal(WorkTaskStatus.Failed, task.Status);
            var verification = Assert.IsType<TaskVerificationRecord>(task.LastVerification);
            Assert.False(verification.Succeeded);
            Assert.Equal(contractRejected ? 1 : 17, verification.ExitCode);
            Assert.Equal(Start, verification.DispatchAttemptAt);
            Assert.Equal(End, verification.CompletedAt);
            Assert.Equal(contractRejected ? "0" : "17", File.ReadAllText(exit).Trim());
            var marker = DispatchFailureDiagnosticMarker.Format(
                DispatchFailureDiagnosticMarker.PlannerOutputContractRejected);
            if (contractRejected)
                Assert.Contains(marker, verification.StandardError, StringComparison.Ordinal);
            else
                Assert.DoesNotContain(marker, verification.StandardError, StringComparison.Ordinal);

            var classifier = Assert.Single(goal.Timeline.Where(e => e.TaskId == task.Id &&
                e.Kind == ProgressKind.TaskNote && e.Message.StartsWith("CLASSIFIER ", StringComparison.Ordinal)));
            Assert.Equal(End, classifier.OccurredAt);
            Assert.Equal(contractRejected ? "planner-output-contract-rejected" : "unknown-failure",
                TaskOutcomeClassifier.TryExtractRule(classifier.Message));

            // Close the goal for the existing terminal-goal report cohort. The round already failed;
            // a goal-level cancellation must not replace its first task-ending event.
            kernel.CancelGoal(goal.Id, "Fixture cohort closed after the failed round.");
            var round = Assert.Single(WorkerRoundLedger.FromGoal(goal));
            Assert.Equal(FixtureTask.Value, round.TaskId);
            Assert.Equal(AgentRole.Planner, round.Role);
            Assert.Equal(Start, round.DispatchedAt);
            Assert.Equal(End, round.EndedAt);
            Assert.Equal(WorkerRoundStopCause.Failed, round.StopCause);
            Assert.True(round.UsageReported);
            Assert.Equal(100L, round.InputTokens);
            Assert.Equal(10L, round.CachedInputTokens);
            Assert.Equal(20L, round.OutputTokens);
            Assert.Equal(contractRejected, round.PlannerContractRejected);
            Assert.False(round.PlannerClarification);

            // Snapshot hydration and the public read-only route consume the actual runner evidence.
            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? selectedGoal = null;
            string Run(string[] args) => CaptureConsole(() =>
            {
                Assert.True(CliReadOnlyCommandRunner.TryExecute(args, repository, workspace,
                    new InMemoryModelProviderRegistry([]), ref agents, ref profiles, ref selectedGoal,
                    out var changed));
                Assert.False(changed);
            });

            var text = Run(WindowArgs);
            Assert.Contains("Window | 0 | 1 | 1 | 0 | 0 | 1 | n/a | 1 | 100 | 10 | 20 | 0",
                text, StringComparison.Ordinal);
            var jsonText = Run([.. WindowArgs, "--json"]);
            using var json = JsonDocument.Parse(jsonText);
            var diagnosticRow = Assert.Single(json.RootElement.GetProperty("plannerRounds").EnumerateArray());
            Assert.Equal(contractRejected, diagnosticRow.GetProperty("contractRejected").GetBoolean());
            Assert.False(diagnosticRow.GetProperty("clarification").GetBoolean());
            var window = json.RootElement.GetProperty("window");
            Assert.Equal(1, window.GetProperty("rounds").GetInt32());
            Assert.Equal(1, window.GetProperty("lostGoals").GetInt32());
            Assert.Equal(100L, window.GetProperty("inputTokens").GetInt64());
            Assert.Equal(10L, window.GetProperty("cachedInputTokens").GetInt64());
            Assert.Equal(20L, window.GetProperty("outputTokens").GetInt64());
            Assert.Equal(0, window.GetProperty("usageUnreported").GetInt32());
            Assert.Equal(0, repository.SaveAttempts);
            Assert.Equal(0, repository.MergeSaveAttempts);
            Assert.Equal(0, repository.MutationAttempts);
            Assert.Equal(0, repository.FullLoadAttempts);
            Assert.Equal(0, repository.ListOutboxMessagesCount);
            Assert.Equal(0, repository.OutboxClaimAttempts);

            // Acceptance retains these outputs with its invocation/revision receipt before production edits.
            output.WriteLine($"arm={(contractRejected ? "contract-rejected" : "unrelated-failure")}");
            output.WriteLine($"observed-exit={verification.ObservedRootExitCode}; recorded-exit={verification.ExitCode}");
            output.WriteLine($"diagnostic={verification.StandardError}");
            output.WriteLine($"classifier={classifier.Message}");
            output.WriteLine($"ledger={JsonSerializer.Serialize(round)}");
            output.WriteLine($"compact-text:{Environment.NewLine}{text}");
            output.WriteLine($"compact-json={jsonText}");
            return round;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FixtureClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}
