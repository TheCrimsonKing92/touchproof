using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class GoalStatusTransitionEventTests
{
    [Fact]
    public void ActiveThroughVerifyingToVerifiedRecordsBothSites()
    {
        using var fixture = new Fixture();
        var start = fixture.ReadEvents().Length;

        fixture.Kernel.ConcludeInterruptedDispatchRecovery(
            fixture.Goal.Id, fixture.Task.Id, GoalStatus.Verifying, WorkTaskStatus.Completed);
        Assert.True(fixture.Kernel.ReconcileGoalAcceptanceVerified(fixture.Goal.Id, "Acceptance passed."));

        var transitions = Transitions(fixture.ReadEvents().Skip(start));
        Assert.Collection(transitions,
            entry => AssertTransition(entry, fixture.Goal.Id, "Active", "Verifying",
                "DefaultTasks.ConcludeInterruptedDispatchRecovery"),
            entry => AssertTransition(entry, fixture.Goal.Id, "Verifying", "Verified",
                "GoalLifecycle.ReconcileGoalAcceptanceVerified"));
        Assert.NotEqual(transitions[0].GetProperty("site").GetString(), transitions[1].GetProperty("site").GetString());
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
    }

    [Fact]
    public void DispatchWithUnchangedActiveStatusWritesNoTransition()
    {
        using var fixture = new Fixture();
        var start = fixture.ReadEvents().Length;

        fixture.Kernel.RecordTaskDispatch(fixture.Goal.Id, fixture.Task.Id,
            new TaskDispatchRecord("worker", "codex exec", fixture.Root, DateTimeOffset.UtcNow));

        var events = fixture.ReadEvents().Skip(start).ToArray();
        Assert.Contains(events, entry => entry.GetProperty("eventType").GetString() == "TaskDispatched");
        Assert.Empty(Transitions(events));
        Assert.Equal(GoalStatus.Active, fixture.Goal.Status);
    }

    [Fact]
    public void ExplicitParkRecordsActualStatusAndStableSite()
    {
        using var fixture = new Fixture();
        var start = fixture.ReadEvents().Length;

        fixture.Kernel.ParkGoal(fixture.Goal.Id, "Operator hold.");

        AssertTransition(Assert.Single(Transitions(fixture.ReadEvents().Skip(start))), fixture.Goal.Id,
            "Active", "Parked", "ApplyExplicitDisposition.Park");
        Assert.Equal(GoalStatus.Parked, fixture.Goal.Status);
    }

    [Fact]
    public void ParkedLifecycleChangeWritesNothingUntilUnpark()
    {
        using var fixture = new Fixture();
        fixture.Kernel.ParkGoal(fixture.Goal.Id, "Operator hold.");
        var start = fixture.ReadEvents().Length;

        fixture.Kernel.ConcludeInterruptedDispatchRecovery(
            fixture.Goal.Id, fixture.Task.Id, GoalStatus.Verifying, WorkTaskStatus.Completed);

        Assert.Empty(Transitions(fixture.ReadEvents().Skip(start)));
        Assert.Equal(GoalStatus.Parked, fixture.Goal.Status);
        Assert.Equal(GoalStatus.Verifying, fixture.Goal.LifecycleStatus);

        fixture.Kernel.UnparkGoal(fixture.Goal.Id, "Resume retained lifecycle.");

        AssertTransition(Assert.Single(Transitions(fixture.ReadEvents().Skip(start))), fixture.Goal.Id,
            "Parked", "Verifying", "ApplyExplicitDisposition.Unpark");
        Assert.Equal(GoalStatus.Verifying, fixture.Goal.Status);
    }

    [Fact]
    public void CriticalDispatchGateForwardsStatusTransitions()
    {
        using var fixture = new Fixture();
        fixture.Kernel.SetEventWriter(new CriticalDispatchLifecycleEventGate(fixture.Writer));
        var start = fixture.ReadEvents().Length;

        fixture.Kernel.ConcludeInterruptedDispatchRecovery(
            fixture.Goal.Id, fixture.Task.Id, GoalStatus.Verifying, WorkTaskStatus.Completed);

        AssertTransition(Assert.Single(Transitions(fixture.ReadEvents().Skip(start))), fixture.Goal.Id,
            "Active", "Verifying", "DefaultTasks.ConcludeInterruptedDispatchRecovery");
    }

    [Fact]
    public void DeferredCreationWriterReplaysAndThenForwardsTransitions()
    {
        using var fixture = new Fixture();
        var deferred = new DeferredGoalLifecycleEventWriter();
        fixture.Kernel.SetEventWriter(deferred);
        var start = fixture.ReadEvents().Length;

        fixture.Kernel.ConcludeInterruptedDispatchRecovery(
            fixture.Goal.Id, fixture.Task.Id, GoalStatus.Verifying, WorkTaskStatus.Completed);

        Assert.Empty(fixture.ReadEvents().Skip(start));
        var effects = deferred.SnapshotEffects();
        Assert.Collection(effects,
            entry => Assert.Equal(GoalCreationLifecycleEffectKind.TaskStatusTransition, entry.Kind),
            entry => Assert.Equal(GoalCreationLifecycleEffectKind.GoalStatusTransition, entry.Kind));
        var effect = effects[1];
        Assert.Equal(GoalCreationLifecycleEffectKind.GoalStatusTransition, effect.Kind);
        effects[0].DeliverIdempotently(fixture.Writer);
        effect.DeliverIdempotently(fixture.Writer);
        deferred.CompleteDeliveryTo(fixture.Writer);
        Assert.True(fixture.Kernel.ReconcileGoalAcceptanceVerified(fixture.Goal.Id, "Acceptance passed."));

        var transitions = Transitions(fixture.ReadEvents().Skip(start));
        Assert.Collection(transitions,
            entry =>
            {
                AssertTransition(entry, fixture.Goal.Id, "Active", "Verifying",
                    "DefaultTasks.ConcludeInterruptedDispatchRecovery");
                Assert.Equal(effect.DeliveryId, entry.GetProperty("deliveryId").GetString());
            },
            entry => AssertTransition(entry, fixture.Goal.Id, "Verifying", "Verified",
                "GoalLifecycle.ReconcileGoalAcceptanceVerified"));
        Assert.Empty(deferred.SnapshotEffects());
    }

    [Fact]
    public void EveryRuntimeStatusCallHasAUniqueDeclaredSite()
    {
        var root = VerifiedRepositoryRoot.Find();
        var recorderPath = Path.Combine(root, "src", "Mcg.AgentOrchestrator.Core", "Application", "GoalStatusTransitionRecorder.cs");
        var constants = Regex.Matches(File.ReadAllText(recorderPath),
                "const string (?<name>\\w+) = \"(?<value>[^\"]+)\";")
            .ToDictionary(match => match.Groups["name"].Value, match => match.Groups["value"].Value);
        Assert.NotEmpty(constants);
        Assert.Equal(constants.Count, constants.Values.Distinct(StringComparer.Ordinal).Count());

        // These create or project state; they do not mutate a live kernel goal.
        var nonRuntimeCalls = new Dictionary<string, string[]>
        {
            ["src/Mcg.AgentOrchestrator.Core/Domain/Goal.cs"] =
                ["goal.SetStatus(metadata.Status);", "goal.SetStatus(snapshot.Status);"],
            ["src/Mcg.AgentOrchestrator.Core/Application/ProviderUsageHoldView.cs"] =
                ["goal.SetStatus(row.Status);"],
            ["src/Mcg.AgentOrchestrator.Core/Application/ProviderHoldSeedPruning.cs"] =
                ["goal.SetStatus(row.Status);"]
        };
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
                if (!line.Contains(".SetStatus(GoalStatus.", StringComparison.Ordinal) &&
                    !line.Contains("goal.SetStatus(", StringComparison.Ordinal) &&
                    !line.Contains("goal.ApplyExplicitDisposition(", StringComparison.Ordinal))
                    continue;
                if (nonRuntimeCalls.TryGetValue(relative, out var exclusions) && exclusions.Contains(line.Trim()))
                    continue;
                var site = Regex.Match(line, @"GoalStatusTransitionRecorder\.(?<name>\w+)\);");
                calls.Add(($"{relative}:{i + 1}", site.Success ? site.Groups["name"].Value : null));
            }
        }

        var inventory = string.Join(Environment.NewLine, calls.Select(call => $"{call.Location}: {call.Site ?? "MISSING"}"));
        Assert.NotEmpty(calls);
        Assert.True(calls.All(call => call.Site is not null && constants.ContainsKey(call.Site)), inventory);
        Assert.True(calls.Select(call => call.Site).Distinct(StringComparer.Ordinal).Count() == calls.Count, inventory);
        Assert.True(constants.Keys.All(name => calls.Count(call => call.Site == name) == 1), inventory);
    }

    private static JsonElement[] Transitions(IEnumerable<JsonElement> events) => events
        .Where(entry => entry.GetProperty("eventType").GetString() == "goal-status-transition").ToArray();

    private static void AssertTransition(JsonElement entry, GoalId goalId, string from, string to, string site)
    {
        Assert.Equal(goalId.Value, entry.GetProperty("goalId").GetString());
        Assert.Equal(from, entry.GetProperty("from").GetString());
        Assert.Equal(to, entry.GetProperty("to").GetString());
        Assert.Equal(site, entry.GetProperty("site").GetString());
        Assert.Equal(1, entry.EnumerateObject().Count(property => property.Name == "goalId"));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "goal-status-transition-" + Guid.NewGuid().ToString("N"));
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal GoalLifecycleEventWriter Writer { get; }
        internal Goal Goal { get; }
        internal TaskSpec Task => Goal.Tasks[0];

        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Writer = new GoalLifecycleEventWriter(Root);
            Kernel.SetEventWriter(Writer);
            Goal = Kernel.CreateGoal("Measure status transitions.",
                [new TaskSpec(TaskId.New(), "Implement measurement.", AgentRole.Developer)]);
            Kernel.ActivateGoal(Goal.Id, AgentCatalog.Default().Agents);
            Assert.Equal(GoalStatus.Active, Goal.Status);
        }

        internal JsonElement[] ReadEvents() => File.ReadAllLines(Path.Combine(Root, $"{Goal.Id.Value}.jsonl"))
            .Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            }).ToArray();

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
