namespace Mcg.AgentOrchestrator.Core;

public sealed class TaskBriefBudgetHeadroom
{
    private const int NearLimitThresholdPercent = 90;

    public TaskBriefBudgetHeadroom(int budget, int measuredCharacterCount)
    {
        Headroom = (long)budget - measuredCharacterCount;
        UsedPercentage = budget > 0 ? (decimal)measuredCharacterCount * 100 / budget : 0;
        IsNearLimit = measuredCharacterCount > budget
            || (long)measuredCharacterCount * 100 >= (long)budget * NearLimitThresholdPercent;
    }

    public long Headroom { get; }
    public decimal UsedPercentage { get; }
    public bool IsNearLimit { get; }
}
