using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each case owns an in-memory repository, temporary directory and AsyncLocal console capture.
public sealed class PlannerRoundReportReadPathTests : CliTaskQueryTestSupport
{
    private const string GoalKey = "11111111111111111111111111111111";
    private const string Rejected = "CLASSIFIER rule=planner-output-contract-rejected; outcome_class=unknown-era";
    private const string Unrelated = "CLASSIFIER rule=unknown-failure; outcome_class=unknown-era";
    private static readonly string[] WindowArgs =
        ["round-value", "--since", "2026-11-01T00:00:00Z", "--until", "2026-11-02T00:00:00Z"];
    private static DateTimeOffset At(int hour, int minute = 0) => new(2026, 11, 1, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void SyntheticTimeline_TextAndJsonMatchIndependentDispatchOracle()
    {
        var kernel = Create(Planner(1, 2, 3),
        [
            Event(1, 30, Rejected), Event(1, 31, Rejected),
            Event(1, 40, "failed", ProgressKind.TaskFailed),
            Event(2, 0, "question", ProgressKind.HumanInputRequested),
            Event(2, 0, "question", ProgressKind.HumanInputRequested), Event(2, 30, Unrelated),
            Event(2, 40, "failed", ProgressKind.TaskFailed),
            Event(2, 10, "Developer question", ProgressKind.HumanInputRequested, "developer"),
            Event(3, 0, Rejected), Event(3, 30, "failed", ProgressKind.TaskFailed)
        ]);
        Read(kernel, (text, json, baseline) =>
        {
            string[] oracle =
            [
                "Planner rounds | rejections=2 | clarifications=1",
                "Planner dispatch | Goal | Task | Round | Contract rejected | Clarification",
                $"2026-11-01T01:00:00.0000000+00:00 | {GoalKey} | planner | 1 | yes | no",
                $"2026-11-01T02:00:00.0000000+00:00 | {GoalKey} | planner | 2 | no | yes",
                $"2026-11-01T03:00:00.0000000+00:00 | {GoalKey} | planner | 3 | yes | no"
            ];
            Assert.EndsWith(string.Join(Environment.NewLine, oracle) + Environment.NewLine, text, StringComparison.Ordinal);
            Assert.Contains("Window | 0 | 1 | 3 | 0 | 0 | 3 | n/a | 1 | 300 | 30 | 60 | 0", text, StringComparison.Ordinal);
            var rows = json.GetProperty("plannerRounds").EnumerateArray().ToArray();
            Assert.Equal(3, rows.Length);
            Assert.Equal(new[] { true, false, true }, rows.Select(r => r.GetProperty("contractRejected").GetBoolean()));
            Assert.Equal(new[] { false, true, false }, rows.Select(r => r.GetProperty("clarification").GetBoolean()));
            Assert.Equal(new[] { At(1), At(2), At(3) }, rows.Select(r => r.GetProperty("dispatchedAt").GetDateTimeOffset()));
            Assert.Equal(new[] { 1, 2, 3 }, rows.Select(r => r.GetProperty("roundIndex").GetInt32()));
            Assert.All(rows, r =>
            {
                Assert.Equal(GoalKey, r.GetProperty("goalId").GetString());
                Assert.Equal("planner", r.GetProperty("taskId").GetString());
            });
            var totals = json.GetProperty("window");
            Assert.Equal(300, totals.GetProperty("inputTokens").GetInt64());
            Assert.Equal(30, totals.GetProperty("cachedInputTokens").GetInt64());
            Assert.Equal(60, totals.GetProperty("outputTokens").GetInt64());
            Assert.Equal(0, totals.GetProperty("usageUnreported").GetInt32());
            Assert.Equal(json.GetProperty("plannerRounds").GetRawText(), baseline.GetProperty("plannerRounds").GetRawText());
        });
    }

    [Theory]
    [InlineData("unrelated", false, false)]
    [InlineData("both", true, true)]
    [InlineData("missing", null, null)]
    [InlineData("cancelled", null, null)]
    [InlineData("other-role", null, null)]
    [InlineData("worker-narration", null, null)]
    public void NonproofControls_DoNotManufacturePlannerRejection(string control, bool? rejection, bool? clarification)
    {
        var task = Planner(1);
        ProgressEventSnapshot[] events = control switch
        {
            "unrelated" => [Event(1, 30, Unrelated), Event(1, 30, "failed", ProgressKind.TaskFailed)],
            "both" => [Event(1, 30, Rejected), Event(1, 31, "question", ProgressKind.HumanInputRequested)],
            "cancelled" => [Event(1, 30, "cancelled", ProgressKind.TaskCancelled)],
            "other-role" => [Event(1, 30, "Developer question", ProgressKind.HumanInputRequested, "developer")],
            "worker-narration" =>
            [
                Event(1, 30, "worker wrote: " + Rejected), Event(1, 30, Rejected, ProgressKind.TaskVerificationRecorded),
                Event(1, 30, Rejected, ProgressKind.OperatorTaskNote)
            ],
            _ => [Event(1, 30, "failed", ProgressKind.TaskFailed)]
        };
        if (control == "worker-narration") task = task with
        {
            LastVerification = new("fixture", "root", 1, Rejected, Rejected, At(1, 30))
        };
        Read(Create(task, events), (text, json, baseline) =>
        {
            if (rejection.HasValue || clarification.HasValue)
            {
                var row = Assert.Single(json.GetProperty("plannerRounds").EnumerateArray());
                Assert.Equal(rejection, row.GetProperty("contractRejected").GetBoolean());
                Assert.Equal(clarification, row.GetProperty("clarification").GetBoolean());
            }
            else
            {
                Assert.False(json.TryGetProperty("plannerRounds", out _));
                Assert.False(baseline.TryGetProperty("plannerRounds", out _));
                Assert.DoesNotContain("Planner rounds |", text, StringComparison.Ordinal);
            }
            Assert.Equal(100, json.GetProperty("window").GetProperty("inputTokens").GetInt64());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableLinkage_RemainsVisibleAlongsideObservedEvidence(bool duplicateStart)
    {
        var task = Planner(1, 2, 3);
        if (duplicateStart) task = task with
        {
            DispatchHistory = [.. task.DispatchHistory!, Dispatch(1) with { ModelName = "conflicting" }]
        };
        else task = task with
        {
            VerificationHistory = [new("fixture", "root", 1, "", "", At(2), DispatchAttemptAt: At(1))]
        };
        var events = duplicateStart
            ? new[] { Event(1, 30, Rejected), Event(2, 30, Unrelated), Event(3, 0, Rejected) }
            : new[] { Event(1, 30, Unrelated), Event(2, 0, Rejected), Event(3, 0, Rejected) };
        Read(Create(task, events), (text, json, _) =>
        {
            var rows = json.GetProperty("plannerRounds").EnumerateArray().ToArray();
            Assert.Equal(3, rows.Length);
            Assert.Equal(JsonValueKind.Null, rows[0].GetProperty("contractRejected").ValueKind);
            if (!duplicateStart) Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("contractRejected").ValueKind);
            Assert.True(rows[2].GetProperty("contractRejected").GetBoolean());
            Assert.Contains("Planner rounds | rejections=1 | clarifications=0", text, StringComparison.Ordinal);
            Assert.Contains("unavailable", text, StringComparison.Ordinal);
        });
    }

    private static TaskDispatchSnapshot Dispatch(int hour) => new("worker", "fixture", "root", At(hour),
        ProviderName: "OpenAI", ModelName: "fixture-model", ContextPackageReceipt:
            new WorkerContextPackageReceipt("package", [], ProviderUsageValue.Reported(100),
                ProviderUsageValue.Reported(10), ProviderUsageValue.Reported(20)));
    private static TaskSnapshot Planner(params int[] hours) => new("planner", "fixture", AgentRole.Planner,
        WorkTaskStatus.Failed, null, null, null, [], null, null, DispatchHistory: hours.Select(Dispatch).ToArray());
    private static ProgressEventSnapshot Event(int hour, int minute, string message,
        ProgressKind kind = ProgressKind.TaskNote, string task = "planner") => new(GoalKey, task, kind, message, At(hour, minute));
    private static AgentOrchestratorKernel Create(TaskSnapshot planner, ProgressEventSnapshot[] events) =>
        AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
        [
            new GoalSnapshot(GoalKey, "Planner measurement fixture", GoalStatus.Cancelled,
            [planner, new("developer", "other role", AgentRole.Developer, WorkTaskStatus.Pending,
                null, null, null, [], null, null)], events)
        ], []));

    private static void Read(AgentOrchestratorKernel kernel, Action<string, JsonElement, JsonElement> verify)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? selected = null;
            string Run(string[] args) => CaptureConsole(() =>
            {
                Assert.True(CliReadOnlyCommandRunner.TryExecute(args, repository, workspace,
                    new InMemoryModelProviderRegistry([]), ref agents, ref profiles, ref selected, out var changed));
                Assert.False(changed);
            });
            var text = Run(WindowArgs);
            using var json = JsonDocument.Parse(Run([.. WindowArgs, "--json"]));
            using var baseline = JsonDocument.Parse(Run([.. WindowArgs, "--json",
                "--baseline-since", "2026-10-31T00:00:00Z", "--baseline-until", "2026-11-01T00:00:00Z"]));
            verify(text, json.RootElement, baseline.RootElement);
            Assert.Equal(3, repository.LoadGoalsCount);
            Assert.Equal(0, repository.SaveAttempts);
            Assert.Equal(0, repository.MergeSaveAttempts);
            Assert.Equal(0, repository.MutationAttempts);
            Assert.Equal(0, repository.FullLoadAttempts);
            Assert.Equal(0, repository.ListOutboxMessagesCount);
            Assert.Equal(0, repository.OutboxClaimAttempts);
            Assert.Null(selected);
            Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
