namespace Mcg.AgentOrchestrator.Core;

// The host-bound directive that established execution ownership. Source review
// stays on the worker verification record, including for compound criteria.
public sealed record PlannerCriterionEvidenceSource(
    PlannerEvidenceIdentity Identity, PlannerEvidencePayload Directive,
    bool EstablishesOwnership = true);
