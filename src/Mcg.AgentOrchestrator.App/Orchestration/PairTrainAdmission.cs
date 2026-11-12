namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class PairTrainAdmission
{
    internal static bool CanAdmitTrain(bool groupedAdmissionOpen, bool admissionSuppressed, bool capacityAdmitted) =>
        groupedAdmissionOpen && !admissionSuppressed && capacityAdmitted;
}
