namespace Mcg.AgentOrchestrator.Core;

/// <summary>A per-discovery snapshot; identities and schema metadata are not inferred facts.
/// RepositoryRoot labels the logical root ("."), rather than persisting the host's absolute path.</summary>
public sealed record ProjectModel(
    int SchemaVersion,
    string RepositoryRoot,
    IReadOnlyList<ProjectUnit> Units,
    IReadOnlyList<UnitDependency> Dependencies,
    IReadOnlyList<UnitTestSetup> TestSetups,
    IReadOnlyList<ProjectOwnerQuestion> OwnerQuestions,
    IReadOnlyList<UnitCommands> Commands,
    IReadOnlyList<EnvironmentNeed> EnvironmentNeeds,
    IReadOnlyList<UnitMeasurement> Measurements,
    IReadOnlyList<SharedStateHazard> Hazards,
    ProjectFact<string>? IntegrationBranch = null,
    ProjectStack Stack = ProjectStack.Dotnet,
    DeliveryProbeRecord? DeliveryProbe = null,
    IReadOnlyList<ProjectRiskArea>? RiskAreas = null,
    IReadOnlyList<ProjectConvention>? Conventions = null,
    IReadOnlyList<ProjectVerificationKind>? Verifications = null)
{
    public const int CurrentSchemaVersion = 5;

    public IReadOnlyList<ProjectRiskArea> RiskAreas { get; init; } = RiskAreas ?? [];
    public IReadOnlyList<ProjectConvention> Conventions { get; init; } = Conventions ?? [];
    public IReadOnlyList<ProjectVerificationKind> Verifications { get; init; } = Verifications ?? [];
}
