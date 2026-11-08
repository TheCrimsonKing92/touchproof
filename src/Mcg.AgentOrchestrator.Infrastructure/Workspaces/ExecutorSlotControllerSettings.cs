namespace Mcg.AgentOrchestrator.Infrastructure;

// Evidence policy, never executor capacity or dispatch policy.
internal sealed record ExecutorSlotControllerSettings(
    int LookbackHours = 24, int MinimumSoloSamples = 5, int MinimumSamplesPerK = 5,
    int JobsPerWindow = 20, int StableWindows = 3, int TrialJobs = 20,
    double StabilityTolerance = 0.05)
{
    internal void Validate()
    {
        if (LookbackHours <= 0 || MinimumSoloSamples <= 0 || MinimumSamplesPerK <= 0 ||
            JobsPerWindow <= 0 || StableWindows <= 0 || TrialJobs <= 0 ||
            !double.IsFinite(StabilityTolerance) || StabilityTolerance is < 0 or > 1)
            throw new InvalidOperationException("invalid-executor-slot-controller-settings");
    }
}
