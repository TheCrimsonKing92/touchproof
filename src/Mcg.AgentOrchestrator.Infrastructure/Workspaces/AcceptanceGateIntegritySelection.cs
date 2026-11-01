namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceGateIntegritySelection
{
    internal static bool NeedsRelabel(IntegrityLabelState state, bool directory)
    {
        if (!state.Exists || state.NativeQueryError is not null || state.Low)
            return true;

        // Every parent is prepared before its children, but is still traversed
        // when already Medium: explicit Low labels can exist at any depth.
        // A successful read with no file label means implicit Medium. Directories
        // also need transitive Medium inheritance for outputs created by checks.
        return directory ? !state.Medium || !state.Inheritable
            : !state.Medium && !state.Unlabeled;
    }
}
