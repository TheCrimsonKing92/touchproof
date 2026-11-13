using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every filesystem/database identity is fixture-local; the runner never starts a process.
internal sealed class SelectedExecutionBackstopFixture : ISelectedExecutionBackstopRunner, IDisposable
{
    internal string Home { get; } = Path.Combine(Path.GetTempPath(), "mcg-backstop-tests", Guid.NewGuid().ToString("N"));
    internal SqliteRunEventStore Events { get; }
    internal SelectedExecutionBackstopStore Store { get; }
    internal CollaborationItemStore Items { get; }
    internal ConcurrentQueue<SelectedExecutionBackstopRequest> Calls { get; } = new();
    internal ConcurrentQueue<string> Lines { get; } = new();
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Func<SelectedExecutionBackstopRequest, Task<SelectedExecutionBackstopOutcome>> Run { get; set; } =
        _ => Task.FromResult(SelectedExecutionBackstopOutcome.Green);
    internal DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    internal string Head { get; set; } = "main-2";
    internal SelectedExecutionBackstopCoordinator Coordinator { get; }

    internal SelectedExecutionBackstopFixture()
    {
        Directory.CreateDirectory(Home);
        Events = new(Path.Combine(Home, "run-events.db"));
        Store = new(Events);
        Items = CollaborationItemStore.ForDirectory(Home);
        Coordinator = CreateCoordinator();
    }

    internal SelectedExecutionBackstopCoordinator CreateCoordinator(Action<string>? progress = null) =>
        new(Store, this, Items, Home, () => Now, line => { Lines.Enqueue(line); progress?.Invoke(line); });
    internal static AcceptanceSelectedExecutionRecord Applied => AcceptanceSelectedExecutionFixture.Observe(
        AcceptanceSelectedExecutionFixture.Session(AcceptanceSelectedExecutionMode.Enforce)).SelectedExecution!;
    internal Task Land(string goal = "goal-1", string commit = "main-1", AcceptanceSelectedExecutionRecord? record = null) =>
        Coordinator.NoteLandingAsync(goal, commit, record ?? Applied, "main-0");
    internal Task Service(bool capacity = true, bool suppressed = false) => Coordinator.ServiceAsync(() => Head, [], capacity, suppressed);
    internal async Task Finish()
    {
        await Service();
        await Coordinator.DrainAsync();
    }
    public Task<SelectedExecutionBackstopOutcome> RunAsync(SelectedExecutionBackstopRequest request, CancellationToken cancellationToken)
    {
        Calls.Enqueue(request);
        Entered.TrySetResult();
        return Run(request);
    }
    public void Dispose() => Directory.Delete(Home, recursive: true);
}
