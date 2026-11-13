namespace Mcg.AgentOrchestrator.App.Orchestration;

// Launch refusals have no attempt identity; both driver paths retry through their normal next tick.
internal static class FocusedEvidenceLaunchRefusal
{
    internal static string Describe(Exception exception, string subject) => exception switch
    {
        AcceptanceArtifactWriterLeaseBusyException =>
            $"Background {subject} artifact writer is busy; retry on next conduct tick. {exception.Message}",
        PreReviewEvidenceBuildSlotsBusyException =>
            $"Background {subject} is waiting for build capacity; retry on next conduct tick. {exception.Message}",
        _ => throw new ArgumentException("Unsupported focused-evidence launch refusal.", nameof(exception))
    };
}
