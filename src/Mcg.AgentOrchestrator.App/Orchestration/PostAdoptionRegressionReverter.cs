using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class PostAdoptionRegressionReverter
{
    internal enum HandleStatus { NotRegression, Suppressed, Reverted, Held, NotApplicable }

    internal sealed record IncidentEvent(
        string Landing,
        string Trigger,
        ActivationFailedMainRevert.Outcome Outcome,
        string? RevertCommit,
        string? Reason,
        IReadOnlyList<PostAdoptionRegressionEvaluator.AffectedGoal> AffectedGoals,
        bool GateStarved,
        bool GateSlotFree,
        int VerifiedGoalCount);

    private readonly string? _root;
    private readonly string? _branch;
    private readonly Action<string> _appendConductEvent;
    private readonly Func<string?, string?, string?, ActivationFailedMainRevert.Result> _revert;
    private int _incidentClaimed;

    // The caller keeps one instance for the entire incident, even across different landings.
    internal PostAdoptionRegressionReverter(
        string? root,
        string? branch,
        Action<string> appendConductEvent,
        Func<string?, string?, string?, ActivationFailedMainRevert.Result>? revert = null)
    {
        _root = root;
        _branch = branch;
        _appendConductEvent = appendConductEvent;
        _revert = revert ?? ActivationFailedMainRevert.Revert;
    }

    internal (HandleStatus Status, IncidentEvent? Event) Handle(PostAdoptionRegressionEvaluator.Verdict verdict)
    {
        if (!verdict.IsRegression)
        {
            return (HandleStatus.NotRegression, null);
        }

        if (Interlocked.CompareExchange(ref _incidentClaimed, 1, 0) != 0)
        {
            return (HandleStatus.Suppressed, null);
        }

        var result = _revert(_root, _branch, verdict.AdoptedLandingCommit);
        var landing = JsonEncodedText.Encode(verdict.AdoptedLandingCommit);
        var (status, header) = result.Status switch
        {
            ActivationFailedMainRevert.Outcome.Reverted => (HandleStatus.Reverted,
                $"POST_ADOPTION_REGRESSION_REVERTED landing={landing} revertCommit={result.RevertCommit}"),
            ActivationFailedMainRevert.Outcome.Held => (HandleStatus.Held,
                $"POST_ADOPTION_REGRESSION_REVERT_HELD landing={landing} reason={JsonSerializer.Serialize(result.Reason)}"),
            ActivationFailedMainRevert.Outcome.NotApplicable => (HandleStatus.NotApplicable,
                $"POST_ADOPTION_REGRESSION_REVERT_NOT_APPLICABLE landing={landing}"),
            _ => throw new ArgumentOutOfRangeException(nameof(result), result.Status, "Unknown revert outcome")
        };

        var incident = new IncidentEvent(
            verdict.AdoptedLandingCommit, verdict.Trigger, result.Status, result.RevertCommit, result.Reason,
            verdict.AffectedGoals, verdict.GateStarved, verdict.GateSlotFree, verdict.VerifiedGoalCount);
        _appendConductEvent(
            $"{header} trigger={incident.Trigger} sharedReason={JsonSerializer.Serialize(verdict.SharedReason)} " +
            $"gateStarved={incident.GateStarved} gateSlotFree={incident.GateSlotFree} " +
            $"verifiedGoals={incident.VerifiedGoalCount} goals={JsonSerializer.Serialize(incident.AffectedGoals)}");
        return (status, incident);
    }
}
