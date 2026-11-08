namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record ExecutorSlotFailureRates(int Attempts, int LaneTimeout, int LeaseExpired, int TrxIncomplete)
{
    public double? LaneTimeoutRate => Rate(LaneTimeout);
    public double? LeaseExpiredRate => Rate(LeaseExpired);
    public double? TrxIncompleteRate => Rate(TrxIncomplete);
    private double? Rate(int failures) => Attempts == 0 ? null : (double)failures / Attempts;
    internal bool NoWorseThan(ExecutorSlotFailureRates baseline) => Attempts > 0 && baseline.Attempts > 0 &&
        LaneTimeoutRate <= baseline.LaneTimeoutRate && LeaseExpiredRate <= baseline.LeaseExpiredRate &&
        TrxIncompleteRate <= baseline.TrxIncompleteRate;
}
