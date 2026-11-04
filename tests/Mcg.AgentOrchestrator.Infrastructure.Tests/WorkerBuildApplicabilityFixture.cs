using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all state, repositories and admission identities belong to this fixture.
internal sealed class WorkerBuildApplicabilityFixture
{
    internal const string Project = "src/Feature/Feature.csproj";
    internal const string Source = "src/Feature/Feature.cs";
    internal readonly CompletionBuildEvidenceFixture Build = new(workerBuildProtocol1: true);
    internal TaskSpec Task => Build.Task;
    internal string Root => Task.LastDispatch!.WorkingDirectory;
    internal string DispatchId => BackgroundDispatchRunner.BuildDispatchId(Build.GoalId, Build.TaskId,
        Task.LastDispatch! with { ProviderSessionId = null });
    internal readonly TaskIntentAdmissionReference Admission;
    internal Func<string, Dictionary<string, string>?, string[], WorktreeTreeDigestGitStep>? DigestGitRunner;

    internal WorkerBuildApplicabilityFixture(bool? scopeComplete = null)
    {
        Mutate(task => task with { LastProcess = task.LastProcess! with
            { ProcessIdentityStartedAt = Build.Clock.UtcNow, ProcessIdentityImagePath = "fixture-owned-host" } });
        Assert.True(InfrastructureTestSupport.TryReadGitHeadInProcess(Root, out var head));
        WorkerBuildProjectScope.Prepare(Root, Build.GoalId.Value, Build.TaskId.Value, "main");
        WorkerBuildScopeDispatchPreparation.RecordDispatchBaseCommit(Build.Kernel, Build.GoalId, Task, Root, head!);
        var process = Task.LastProcess!;
        if (scopeComplete is { } complete)
            File.WriteAllText(process.StandardOutputPath, File.ReadAllText(process.StandardOutputPath)
                .Replace("END_WORKER_RESULT", $"assigned_scope_complete: {complete.ToString().ToLowerInvariant()}\nEND_WORKER_RESULT", StringComparison.Ordinal));
        var reference = new WorkerBuildHostReference(Build.StatePath, Build.Root, Build.GoalId.Value,
            Build.TaskId.Value, Task.LastDispatch!.DispatchedAt, DispatchId, StorageRoot: Build.Storage.RootPath);
        DispatchProcessHost.WriteParameters(process.StandardOutputPath[..^".out.log".Length] + ".dispatch.json",
            new(Task.LastDispatch.Command, Root, process.StandardOutputPath, process.StandardErrorPath,
                process.ExitCodePath, null, true, WorkerBuildHost: reference));
        var store = new SqliteOperatorIntentStore(Path.Combine(Build.Root, "operator-intents.db"), Build.Root);
        Admission = store.CaptureAdmission(Build.GoalId.Value, Build.TaskId.Value) with { DispatchedAt = Task.LastDispatch.DispatchedAt };
        store.BindPreparedAdmission(Admission, Task.LastDispatch.DispatchedAt);
        Save();
    }

    internal string Digest()
    {
        Assert.True(WorktreeTreeDigest.TryCompute(Root, out var digest, out _));
        return digest;
    }

    internal WorkerBuildProjectScope.Result Scope() => WorkerBuildProjectScope.ResolveForDispatch(Root,
        [Project], Build.GoalId, Build.TaskId, Task.LastDispatch!);

    internal WorkerBuildInvocation Invocation(string id, WorkerBuildOutcome? outcome = WorkerBuildOutcome.Success,
        string? digest = null, string[]? projects = null)
    {
        projects ??= [Project];
        var process = Task.LastProcess!;
        var host = new WorkerBuildProcess(process.ProcessId, process.ProcessIdentityStartedAt!.Value,
            process.ProcessIdentityImagePath!);
        var observations = new List<WorkerBuildObservation> { new(WorkerBuildPhase.Requested, Build.Clock.UtcNow) };
        var started = outcome is WorkerBuildOutcome.Success or WorkerBuildOutcome.CompilerFailure or WorkerBuildOutcome.TimeoutAfterStart;
        var children = started ? projects.Select((_, index) => new WorkerBuildProcess(10000 + index,
            Build.Clock.UtcNow, "fixture-compiler")).ToArray() : [];
        if (outcome == WorkerBuildOutcome.CapacityBeforeStart)
            observations.Add(new(WorkerBuildPhase.WaitingForBuildSlot, Build.Clock.UtcNow));
        foreach (var child in children)
        {
            observations.Add(new(WorkerBuildPhase.ChildRegistered, Build.Clock.UtcNow));
            observations.Add(new(WorkerBuildPhase.CompilerStarted, Build.Clock.UtcNow));
        }
        if (outcome is not null) observations.Add(new(WorkerBuildPhase.Completed, Build.Clock.UtcNow));
        var invocation = new WorkerBuildInvocation(id, id, Build.GoalId.Value, Build.TaskId.Value,
            Task.LastDispatch!.DispatchedAt, DispatchId, Root, digest ?? Digest(), Scope().Identity,
            projects, "Debug", Build.Clock.UtcNow.AddMinutes(1), host, host, Admission.StorePath, Admission.Version,
            1, observations, Children: children, Outcome: outcome,
            CompilerExitCode: outcome == WorkerBuildOutcome.Success ? 0 : started ? 1 : null,
            CustodyReleased: outcome is not null);
        invocation.RequireValid();
        return invocation;
    }

    internal void SetInvocations(params WorkerBuildInvocation[] invocations)
    {
        Mutate(task => task with { WorkerBuildInvocations = invocations });
        Save();
    }

    internal void Mutate(Func<TaskSnapshot, TaskSnapshot> mutate) => Build.Kernel = AgentOrchestratorKernel.FromSnapshot(
        Build.Kernel.ExportSnapshot() with { Goals = Build.Kernel.ExportSnapshot().Goals.Select(goal => goal with
        { Tasks = goal.Tasks.Select(task => task.Id == Build.TaskId.Value ? mutate(task) : task).ToArray() }).ToArray() }, Build.Clock);

    internal void Save() => new SqliteOrchestratorStateRepository(Build.StatePath).SaveAsync(Build.Kernel).GetAwaiter().GetResult();
    internal WorkerBuildInvocationOperations Operations() => new(Build.StatePath, Build.Root, Build.Storage,
        isHostAlive: _ => false, digestGitRunner: DigestGitRunner);
    internal WorkerBuildConsumption Consume() => Operations().Consume(Build.Kernel, Build.GoalId, Build.TaskId, [Source], [Source]);
    internal BackgroundDispatchRunner Runner() => new(Build.Clock, isStillRunning: _ => false,
        completionBuildEvidence: Build.Coordinator(), resolveWorkerBuildReceiptPath: _ => Build.ReceiptPath)
        { WorkerBuildInvocations = Operations() };
}
