using Mcg.AgentOrchestrator.Infrastructure;

internal static class ExecutorSlotTestData
{
    internal static readonly DateTimeOffset Now = new(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
    internal static readonly ExecutorSlotControllerSettings Settings = new(
        MinimumSoloSamples: 2, MinimumSamplesPerK: 2, JobsPerWindow: 4, StableWindows: 1, TrialJobs: 4);

    internal static ExecutorSlotSample[] Jobs(int k = 1, double seconds = 10, int count = 4,
        string lane = "lane", string prefix = "base", DateTimeOffset? end = null, string outcome = "accepted") =>
        Enumerable.Range(0, count).Select(i => new ExecutorSlotSample(prefix + i, lane,
            (end ?? Now.AddMinutes(-1)).AddSeconds(i), (end ?? Now.AddMinutes(-1)).AddSeconds(i - 10),
            k, outcome, seconds)).ToArray();

    internal static ExecutorSlotDecisionResult StartTrial() => ExecutorSlotDecision.Decide(Jobs(), Settings, null, 1, false, Now);

    internal static ExecutorSlotSample[] CompletedTrial(double seconds = 12, string? failure = null)
    {
        var trial = Jobs(2, seconds, prefix: "trial", end: Now.AddMinutes(1));
        if (failure is not null) trial[0] = trial[0] with { Outcome = failure, RemoteSeconds = null };
        return Jobs().Concat(trial).ToArray();
    }
}
