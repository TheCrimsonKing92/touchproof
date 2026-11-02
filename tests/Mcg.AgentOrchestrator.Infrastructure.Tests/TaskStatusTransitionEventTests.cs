using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

// Parallel-safe: each fixture owns its kernel and a unique event directory; no processes run.
public sealed class TaskStatusTransitionEventTests
{
    [Fact]
    public void AssignedThroughRunningToCompletedRecordsDistinctSites()
    {
        using var fixture = new Fixture();
        AssertTransition(Assert.Single(Transitions(fixture.ReadEvents())), fixture,
            "Pending", "Assigned", "GoalLifecycle.ActivateGoal");
        var start = fixture.ReadEvents().Length;

        fixture.Dispatch();
        fixture.Kernel.RecordDispatchExecutionResult(fixture.Goal.Id, fixture.Task.Id,
            new TaskVerificationRecord("codex exec", fixture.Root, 0,
                "WORKER_RESULT: files: none commands: none tests: pass commit: none blockers: none model_fit: test skills: none confidence: high END_WORKER_RESULT",
                "", DateTimeOffset.UtcNow, WorkerResultPresent: true));

        var transitions = Transitions(fixture.ReadEvents().Skip(start));
        Assert.Collection(transitions,
            entry => AssertTransition(entry, fixture, "Assigned", "Running", "Recording.RecordTaskDispatch"),
            entry => AssertTransition(entry, fixture, "Running", "Completed",
                "GoalLifecycle.ReportTaskProgress"));
        Assert.Equal(3, Transitions(fixture.ReadEvents()).Select(entry => entry.GetProperty("site").GetString())
            .Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(WorkTaskStatus.Completed, fixture.Task.Status);
    }

    [Fact]
    public void UnchangedStatusWritesNoTransition()
    {
        using var fixture = new Fixture();
        fixture.Dispatch();
        var start = fixture.ReadEvents().Length;

        fixture.Kernel.RecordTaskProcessStarted(fixture.Goal.Id, fixture.Task.Id, fixture.Process());
        fixture.Kernel.ConcludeInterruptedDispatchRecovery(
            fixture.Goal.Id, fixture.Task.Id, GoalStatus.Active, WorkTaskStatus.Running);

        var events = fixture.ReadEvents().Skip(start).ToArray();
        Assert.Contains(events, entry => entry.GetProperty("eventType").GetString() == "TaskProcessStarted");
        Assert.Empty(Transitions(events));
        Assert.Equal(WorkTaskStatus.Running, fixture.Task.Status);
    }

    [Theory]
    [InlineData(true, "Recording.RecordTaskProcessCancelled.Conductor")]
    [InlineData(false, "Recording.RecordTaskProcessCancelled")]
    public void CancellationRecordsItsOwnSite(bool byConductor, string site)
    {
        using var fixture = new Fixture();
        fixture.Dispatch();
        var process = fixture.Process();
        fixture.Kernel.RecordTaskProcessStarted(fixture.Goal.Id, fixture.Task.Id, process);
        var start = fixture.ReadEvents().Length;

        fixture.Kernel.RecordTaskProcessCancelled(fixture.Goal.Id, fixture.Task.Id,
            process with { CompletedAt = process.StartedAt.AddSeconds(1), ExitCode = -1,
                WasCancelled = true, WasCancelledByConductor = byConductor });

        AssertTransition(Assert.Single(Transitions(fixture.ReadEvents().Skip(start))), fixture,
            "Running", "Cancelled", site);
        Assert.Equal(WorkTaskStatus.Cancelled, fixture.Task.Status);
        Assert.Equal(byConductor, fixture.Task.WasCancelledByConductor);
    }

    [Fact]
    public void FailedTaskRetryRecordsFailedToAssigned()
    {
        using var fixture = new Fixture();
        fixture.Kernel.EscalateTaskFailure(fixture.Goal.Id, fixture.Task.Id, "Terminal escalation.");
        Assert.Equal(WorkTaskStatus.Failed, fixture.Task.Status);
        var start = fixture.ReadEvents().Length;

        fixture.Kernel.RetryTask(fixture.Goal.Id, fixture.Task.Id, "Retry with clarified contract.",
            RetryCause.ContractClarification);

        AssertTransition(Assert.Single(Transitions(fixture.ReadEvents().Skip(start))), fixture,
            "Failed", "Assigned", "GoalLifecycle.ResetTaskForRetry");
        Assert.Equal(WorkTaskStatus.Assigned, fixture.Task.Status);
    }

    [Fact]
    public void SnapshotRestorationWritesNothingAndLaterLiveChangesRecord()
    {
        using var fixture = new Fixture();
        var snapshot = fixture.Kernel.ExportGoalSnapshot(fixture.Goal.Id);
        fixture.Dispatch();
        var start = fixture.ReadEvents().Length;

        fixture.Kernel.ReplaceGoalStateWithSnapshot(snapshot, []);

        Assert.Empty(fixture.ReadEvents().Skip(start));
        Assert.Equal(WorkTaskStatus.Assigned,
            fixture.Kernel.ExportGoalSnapshot(fixture.Goal.Id).Tasks[0].Status);
        fixture.Dispatch();
        AssertTransition(Assert.Single(Transitions(fixture.ReadEvents().Skip(start))), fixture,
            "Assigned", "Running", "Recording.RecordTaskDispatch");
    }

    [Fact]
    public void CriticalDispatchGateForwardsTaskTransitions()
    {
        using var fixture = new Fixture();
        fixture.Kernel.SetEventWriter(new CriticalDispatchLifecycleEventGate(fixture.Writer));
        var start = fixture.ReadEvents().Length;

        fixture.Dispatch();

        AssertTransition(Assert.Single(Transitions(fixture.ReadEvents().Skip(start))), fixture,
            "Assigned", "Running", "Recording.RecordTaskDispatch");
    }

    [Fact]
    public void DeferredWriterReplaysOnceAndThenForwardsTaskTransitions()
    {
        using var fixture = new Fixture();
        var deferred = new DeferredGoalLifecycleEventWriter();
        fixture.Kernel.SetEventWriter(deferred);
        var start = fixture.ReadEvents().Length;

        fixture.Kernel.ConcludeInterruptedDispatchRecovery(
            fixture.Goal.Id, fixture.Task.Id, GoalStatus.Active, WorkTaskStatus.Running);

        Assert.Empty(fixture.ReadEvents().Skip(start));
        var effect = Assert.Single(deferred.SnapshotEffects());
        Assert.Equal(GoalCreationLifecycleEffectKind.TaskStatusTransition, effect.Kind);
        effect.DeliverIdempotently(fixture.Writer);
        effect.DeliverIdempotently(fixture.Writer);
        deferred.CompleteDeliveryTo(fixture.Writer);
        fixture.Kernel.ConcludeInterruptedDispatchRecovery(
            fixture.Goal.Id, fixture.Task.Id, GoalStatus.Active, WorkTaskStatus.Assigned);

        Assert.Collection(Transitions(fixture.ReadEvents().Skip(start)),
            entry =>
            {
                AssertTransition(entry, fixture, "Assigned", "Running",
                    "DefaultTasks.ConcludeInterruptedDispatchRecovery");
                Assert.Equal(effect.DeliveryId, entry.GetProperty("deliveryId").GetString());
            },
            entry => AssertTransition(entry, fixture, "Running", "Assigned",
                "DefaultTasks.ConcludeInterruptedDispatchRecovery"));
        Assert.Empty(deferred.SnapshotEffects());
    }

    [Fact]
    public void EveryTaskStatusCallHasAUniqueDeclaredSite()
    {
        var root = VerifiedRepositoryRoot.Find();
        var recorderPath = Path.Combine(root, "src", "Mcg.AgentOrchestrator.Core", "Application",
            "TaskStatusTransitionRecorder.cs");
        var constants = Regex.Matches(File.ReadAllText(recorderPath),
                "const string (?<name>\\w+) = \"(?<value>[^\"]+)\";")
            .ToDictionary(match => match.Groups["name"].Value, match => match.Groups["value"].Value);
        Assert.NotEmpty(constants);
        Assert.Equal(constants.Count, constants.Values.Distinct(StringComparer.Ordinal).Count());

        // Only reconstruction and the reservation's snapshot projection discard mutation results.
        var nonRuntimeCalls = new Dictionary<string, string[]>
        {
            ["src/Mcg.AgentOrchestrator.Core/Domain/TaskSpec.cs"] =
                ["task.AssignTo(new AgentId(snapshot.AssignedAgentId));", "task.SetStatus(snapshot.Status);"],
            ["src/Mcg.AgentOrchestrator.Core/Domain/RetryAdmission.cs"] =
                ["task.BindPreparedDispatch(preparedDispatch);"]
        };
        var exclusionsSeen = new List<string>();
        var calls = new List<(string Location, string? Site)>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         .Any(part => part is "bin" or "obj" or ".scratch" or ".orchestrator-prototype")))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var statusCall = line.Contains(".SetStatus(", StringComparison.Ordinal) &&
                    !line.Contains("goal.SetStatus(", StringComparison.Ordinal) &&
                    !line.Contains(".SetStatus(GoalStatus.", StringComparison.Ordinal);
                if (!statusCall &&
                    !line.Contains(".SetConductorCancelledStatus(", StringComparison.Ordinal) &&
                    !line.Contains(".AssignTo(", StringComparison.Ordinal) &&
                    !line.Contains(".ReinstateVerification(", StringComparison.Ordinal) &&
                    !line.Contains(".BindPreparedDispatch(", StringComparison.Ordinal))
                    continue;
                if (nonRuntimeCalls.TryGetValue(relative, out var exclusions) && exclusions.Contains(line.Trim()))
                {
                    exclusionsSeen.Add(line.Trim());
                    continue;
                }
                var site = Regex.Match(line, @"TaskStatusTransitionRecorder\.(?<name>\w+)\);$");
                calls.Add(($"{relative}:{i + 1}", site.Success ? site.Groups["name"].Value : null));
            }
        }

        var inventory = string.Join(Environment.NewLine, calls.Select(call => $"{call.Location}: {call.Site ?? "MISSING"}"));
        Assert.NotEmpty(calls);
        Assert.True(calls.All(call => call.Site is not null && constants.ContainsKey(call.Site)), inventory);
        Assert.True(calls.Select(call => call.Site).Distinct(StringComparer.Ordinal).Count() == calls.Count, inventory);
        Assert.True(constants.Keys.All(name => calls.Count(call => call.Site == name) == 1), inventory);
        Assert.Equal(nonRuntimeCalls.Values.SelectMany(lines => lines).Order(), exclusionsSeen.Order());
        AssertDomainMutationCensus(root);
    }

    private static void AssertDomainMutationCensus(string root)
    {
        var domain = Path.Combine(root, "src", "Mcg.AgentOrchestrator.Core", "Domain");
        var mutations = Directory.EnumerateFiles(domain, "TaskSpec*.cs")
            .SelectMany(File.ReadAllLines)
            .Where(line => Regex.IsMatch(line, @"^\s*(?:this\.)?Status\s*=(?!=)") ||
                           Regex.IsMatch(line, @"^\s*SetStatus\("))
            .Select(line => line.Trim()).Order().ToArray();
        Assert.Equal(new[]
        {
            "Status = WorkTaskStatus.Assigned;",
            "Status = status;",
            "Status = WorkTaskStatus.Cancelled;",
            "Status = WorkTaskStatus.Completed;",
            "SetStatus(WorkTaskStatus.Running);",
            "SetStatus(WorkTaskStatus.Running);"
        }.Order(), mutations);
    }

    private static JsonElement[] Transitions(IEnumerable<JsonElement> events) => events
        .Where(entry => entry.GetProperty("eventType").GetString() == "task-status-transition").ToArray();

    private static void AssertTransition(JsonElement entry, Fixture fixture, string from, string to, string site)
    {
        Assert.Equal(fixture.Goal.Id.Value, entry.GetProperty("goalId").GetString());
        Assert.Equal(fixture.Task.Id.Value, entry.GetProperty("taskId").GetString());
        Assert.Equal(from, entry.GetProperty("from").GetString());
        Assert.Equal(to, entry.GetProperty("to").GetString());
        Assert.Equal(site, entry.GetProperty("site").GetString());
        Assert.Equal(1, entry.EnumerateObject().Count(property => property.Name == "goalId"));
        Assert.Equal(1, entry.EnumerateObject().Count(property => property.Name == "taskId"));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "task-status-transition-" + Guid.NewGuid().ToString("N"));
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal GoalLifecycleEventWriter Writer { get; }
        internal Goal Goal { get; }
        internal TaskSpec Task => Goal.Tasks[0];

        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Writer = new GoalLifecycleEventWriter(Root);
            Kernel.SetEventWriter(Writer);
            Goal = Kernel.CreateGoal("Measure task transitions.",
                [new TaskSpec(TaskId.New(), "Implement measurement.", AgentRole.Developer)]);
            Kernel.ActivateGoal(Goal.Id, AgentCatalog.Default().Agents);
            Assert.Equal(WorkTaskStatus.Assigned, Task.Status);
        }

        internal void Dispatch() => Kernel.RecordTaskDispatch(Goal.Id, Task.Id,
            new TaskDispatchRecord("worker", "codex exec", Root, DateTimeOffset.UtcNow));

        internal TaskProcessRecord Process() => new(123, "codex exec", Root,
            Path.Combine(Root, "stdout"), Path.Combine(Root, "stderr"), Path.Combine(Root, "exit"),
            DateTimeOffset.UtcNow, null, null);

        internal JsonElement[] ReadEvents() => File.ReadAllLines(Path.Combine(Root, $"{Goal.Id.Value}.jsonl"))
            .Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            }).ToArray();

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
