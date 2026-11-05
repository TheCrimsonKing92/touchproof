using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class WorkerBuildInvocationOperations(string statePath, string executionDirectory,
    DotnetBuildStorageRoot? storageRoot = null,
    Func<WorkerBuildProcess, bool>? isHostAlive = null,
    Func<string, Dictionary<string, string>?, string[], WorktreeTreeDigestGitStep>? digestGitRunner = null,
    string? projectHomeDirectory = null, string integrationBranch = TrunkBranchName.Default) : IWorkerBuildInvocationOperations
{
    internal const string OperationName = "dispatch:worker-build";
    private readonly SqliteOrchestratorStateRepository _repository = new(statePath);

    public WorkerBuildHostReference Prepare(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId)
    {
        var task = kernel.GetTask(goalId, taskId);
        if (task.LastDispatch?.WorkerBuildScope is null)
            throw new InvalidOperationException("Host build enrollment requires dispatch-pinned project scope.");
        kernel.RecordWorkerBuildProtocol(goalId, taskId, 1);
        return new(statePath, executionDirectory, goalId.Value, taskId.Value, task.LastDispatch.DispatchedAt,
            BuildWorkerDispatchId(goalId, taskId, task.LastDispatch), StorageRoot: storageRoot?.RootPath,
            StructuralPolicy: WorkerBuildStructuralPreflight.CapturePolicy(task.LastDispatch, projectHomeDirectory, integrationBranch));
    }

    // Provider sessions are learned from live heartbeats after enrollment. They are
    // metadata, not worker-build authority; keep the shared dispatch hash unchanged.
    private static string BuildWorkerDispatchId(GoalId goalId, TaskId taskId, TaskDispatchRecord dispatch) =>
        BackgroundDispatchRunner.BuildDispatchId(goalId, taskId, dispatch with { ProviderSessionId = null });

    internal AgentOrchestratorKernel Load(string goalId) => _repository.LoadGoalsAsync([new GoalId(goalId)]).GetAwaiter().GetResult();

    public bool ReconcileOutstanding(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId, bool cancelled = false)
    {
        var canonical = Load(goalId.Value).GetTask(goalId, taskId);
        foreach (var invocation in canonical.WorkerBuildInvocations.Where(i => !i.IsTerminal || !i.CustodyReleased))
            Reconcile(invocation, cancelled);
        canonical = Load(goalId.Value).GetTask(goalId, taskId);
        Synchronize(kernel, goalId, taskId, canonical.WorkerBuildInvocations);
        return canonical.WorkerBuildInvocations.All(i => i.IsTerminal && i.CustodyReleased);
    }

    private static void Synchronize(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId,
        IReadOnlyList<WorkerBuildInvocation> invocations)
    {
        kernel.ReconcileWorkerBuildInvocations(goalId, taskId, invocations);
    }

    internal WorkerBuildInvocation Store(WorkerBuildInvocation next, int? expectedVersion) => _repository
        .TransactGoalStateAsync(new GoalId(next.GoalId), (state, _) =>
        {
            if (state is null) throw new InvalidOperationException("Worker build goal is unavailable.");
            var loaded = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([state.Goal], []));
            var task = loaded.GetTask(new GoalId(next.GoalId), new TaskId(next.TaskId));
            var current = task.WorkerBuildInvocations.SingleOrDefault(i => i.Id == next.Id);
            if (current?.Version != expectedVersion)
                throw new InvalidOperationException("Worker build transition lost expected-version authority.");
            if (current is null && (task.LastDispatch?.DispatchedAt != next.DispatchAt ||
                task.LastDispatch.WorkerBuildProtocolVersion != 1 || task.Status != WorkTaskStatus.Running))
                throw new UnauthorizedAccessException("Worker build request lost its current dispatch authority.");
            loaded.RecordWorkerBuildInvocation(new GoalId(next.GoalId), new TaskId(next.TaskId), next);
            var updated = state with { Goal = loaded.ExportGoalSnapshot(new GoalId(next.GoalId)) };
            return Task.FromResult((true, (GoalStateSnapshot?)updated, next));
        }).GetAwaiter().GetResult();

    internal static TaskSpec RequireOrigin(AgentOrchestratorKernel kernel, WorkerBuildHostReference reference,
        WorkerBuildProcess host, bool requireRunning)
    {
        var goal = kernel.GetGoal(new GoalId(reference.GoalId));
        var task = kernel.GetTask(goal.Id, new TaskId(reference.TaskId));
        var dispatch = task.LastDispatch;
        var process = task.LastProcess;
        // This check is the authority-negative-control mutation seam. No worker field can supply it.
        if (dispatch is null || dispatch.WorkerBuildProtocolVersion != 1 || dispatch.DispatchedAt != reference.DispatchAt ||
            BuildWorkerDispatchId(goal.Id, task.Id, dispatch) != reference.DispatchId ||
            process is null || process.ProcessId != host.Pid || process.ProcessIdentityStartedAt != host.StartedAt ||
            string.IsNullOrWhiteSpace(process.ProcessIdentityImagePath) ||
            !string.Equals(process.ProcessIdentityImagePath, host.ImagePath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFullPath(dispatch.WorkingDirectory), Path.GetFullPath(process.WorkingDirectory), StringComparison.OrdinalIgnoreCase) ||
            task.Status == WorkTaskStatus.Cancelled || goal.Status is GoalStatus.Cancelled or GoalStatus.Superseded ||
            requireRunning && (task.Status != WorkTaskStatus.Running || !process.IsRunning || goal.Status != GoalStatus.Active))
            throw new UnauthorizedAccessException("Worker build canonical dispatch/host binding rejected.");
        return task;
    }

    public WorkerBuildConsumption Consume(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId,
        IReadOnlyList<string> changedPaths, IReadOnlyList<string> dirtyPaths)
    {
        var current = Load(goalId.Value);
        var task = current.GetTask(goalId, taskId);
        var dispatch = task.LastDispatch ?? throw new InvalidOperationException("Worker build consumer has no dispatch.");
        var reference = new WorkerBuildHostReference(statePath, executionDirectory, goalId.Value, taskId.Value,
            dispatch.DispatchedAt, BuildWorkerDispatchId(goalId, taskId, dispatch));
        // Validate before reconciling custody or importing history. Invalid records are
        // evidence refusals, not missing outcomes that authorize a completion build.
        var rejection = ValidateHistory(kernel, goalId, taskId, task);
        if (rejection is not null) return rejection;
        var invocations = task.WorkerBuildInvocations.Where(i => i.DispatchId == reference.DispatchId).ToArray();
        foreach (var pending in invocations.Where(i => !i.IsTerminal || !i.CustodyReleased)) Reconcile(pending);
        current = Load(goalId.Value);
        task = current.GetTask(goalId, taskId);
        rejection = ValidateHistory(kernel, goalId, taskId, task);
        if (rejection is not null) return rejection;
        var required = WorkerBuildEvidenceRequirement.FindRequiredProjects(dispatch.WorkingDirectory, task.RequiredRole, changedPaths, dirtyPaths);
        if (required.Count == 0 && invocations.Length == 0) return new(false, false, "worker_build_host=not-required");
        WorkerBuildApplicabilityExplanation? explanation = null;
        WorkerBuildConsumption Explain(WorkerBuildConsumption result) => result with { Applicability = explanation };
        WorkerBuildProjectScope.Result scope;
        try { scope = WorkerBuildProjectScope.ResolveForDispatch(dispatch.WorkingDirectory, required, goalId, taskId, dispatch); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            explanation = WorkerBuildApplicabilityExplanation.Capture(reference.DispatchId, null, null, task.WorkerBuildInvocations);
            return Explain(Rejected("scope-invalid", "project scope comparison evidence is unavailable"));
        }
        if (!WorktreeTreeDigest.TryCompute(dispatch.WorkingDirectory, out var digest, out var reason, out var failure, digestGitRunner))
        {
            explanation = WorkerBuildApplicabilityExplanation.Capture(reference.DispatchId, null, scope, task.WorkerBuildInvocations);
            return Explain(new(true, false, "worker_build_host=candidate-unavailable; " + WorktreeTreeDigestFailure.WithDetail(reason, failure)));
        }
        explanation = WorkerBuildApplicabilityExplanation.Capture(reference.DispatchId, digest, scope, task.WorkerBuildInvocations,
            (recorded, candidate) => WorkerBuildApplicabilityExplanation.CompareRecordedTrees(dispatch.WorkingDirectory, recorded, candidate));
        var applicable = task.WorkerBuildInvocations.Where(i => i.DispatchId == reference.DispatchId &&
            i.TreeDigest == digest && i.Projects.Intersect(scope.Projects, StringComparer.OrdinalIgnoreCase).Count() == scope.Projects.Count)
            .OrderByDescending(i => i.Deadline).FirstOrDefault();
        if (applicable is null)
        {
            // Missing evidence is eligible only under the current host-owned origin. A stale
            // caller, pending invocation or invalid identity cannot authorize another build.
            var caller = kernel.GetTask(goalId, taskId);
            var process = task.LastProcess;
            if (caller.LastDispatch is not { } callerDispatch || caller.LastProcess is not { } callerProcess ||
                callerDispatch.DispatchedAt != dispatch.DispatchedAt ||
                BuildWorkerDispatchId(goalId, taskId, callerDispatch) != reference.DispatchId ||
                process is null || callerProcess.ProcessId != process.ProcessId ||
                callerProcess.StartedAt != process.StartedAt ||
                process.ProcessIdentityStartedAt is not { } started ||
                string.IsNullOrWhiteSpace(process.ProcessIdentityImagePath))
                return Explain(Rejected("origin-invalid", "current dispatch/host identity is required"));
            var host = new WorkerBuildProcess(process.ProcessId, started, process.ProcessIdentityImagePath);
            try
            {
                RequireOrigin(current, reference, host, requireRunning: false);
                RequireOrigin(kernel, reference, host, requireRunning: false);
            }
            catch (UnauthorizedAccessException ex) { return Explain(Rejected("origin-invalid", ex.Message)); }
            if (task.WorkerBuildInvocations.Any(i => i.DispatchId == reference.DispatchId &&
                    (!i.IsTerminal || !i.CustodyReleased)))
                return Explain(new(true, false, "worker_build_host=reconciliation-pending; existing invocation retains custody"));
            if ((isHostAlive ?? IsExactProcessAlive)(host))
                return Explain(Rejected("origin-invalid", "worker host has not exited"));
            rejection = ValidateCompletionEnrollment(reference, dispatch, process);
            if (rejection is not null) return Explain(rejection);
            return Explain(new(true, false, "worker_build_host=missing-current-candidate-outcome; transport_cause=unknown",
                WorkerBuildEvidenceGap.MissingApplicableInvocation));
        }
        RequireOrigin(current, reference, applicable.Host, requireRunning: false);
        if (!applicable.IsTerminal || !applicable.CustodyReleased)
            return Explain(new(true, false, $"worker_build_invocation={applicable.Id}; phase={applicable.Phase}; reconciliation=pending"));
        WorkerBuildInvocation? consumed = null;
        var admission = new TaskIntentAdmissionReference(applicable.AdmissionStorePath, applicable.GoalId,
            applicable.TaskId, applicable.AdmissionVersion, applicable.DispatchAt);
        TaskIntentAdmission.ExecuteAuthorized(admission, "worker-build-consume:" + applicable.Id, () =>
        consumed = _repository.TransactGoalStateAsync(goalId, (state, cancellationToken) =>
        {
            if (state is null) throw new InvalidOperationException("Worker build consumption lost goal.");
            var loaded = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([state.Goal], []));
            var canonicalTask = RequireOrigin(loaded, reference, applicable.Host, requireRunning: false);
            var canonical = canonicalTask.WorkerBuildInvocations.Single(i => i.Id == applicable.Id);
            if (!canonical.SameOrigin(applicable) || !canonical.IsTerminal || !canonical.CustodyReleased ||
                !WorktreeTreeDigest.TryCompute(dispatch.WorkingDirectory, out var nowDigest, out _) || nowDigest != canonical.TreeDigest ||
                WorkerBuildProjectScope.ResolveForDispatch(dispatch.WorkingDirectory, required,
                    goalId, taskId, canonicalTask.LastDispatch!).Projects
                    .Except(canonical.Projects, StringComparer.OrdinalIgnoreCase).Any())
                throw new UnauthorizedAccessException("Worker build consumption lost candidate/scope applicability.");
            if (canonical.Consumed) return Task.FromResult((false, state, canonical));
            var next = canonical with { Version = canonical.Version + 1, Consumed = true };
            loaded.RecordWorkerBuildInvocation(goalId, taskId, next);
            return Task.FromResult((true, (GoalStateSnapshot?)(state with { Goal = loaded.ExportGoalSnapshot(goalId) }), next));
        }).GetAwaiter().GetResult());
        if (consumed is null) throw new InvalidOperationException("Worker build consumption did not execute.");
        Synchronize(kernel, goalId, taskId, Load(goalId.Value).GetTask(goalId, taskId).WorkerBuildInvocations);
        return Explain(new(consumed.Outcome != WorkerBuildOutcome.Success && consumed.Outcome != WorkerBuildOutcome.CompilerFailure,
            consumed.Outcome == WorkerBuildOutcome.CompilerFailure,
            $"worker_build_invocation={consumed.Id}; phase={consumed.Phase}; outcome={consumed.Outcome}; consumed={consumed.Consumed}"));
    }

    private static WorkerBuildConsumption Rejected(string reason, string detail) =>
        new(true, false, $"worker_build_host={reason}; {detail}", WorkerBuildEvidenceGap.Rejected);

    private static WorkerBuildConsumption? ValidateHistory(AgentOrchestratorKernel kernel,
        GoalId goalId, TaskId taskId, TaskSpec canonical)
    {
        // Catch only domain validation at the import boundary. Storage, process and
        // custody failures must retain their own failure channels.
        try
        {
            foreach (var invocation in canonical.WorkerBuildInvocations)
            {
                invocation.RequireValid();
                var origin = canonical.DispatchHistory.SingleOrDefault(d => d.DispatchedAt == invocation.DispatchAt);
                if (origin is null || invocation.GoalId != goalId.Value || invocation.TaskId != taskId.Value ||
                    invocation.DispatchId != BuildWorkerDispatchId(goalId, taskId, origin) ||
                    !string.Equals(invocation.WorktreeRoot, Path.GetFullPath(origin.WorkingDirectory), StringComparison.OrdinalIgnoreCase))
                    return Rejected("history-invalid", "invocation does not belong to its recorded goal/task/dispatch/worktree");
            }
            Synchronize(kernel, goalId, taskId, canonical.WorkerBuildInvocations);
        }
        catch (InvalidOperationException ex) { return Rejected("history-invalid", ex.Message); }
        return null;
    }

    private static WorkerBuildConsumption? ValidateCompletionEnrollment(WorkerBuildHostReference expected,
        TaskDispatchRecord dispatch, TaskProcessRecord process)
    {
        // Reuse the host's existing launch contract. A copied task snapshot cannot
        // enroll another state store or execution root merely by retaining a pid.
        const string suffix = ".out.log";
        if (!process.StandardOutputPath.EndsWith(suffix, StringComparison.Ordinal))
            return Rejected("enrollment-invalid", "host launch contract is unavailable; operator decision required");
        DispatchProcessHost.DispatchRunParameters parameters;
        try { parameters = DispatchProcessHost.ReadParameters(process.StandardOutputPath[..^suffix.Length] + ".dispatch.json"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        { return Rejected("enrollment-invalid", $"host launch contract is unreadable; operator decision required: {ex.Message}"); }
        var enrolled = parameters.WorkerBuildHost;
        if (enrolled is null || enrolled.GoalId != expected.GoalId || enrolled.TaskId != expected.TaskId ||
            enrolled.DispatchAt != expected.DispatchAt || enrolled.DispatchId != expected.DispatchId ||
            !SamePath(enrolled.StatePath, expected.StatePath) || !SamePath(enrolled.ExecutionDirectory, expected.ExecutionDirectory) ||
            !SamePath(parameters.WorkingDirectory, dispatch.WorkingDirectory) ||
            !SamePath(parameters.StdoutPath, process.StandardOutputPath) || !SamePath(parameters.StderrPath, process.StandardErrorPath) ||
            !SamePath(parameters.ExitCodePath, process.ExitCodePath))
            return Rejected("enrollment-invalid", "host launch contract does not bind this store/execution root/dispatch; operator decision required");
        return null;
    }

    private static bool SamePath(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return false; }
    }

    internal WorkerBuildInvocation Reconcile(WorkerBuildInvocation invocation, bool cancelled = false)
    {
        if (IsExactProcessAlive(invocation.Host)) return invocation;
        foreach (var child in invocation.Children ?? [])
            if (IsExactProcessAlive(child))
                throw new InvalidOperationException("Worker build host exited but owned child termination is unconfirmed; custody retained.");
        if (!invocation.IsTerminal)
            invocation = Store(invocation with { Version = invocation.Version + 1,
                Observations = invocation.Observations.Append(new(WorkerBuildPhase.Completed, DateTimeOffset.UtcNow)).ToArray(),
                Outcome = cancelled ? WorkerBuildOutcome.Cancelled : WorkerBuildOutcome.Interrupted,
                Diagnostic = cancelled ? "Host-owned cancellation confirmed child termination." :
                    "Host exited before durable terminal observation; no relaunch permitted." }, invocation.Version);
        if (!invocation.CustodyReleased)
        {
            var goal = Load(invocation.GoalId).GetGoal(new GoalId(invocation.GoalId));
            if (GoalEvidenceOperationOwner.TryParse(invocation.LeaseOwner, out var owner) && owner is not null)
            {
                var entries = GoalOperationJournal.ReadStrict(executionDirectory, goal.Id).Entries
                    .Where(e => e.OperationInstanceId == owner.InstanceId && e.Operation == owner.Operation).ToArray();
                var store = new ReconcileSweepRemediationStore(statePath);
                if (entries.Length == 0 && store.TryGetAcceptanceLeaseOwner(invocation.GoalId) == invocation.LeaseOwner)
                    throw new InvalidOperationException("Worker build custody lacks its matching operation journal; retained for recovery.");
                if (entries.Length > 0 && !entries.Any(e =>
                    e.Status is GoalOperationStatus.Completed or GoalOperationStatus.Failed or GoalOperationStatus.Aborted))
                    GoalOperationJournal.Aborted(executionDirectory, goal, owner.Operation,
                        "Worker build host exited; contained owned children confirmed terminal.", operationInstanceId: owner.InstanceId);
                store.ReleaseAcceptanceLease(invocation.GoalId, invocation.LeaseOwner!);
            }
            invocation = Store(invocation with { Version = invocation.Version + 1, CustodyReleased = true }, invocation.Version);
        }
        return invocation;
    }

    internal static bool IsExactProcessAlive(WorkerBuildProcess identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.Pid);
            if (process.HasExited) return false;
            return WorkerBuildRequestChannel.ReadIdentity(process) == identity;
        }
        catch (ArgumentException) { return false; }
        // Access/identity failures remain unknown, never proof of termination.
    }
}
