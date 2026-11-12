namespace Mcg.AgentOrchestrator.App.Orchestration;

// The grouped attempt id survives member ejection and receipt/composition changes.
internal sealed record ActivationAdmissionPreparingOwner(string Kind, string Id)
{
    internal string Token => $"{Kind}:{Id}";
}
