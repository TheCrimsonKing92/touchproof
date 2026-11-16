namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns drain outcomes and boundary eligibility; the loop owns persistence and handoff.
internal static class ConductorRelaunchDrainIntentBoundary
{
    internal static bool RunsAtTickStart(bool selfRelaunchPending) => !selfRelaunchPending;

    internal static bool RunsAfterDrainStep(
        bool ranAtTickStart, SelfRelaunchDrainOutcome? outcome, bool selfRelaunchStillPending) =>
        !ranAtTickStart && selfRelaunchStillPending && outcome is
            { Disposition: SelfRelaunchDrainDisposition.ContinueTick, HeldForRunningDispatches: true };

    internal enum SelfRelaunchDrainDisposition
    {
        Proceed,
        ContinueTick,
        BreakLoop
    }

    internal sealed record SelfRelaunchDrainOutcome(
        SelfRelaunchDrainDisposition Disposition,
        Exception? Fault = null,
        bool HeldForRunningDispatches = false)
    {
        internal void ThrowIfFaulted()
        {
            if (Fault is not null) throw Fault;
        }
    }
}
