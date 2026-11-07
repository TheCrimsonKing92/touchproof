using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class SourceSizeRatchetObligationOracle
{
    public static RetryCorrectionConditionVerdict Evaluate(string worktreePath)
    {
        var result = SourceSizeRatchetPreflight.Evaluate(worktreePath);
        if (result.HasBlockingViolation) return RetryCorrectionConditionVerdict.Failed;
        return result.AuthorityAvailable && result.EvaluationComplete
            ? RetryCorrectionConditionVerdict.Passed
            : RetryCorrectionConditionVerdict.Unavailable;
    }
}
