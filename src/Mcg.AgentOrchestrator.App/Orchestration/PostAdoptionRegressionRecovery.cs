using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using HandleStatus = Mcg.AgentOrchestrator.App.Orchestration.PostAdoptionRegressionReverter.HandleStatus;
using IncidentEvent = Mcg.AgentOrchestrator.App.Orchestration.PostAdoptionRegressionReverter.IncidentEvent;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// One instance shares the lifetime of the reverter's incident claim.
internal sealed class PostAdoptionRegressionRecovery(
    string? root, string? branch, string orchestratorDirectory, Action<string> appendConductEvent,
    Func<HomeBuildAdoptionRecord?> resolveTarget, Func<ConductorSelfRelaunchRequest, bool> requestRelaunch,
    Func<string, string, string, bool>? push = null, Action<string?, string, string>? raiseOperatorHold = null,
    Func<string, string, BacklogItem>? addBacklogItem = null)
{
    private int _incidentClaimed;

    // The sink reserves the standard handoff. Its coordinator revalidates the target and blockers and waits for canaries.
    internal PostAdoptionRegressionRecovery(string? root, string? branch, string orchestratorDirectory,
        Action<string> appendConductEvent, HomeBuildAdoptionDetector detector,
        Func<ConductorSelfRelaunchRequest, bool> requestRelaunch)
        : this(root, branch, orchestratorDirectory, appendConductEvent, detector.Detect, requestRelaunch,
            raiseOperatorHold: (goalId, revertCommit, detail) => CollaborationItemStore.ForDirectory(orchestratorDirectory)
                .RaiseAsync(CollaborationItemType.Decision, goalId, "Post-adoption regression relaunch held", detail,
                    $"post-adoption-regression-relaunch-held:{revertCommit}").GetAwaiter().GetResult())
    {
    }

    internal void Recover(HandleStatus status, IncidentEvent? incident, int tick, string? goalId, bool gateRunning)
    {
        if (status != HandleStatus.Reverted ||
            incident is not { Outcome: ActivationFailedMainRevert.Outcome.Reverted, RevertCommit: { } revertCommit } ||
            string.IsNullOrWhiteSpace(revertCommit) ||
            Interlocked.CompareExchange(ref _incidentClaimed, 1, 0) != 0)
            return;

        var context = $"landing={incident.Landing} trigger={incident.Trigger} goal={goalId ?? "unknown"} tick={tick}";
        if (gateRunning)
        {
            Report($"RECOVERY_HELD reason=gate-running {context} revertCommit={revertCommit}");
            return;
        }

        var pushResult = RevertPushPolicyGate.Push(orchestratorDirectory, root, revertCommit, branch, appendConductEvent, push);
        var reason = pushResult.Reason is null ? "" : $" reason={pushResult.Reason}";
        AdoptedBuildPublicationReporting.Try(() => appendConductEvent(
            $"REVERT_PUSH_{pushResult.Status.ToString().ToUpperInvariant()} source=post-adoption-regression " +
            $"commit={revertCommit} branch={branch}{reason} {context}"), "revert-push-event");
        FileReland(incident, revertCommit, pushResult, context);
        Relaunch(revertCommit, context, tick, goalId);
    }

    private void FileReland(IncidentEvent incident, string revertCommit, RevertPushPolicyGate.Result pushResult,
        string context)
    {
        BacklogItem item;
        try
        {
            item = PostAdoptionRegressionBacklogFiler.File(orchestratorDirectory, incident, revertCommit, pushResult,
                addBacklogItem);
        }
        catch (Exception ex)
        {
            Report($"BACKLOG_FILING_FAILED reason={ex.GetType().Name} {context} revertCommit={revertCommit}");
            return;
        }

        Report($"BACKLOG_FILED item={item.Id} {context} revertCommit={revertCommit}");
    }

    private void Relaunch(string revertCommit, string context, int tick, string? goalId)
    {
        var attribution = $"{context} revertCommit={revertCommit}";
        var target = resolveTarget();
        if (target is null)
        {
            var detail = $"classification=OPERATOR_HOLD reason=no-restored-build-target detail=\"relaunch held: no restored-build target\" {attribution}";
            Report($"RELAUNCH_HELD {detail}");
            if (raiseOperatorHold is not null)
                AdoptedBuildPublicationReporting.Try(() => raiseOperatorHold(goalId, revertCommit, detail), "regression-relaunch-attention");
            return;
        }

        if (string.IsNullOrWhiteSpace(goalId))
        {
            Report($"RELAUNCH_HELD reason=no-incident-goal {attribution}");
            return;
        }

        bool accepted;
        try
        {
            accepted = requestRelaunch(new ConductorSelfRelaunchRequest(goalId, tick, target));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Report($"RELAUNCH_REFUSED reason={ex.GetType().Name} {attribution}");
            return;
        }

        Report(accepted
            ? $"RELAUNCH_REQUESTED {attribution}"
            : $"RELAUNCH_REFUSED reason=request-not-accepted {attribution}");
    }

    private void Report(string detail) => AdoptedBuildPublicationReporting.Try(
        () => appendConductEvent($"POST_ADOPTION_REGRESSION_{detail}"), "regression-recovery-event");
}
