using System.Reflection;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: every scenario owns its repository, storage roots and coordinator.
public sealed class AcceptanceCohortWorkflowTestsProgressRecording : AcceptanceCohortWorkflowBackgroundSupport
{
    [Fact]
    public void ConductLog_FairnessYield_PersistsTypedDecision()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cohort-fairness-decision-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var blockedHead = new GoalId("11111111111111111111111111111111");
            var selected = new GoalId("22222222222222222222222222222222");
            const string holder = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var decision = new ConductorAcceptanceCohortFairnessDecision(
                ConductorAcceptanceCohortFairnessOutcome.YieldedToRunnableYounger,
                blockedHead,
                selected,
                new ConductorAcceptanceHeldConflict(
                    blockedHead,
                    holder,
                    ConductorAcceptanceHeldConflictKind.LandingPath,
                    "path:src/Blocked.cs:src/Blocked.cs"),
                OvertakeCount: 1,
                CandidateConflicts: []);
            var conductLogPath = Path.Combine(root, "fairness-decision.jsonl");
            var writer = new ConductEventLogWriter(conductLogPath);
            var loop = new ConductorBatchLoop();
            var emit = typeof(ConductorBatchLoop).GetMethod(
                "EmitAcceptanceCohortFairnessDecision",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(emit);
            var writerContextField = typeof(ConductorBatchLoop).GetField(
                "CurrentConductEventLogWriter",
                BindingFlags.Static | BindingFlags.NonPublic);
            var writerContext = Assert.IsType<AsyncLocal<ConductEventLogWriter?>>(writerContextField?.GetValue(null));
            var previousWriter = writerContext.Value;
            try
            {
                writerContext.Value = writer;
                emit.Invoke(loop, [decision]);
            }
            finally
            {
                writerContext.Value = previousWriter;
            }

            var records = (File.Exists(conductLogPath) ? File.ReadAllLines(conductLogPath) : [])
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                    line,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .Where(record =>
                    record.EventKind == "acceptance-cohort" &&
                    record.Detail.StartsWith("ACCEPTANCE_COHORT_FAIRNESS ", StringComparison.Ordinal))
                .ToArray();
            var fairness = Assert.Single(records);
            Assert.Contains($"blocked={blockedHead.Value} selected={selected.Value}", fairness.Detail, StringComparison.Ordinal);
            Assert.Contains(
                $"holder={holder} conflict_kind=LandingPath conflict=path:src/Blocked.cs:src/Blocked.cs overtakes=1",
                fairness.Detail,
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void CohortProgressEvents_AreStructuredForBothMembersWithoutUnknownGoal()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cohort-progress-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var first = new GoalId("11111111111111111111111111111111");
            var second = new GoalId("22222222222222222222222222222222");
            var bindings = new[]
            {
                Bind(first, new string('a', 40), "src/First.cs", "production:first"),
                Bind(second, new string('b', 40), "src/Second.cs", "production:second")
            };
            var identity = AcceptanceCohortIdentity.Create(
                bindings,
                new string('c', 40),
                new string('d', 40),
                "manifest-v1");
            var logPath = Path.Combine(root, "conduct-events.jsonl");
            var writer = new ConductEventLogWriter(logPath);
            var now = DateTimeOffset.UtcNow;

            ConductorDriver.AppendCohortGateProgressEvents(
                writer,
                identity,
                bindings,
                new AcceptanceGateProgress(
                    GoalId: null,
                    Phase: "lane",
                    CurrentTarget: "focused-cohort",
                    SlotIndex: 0,
                    ProcessId: Environment.ProcessId,
                    ChildProcessId: 1234,
                    StartedAt: now.AddSeconds(-1),
                    LastObservedAt: now,
                    LastProgressAt: now,
                    Elapsed: TimeSpan.FromSeconds(1),
                    OutputBytes: 42,
                    HeartbeatPath: Path.Combine(root, "heartbeat.json")));

            var records = File.ReadAllLines(logPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                    line,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .ToArray();
            Assert.Equal(2, records.Length);
            Assert.Contains(records, record => record.GoalId == first.Value[..8]);
            Assert.Contains(records, record => record.GoalId == second.Value[..8]);
            Assert.All(records, record =>
            {
                Assert.Equal("gate-progress", record.EventKind);
                Assert.Contains($"cohort={identity.Value[..18]}", record.Detail, StringComparison.Ordinal);
                Assert.Contains($"members={first.Value[..8]},{second.Value[..8]}", record.Detail, StringComparison.Ordinal);
                Assert.DoesNotContain("goal=unknown", record.Detail, StringComparison.Ordinal);
            });
        }
        finally
        {
            DeleteDirectory(root);
        }
    }
}
