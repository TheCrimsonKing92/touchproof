using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

/// <summary>One query-only attention invocation, including the explicit legacy query capability.</summary>
internal sealed record AttentionJsonReadContext(
    IReadOnlyList<HumanInputRequest> Waits, IReadOnlyList<CollaborationItem> Items,
    IReadOnlyList<ProviderUsageHoldRow> ProviderHolds, IReadOnlyList<AttentionGateEvidence> GateEvidence,
    IReadOnlyList<OwnerQuestionHoldBlocker> OwnerQuestions,
    IReadOnlyList<AttentionPostLandingDuty>? PostLandingDuties = null,
    IReadOnlyCollection<Goal>? Goals = null,
    IReadOnlyList<PlannerBriefAmendmentRevisionPendingHold>? AmendmentHolds = null)
{
    internal static async Task<AttentionJsonReadContext> ReadAsync(IOrchestratorStateQueries queries,
        OrchestratorWorkspace workspace, DateTimeOffset now, CancellationToken cancellationToken = default)
        => await ReadAsync(queries, workspace, now, new AttentionProjectionReadOptions(), cancellationToken);

    internal static async Task<AttentionJsonReadContext> ReadAsync(IOrchestratorStateQueries queries,
        OrchestratorWorkspace workspace, DateTimeOffset now, AttentionProjectionReadOptions options,
        CancellationToken cancellationToken)
        => await ReadCoreAsync(queries, workspace, now, options, null, cancellationToken);

    // Fleet requires positive proof that both authoritative stores exist and are supported.
    // Keep the single-workspace query's legacy missing-store behavior unchanged.
    internal static async Task<AttentionJsonReadContext> ReadExistingAsync(OrchestratorWorkspace workspace,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var itemsPath = Path.Combine(workspace.OrchestratorDirectory, "collaboration-items.db");
        RequireExisting(workspace.SqliteStatePath);
        RequireExisting(itemsPath);
        try { SqliteOrchestratorStateRepository.ValidateReadOnlySchema(workspace.SqliteStatePath); }
        catch (InvalidOperationException ex) { throw new FleetAttentionStoreException("unsupported-store", ex.Message, ex); }
        cancellationToken.ThrowIfCancellationRequested();
        CollaborationItemStore store;
        try { store = CollaborationItemStore.OpenReadOnly(itemsPath); }
        catch (InvalidOperationException ex)
        { throw new FleetAttentionStoreException("unsupported-store", StoreSchemaDiagnostics.Message(ex), ex); }
        var items = await store.GetAttentionQueueAsync(cancellationToken: cancellationToken);
        return await ReadCoreAsync(SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath),
            workspace, now, new AttentionProjectionReadOptions(false), items, cancellationToken);
    }

    private static void RequireExisting(string path)
    {
        try { _ = File.GetAttributes(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        { throw new FleetAttentionStoreException("missing-store", $"Required store was not found: {path}", ex); }
    }

    private static async Task<AttentionJsonReadContext> ReadCoreAsync(IOrchestratorStateQueries queries,
        OrchestratorWorkspace workspace, DateTimeOffset now, AttentionProjectionReadOptions options,
        IReadOnlyList<CollaborationItem>? existingItems, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var projection = await queries.ReadAttentionProjectionAsync(options, cancellationToken);
        var itemsDb = Path.Combine(workspace.OrchestratorDirectory, "collaboration-items.db");
        IReadOnlyList<CollaborationItem> items = existingItems ?? (File.Exists(itemsDb)
            ? await CollaborationItemStore.OpenExisting(workspace.OrchestratorDirectory)
                .GetAttentionQueueAsync(cancellationToken: cancellationToken) : []);

        if (projection is null)
        {
            // Unsupported is a declared capability value. Do not catch IO, corruption,
            // cancellation or arbitrary NotSupportedException from implemented operations.
            var metadata = await queries.ListGoalMetadataAsync(cancellationToken);
            var fallbackIds = metadata.Select(goal => new GoalId(goal.Id)).ToArray();
            var baseline = fallbackIds.Length == 0 ? new AgentOrchestratorKernel()
                : await queries.LoadGoalsAsync(fallbackIds, cancellationToken);
            var fallback = FromKernel(baseline, items, now, options);
            return existingItems is null ? fallback : fallback with
            {
                OwnerQuestions = fallback.OwnerQuestions.Where(hold =>
                    AttentionHumanWaitQuery.IsLiveAttentionGoal(baseline, hold.GoalId)).ToArray()
            };
        }

        var facts = projection.Goals.ToDictionary(goal => goal.GoalId, StringComparer.OrdinalIgnoreCase);
        var openWaits = await queries.ListOpenHumanInputRequestsAsync(cancellationToken);
        var currentWaits = openWaits.Where(wait => facts.TryGetValue(wait.GoalId, out var goal) &&
            (wait.PlannerIdentity is null || wait.PlannerIdentity.BriefVersion == goal.BriefVersion)).ToArray();
        // The shared selection's parked decline belongs to the text writer fallback. Excluding
        // parked metadata here applies JSON's existing live filter without ever declining.
        var metadataForSelection = projection.Goals.Where(goal => goal.Status != GoalStatus.Parked)
            .Select(goal => new GoalSummary(goal.GoalId, goal.Status.ToString(), "", "")).ToArray();
        var selection = AttentionReadGoalSelection.Select(metadataForSelection, currentWaits, [], items);
        // Kernel restore can repair a missing persisted request from a WaitingForHuman task.
        // Such owners are potential actionable evidence even with no request-table row.
        var ids = selection.GoalIds.Concat(projection.Goals.Where(goal => goal.HasWaitingTasks &&
                goal.Status is not (GoalStatus.Parked or GoalStatus.Completed or GoalStatus.Failed or
                    GoalStatus.Cancelled or GoalStatus.Superseded))
            .Select(goal => new GoalId(goal.GoalId))).Distinct().OrderBy(id => id.Value, StringComparer.Ordinal).ToArray();
        var kernel = ids.Length == 0 ? new AgentOrchestratorKernel()
            : await queries.LoadGoalsAsync(ids, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var result = new AttentionJsonReadContext(CliCommandHandlers.HumanWaitsForAttention(kernel, null, includeHistory: false),
            CliCommandHandlers.CollaborationItemsForAttention(items, kernel, null, includeHistory: false),
            ProviderUsageHoldView.ReadEvidence(projection.ProviderEvidence, now, kernel.Goals),
            options.IncludeGateEvidence ? projection.GateEvidence : [],
            projection.OwnerQuestions.Where(hold => existingItems is null ||
                facts.TryGetValue(hold.GoalId, out var owner) && owner.Status is not
                    (GoalStatus.Parked or GoalStatus.Failed or GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Superseded))
                .Select(ReadOwnerQuestion).ToArray(), projection.PostLandingDuties, kernel.Goals,
            PlannerBriefAmendmentHoldAttentionView.Holds(kernel, kernel.Goals));
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static AttentionJsonReadContext FromKernel(AgentOrchestratorKernel kernel,
        IReadOnlyList<CollaborationItem> items, DateTimeOffset now, AttentionProjectionReadOptions options) => new(
        CliCommandHandlers.HumanWaitsForAttention(kernel, null, includeHistory: false),
        CliCommandHandlers.CollaborationItemsForAttention(items, kernel, null, includeHistory: false),
        ProviderUsageHoldView.Read(kernel.Goals, now),
        options.IncludeGateEvidence ? kernel.Goals.SelectMany(goal => goal.GateTestObligations
            .Where(item => item.BriefVersion == goal.AuthoritativeBrief.Version)
            .Select(item => new AttentionGateEvidence(goal.Id.Value, item))).ToArray() : [],
        kernel.Goals.Where(goal => goal.Status is not (GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Superseded))
            .Select(OwnerQuestionHoldBlocker.TryRead).OfType<OwnerQuestionHoldBlocker>().ToArray(),
        kernel.Goals.SelectMany(goal => goal.OutstandingPostLandingDuties
            .Select(item => new AttentionPostLandingDuty(goal.Id.Value, item))).ToArray(), kernel.Goals,
        PlannerBriefAmendmentHoldAttentionView.Holds(kernel, kernel.Goals));

    private static OwnerQuestionHoldBlocker ReadOwnerQuestion(AttentionOwnerQuestionEvidence evidence)
    {
        // Only a displayed hold gets this sparse formatting adapter: ordered IDs and the hold,
        // with no historical task graphs, outputs, histories or timeline. Reuse the canonical
        // formatter, including malformed-marker fallback and original task-number semantics.
        var tasks = evidence.TaskIds.Select(id => new TaskSnapshot(id, "Owner question input",
            AgentRole.Developer, WorkTaskStatus.Pending, null, null, null, null, null, null)).ToArray();
        var adapter = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [new GoalSnapshot(evidence.GoalId, "Owner question input", GoalStatus.Active, tasks, [],
                CurrentHold: evidence.Hold)], []));
        return OwnerQuestionHoldBlocker.TryRead(adapter.Goals.Single())!;
    }
}
