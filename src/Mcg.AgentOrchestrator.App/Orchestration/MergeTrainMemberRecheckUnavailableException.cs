namespace Mcg.AgentOrchestrator.App.Orchestration;

// Carries a cause across the nullable runner contract without changing its verdict.
internal sealed class MergeTrainMemberRecheckUnavailableException(
    MergeTrainMemberRecheckUnavailable unavailable) : Exception(unavailable.Code)
{
    internal MergeTrainMemberRecheckUnavailable Unavailable { get; } = unavailable;
}
