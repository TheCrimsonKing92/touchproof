namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum ConductorMergeTrainSkipReason
{
    IneligibleCriterionEvidence,
    CohortAttributedMember,
    TrainImplicatedMember,
    ProjectionGoalMismatch,
    NoLandingPaths,
    NoResourceKeys,
    DocsTreeOnly,
    OwnerProtectedPath,
    MainRevisionMismatch,
    OverlapsSelectedMember,
    SuppressedPair
}
