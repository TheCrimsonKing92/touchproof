using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Private host root and injected executor/clock; no shared build slots or ambient state.
internal sealed class RemoteLaneCostTestFixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "remote-cost-" + Guid.NewGuid().ToString("N"));
    internal FakeRemoteLaneExecutor Executor { get; } = new();
    internal ManualRemoteLaneClock Clock { get; } = new();
    internal RemoteLaneCoordinator Coordinator { get; }
    internal RemoteLaneExecutorEntry Entry { get; }
    internal string Ledger => RemoteExecutorHealthLedger.ResolveStorePath(Root);

    internal RemoteLaneCostTestFixture(IEnumerable<RemoteExecutorHealthRecord>? samples = null,
        int slots = 3, double? costBudget = 4)
    {
        Directory.CreateDirectory(Root);
        Entry = new("executor", 3600, Slots: slots, CostBudget: costBudget);
        Coordinator = new(new([Entry], [], null),
            new RemoteLaneCandidateIdentity("attempt", "goal", "commit", "tree", "main", "manifest"),
            Root, Path.Combine(Root, "artifacts", "attempt"), Executor, Clock, TimeSpan.FromMilliseconds(1), null)
        { AdmissionHistory = new((samples ?? []).ToArray()) };
    }

    internal static Check Lane(string name) => new()
    {
        Name = "infrastructure tests: " + name, Type = "dotnet-test",
        Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
        Arguments = ["--filter", "FullyQualifiedName~" + name + "Tests"]
    };

    internal static RemoteExecutorHealthRecord Sample(string name, double cpu = 80, double wall = 100,
        string executor = "executor", double duration = 600, int? concurrency = null)
    {
        var lane = Lane(name);
        var binding = new RemoteLaneBinding(executor, lane.Name, "commit", "tree", "main",
            GoalAcceptanceVerifier.ShortHash(lane.Arguments[1]), "manifest");
        return new(DateTimeOffset.UnixEpoch, executor, "sample", lane.Name, RemoteLaneOutcomeCode.Accepted,
            null, binding, binding, RemoteTestHostCpuSeconds: cpu, Phases: new(TestSeconds: wall),
            Attempt: new([], null, [], JsonSerializer.SerializeToElement(new { seconds = duration }), null, null),
            Admission: concurrency is { } count ? new(executor, count, [], 1, 1, "light", 4, count) : null);
    }

    internal Task<RemoteLaneExecutorEntry?> Claim(string name) => Coordinator.ClaimForGateAsync(Lane(name), default);
    internal Task<RemoteLaneOutcome> Run(string name, CancellationToken token = default,
        double timeoutSeconds = 2400, double deadlineSeconds = 1200) => Coordinator.RunRemoteAsync(
            Lane(name), Entry, TimeSpan.FromSeconds(timeoutSeconds), TimeSpan.FromSeconds(deadlineSeconds), token);

    internal void ReturnSuccessfulResult()
    {
        var trx = Path.Combine(Root, "remote.trx");
        File.WriteAllText(trx, """
            <TestRun><Times start="2026-10-30T00:00:00Z" finish="2026-10-30T00:01:40Z" />
            <ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" notExecuted="0" /></ResultSummary></TestRun>
            """);
        Executor.Submit = (request, _) =>
        {
            var handle = new FakeRemoteLaneExecutor.Handle(Clock.GetUtcNow());
            handle.Publish(new(request.ExecutorId, request.Lane, request.FilterHash, request.VerifyingCommitSha,
                request.CandidateTreeSha, request.MainSha, request.ManifestIdentity, 0, [trx]));
            return Task.FromResult(new RemoteLaneSubmission(handle));
        };
    }

    internal static async Task Event(Task task, string name)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { throw new TimeoutException("Missing event: " + name); }
    }

    public void Dispose()
    {
        Coordinator.Dispose();
        Directory.Delete(Root, true);
    }
}
