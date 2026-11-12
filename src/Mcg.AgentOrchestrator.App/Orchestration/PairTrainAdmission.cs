namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class PairTrainAdmission
{
    internal static bool CanAdmitTrain(bool groupedAdmissionOpen, bool admissionSuppressed, bool capacityAdmitted) =>
        groupedAdmissionOpen && !admissionSuppressed && capacityAdmitted;

    // Retain the cohort admission body until recorded cohort work has drained and the removal slice lands.
    internal static bool CanAdmitNewCohort() => false;
}
