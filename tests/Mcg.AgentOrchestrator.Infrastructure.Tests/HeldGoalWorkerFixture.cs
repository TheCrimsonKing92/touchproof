using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

internal sealed class HeldGoalWorkerFixture : IDisposable, IClock
{
    internal string Root { get; } = ConductorDriverTests.CreateTempDirectory();
    internal AgentOrchestratorKernel Kernel { get; } = new();
    internal Goal Goal { get; }
    internal TaskSpec Task { get; }
    internal TaskProcessRecord Process { get; }
    internal ConductorDriver Driver { get; }
    internal BackgroundDispatchRunner Runner { get; }
    internal long BytesRead;
    public DateTimeOffset UtcNow { get; } = DateTimeOffset.Parse("2026-11-15T06:00:00Z");

    internal HeldGoalWorkerFixture(int bytes)
    {
        Goal = Kernel.CreateGoal("Running worker incremental output", [new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer)]);
        Kernel.ActivateGoal(Goal.Id, [new AgentDefinition(new AgentId("reviewer"), "Reviewer", AgentRole.Reviewer,
            new ModelProfile("Test", "fixture", ModelCapability.Text, SubscriptionMode.ApiKey))]);
        Task = Goal.Tasks.Single();
        var stdout = Path.Combine(Root, "stdout.log");
        var stderr = Path.Combine(Root, "stderr.log");
        File.WriteAllText(stderr, string.Empty);
        Process = new TaskProcessRecord(999999, "codex exec prompt", Root, stdout, stderr,
            Path.Combine(Root, "exit.txt"), UtcNow.AddMinutes(-5), null, null);
        GrowTo(bytes);
        File.SetLastWriteTimeUtc(stderr, UtcNow.AddMinutes(-3).UtcDateTime);
        Kernel.RecordTaskDispatch(Goal.Id, Task.Id, new TaskDispatchRecord("codex-cli", Process.Command, Root, Process.StartedAt));
        Kernel.RecordTaskProcessStarted(Goal.Id, Task.Id, Process);
        Runner = new(this, postOutputIdleTimeout: TimeSpan.FromMinutes(2), isStillRunning: _ => true,
            tryKillOwnedProcess: _ => throw new InvalidOperationException("Running fixture must not be killed"),
            progressStallTimeout: TimeSpan.FromDays(1), startupHangTimeout: TimeSpan.FromDays(1),
            openLogReadStream: path => new CountingLogReadStream(
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
                read => BytesRead += read));
        Driver = ConductorDriverTests.MakeDriver(getFacts: _ => new(WorkspaceExists: true), executionDirectory: Root);
    }

    internal void GrowTo(int bytes)
    {
        using (var stream = new FileStream(Process.StandardOutputPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            var remaining = bytes - stream.Length;
            stream.Seek(0, SeekOrigin.End);
            var line = System.Text.Encoding.UTF8.GetBytes(new string('x', 79) + "\n");
            while (remaining > 0)
            {
                var count = (int)Math.Min(remaining, line.Length);
                stream.Write(line, 0, count);
                remaining -= count;
            }
        }
        File.SetLastWriteTimeUtc(Process.StandardOutputPath, UtcNow.AddMinutes(-3).UtcDateTime);
    }

    internal void Refresh()
    {
        var outcome = Runner.ReconcileLatestProcess(Kernel, Goal.Id, Task.Id);
        Runner.ApplyRefreshOutcomeAndWriteDiagnostics(Kernel, Goal.Id, Task.Id, outcome);
    }

    internal ConductorBatchLoop Loop() => new(refreshGoalDispatchesBeforeAdvance: (_, _) => { Refresh(); return null; });
    public void Dispose() => Directory.Delete(Root, recursive: true);
}
