using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every filesystem/database identity is fixture-local.
internal sealed class SelectedExecutionBackstopFixture : IDisposable
{
    internal string Home { get; } = Path.Combine(Path.GetTempPath(), "mcg-backstop-tests", Guid.NewGuid().ToString("N"));
    internal SqliteRunEventStore Events { get; }
    internal SelectedExecutionBackstopStore Store { get; }
    internal CollaborationItemStore Items { get; }
    internal ConcurrentQueue<SelectedExecutionBackstopRequest> Calls { get; } = new();
    internal ConcurrentQueue<string> Lines { get; } = new();
    internal DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
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
        new(Store, Items, Home, () => Now, line => { Lines.Enqueue(line); progress?.Invoke(line); });
    internal static AcceptanceSelectedExecutionRecord Applied => AcceptanceSelectedExecutionFixture.Observe(
        AcceptanceSelectedExecutionFixture.Session(AcceptanceSelectedExecutionMode.Enforce)).SelectedExecution!;
    internal Task Land(string goal = "goal-1", string commit = "main-1", AcceptanceSelectedExecutionRecord? record = null) =>
        Coordinator.NoteLandingAsync(goal, commit, record ?? Applied, "main-0");
    internal Task Service() => Coordinator.ServiceAsync();
    internal Task<SelectedExecutionGateMissDetector.Detection> DetectMiss() =>
        new SelectedExecutionGateMissDetector(Coordinator).ObserveAsync("gate-goal",
            Arm(FindingEvidenceArm.Candidate), Arm(FindingEvidenceArm.Baseline));

    private static FindingEvidenceArmReceipt Arm(FindingEvidenceArm arm) =>
        new(arm, "gate-sha", FindingEvidenceArmDisposition.Red,
            Accepted: true, Passed: false, Summary: "synthetic gate failure",
            FailingTestIdentities: ["Candidate.X.Missed"], TestResultPaths: []);
    public void Dispose() => Directory.Delete(Home, recursive: true);
}
