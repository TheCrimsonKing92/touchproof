using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

// Pure candidate inventory and dispatch observations; no processes, files, clocks or shared state.
internal static class AcceptanceSelectedExecutionFixture
{
    internal static readonly string[] Changes = ["src/Mcg.AgentOrchestrator.Infrastructure/Example.cs"];
    internal static readonly AcceptanceTestClassDescriptor[] Classes =
        [new("Candidate.A", null), new("Candidate.B", null), new("Candidate.Guard", null),
         new("Candidate.Floor", null), new("Candidate.X", null)];
    internal static readonly AcceptanceTestLane[] Lanes =
    [
        new("Balanced 1", "(FullyQualifiedName=Candidate.A|FullyQualifiedName=Candidate.B|FullyQualifiedName=Candidate.Guard)&Category!=External&FullyQualifiedName!=Candidate.Excluded"),
        new("Balanced 2", "FullyQualifiedName=Candidate.X"),
        new("Chaos gate", "FullyQualifiedName=Candidate.Floor"),
        new("Residual", string.Join('&', Classes.Select(item => "FullyQualifiedName!=" + item.FullName)))
    ];

    internal static AcceptanceSelectedExecutionSession Session(AcceptanceSelectedExecutionMode mode,
        ReverseDependencyTestSelection? selection = null, IReadOnlyList<string>? changes = null) =>
        new(mode, "in-memory-candidate", changes ?? Changes, read: (_, _) =>
            new(selection ?? ReverseDependencyTestSelection.Resolved(["A"]), Classes, ["Candidate.Guard"]));

    internal static AcceptanceGateLanePlan Observe(AcceptanceSelectedExecutionSession session,
        IReadOnlyList<AcceptanceTestLane>? baseline = null)
    {
        var candidates = baseline ?? Lanes;
        var selected = session.LaneTransform?.Invoke(candidates) ?? candidates;
        var policy = PolicyShardPlan.NotApplicable("in-memory candidate");
        var checks = BuildChecks(selected);
        return AcceptanceGateLanePlanPublisher.Classify("attempt", candidates, selected, [], [], policy,
            DotnetShardDisposition.RunDotnetShards, [], checks, checks, checks, selectedExecution: session);
    }

    internal static IReadOnlyList<Check> BuildChecks(IReadOnlyList<AcceptanceTestLane> lanes) =>
        GoalAcceptanceVerifier.BuildPolicyEffectiveChecks("in-memory candidate", [], Changes,
            [new Check { Name = "infrastructure tests", Type = "dotnet-test", Project = InfrastructureTestsProject }],
            PolicyShardPlan.NotApplicable("in-memory candidate"), lanes);
}
