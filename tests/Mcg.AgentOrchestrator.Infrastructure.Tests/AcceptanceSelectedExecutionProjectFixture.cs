using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

// Pure inputs, or a unique filesystem root owned and removed by RepositoryFixture.
internal static class AcceptanceSelectedExecutionProjectFixture
{
    internal static readonly Check[] Checks =
    [
        new() { Name = "cli tests", Type = "dotnet-test", Project = CliTestsProject,
            Runner = "mtp", Arguments = ["--verbosity", "minimal"] },
        new() { Name = "owner console tests", Type = "dotnet-test", Project = OwnerConsoleTestsProject,
            Runner = "mtp", Arguments = ["--verbosity", "minimal"] }
    ];
    internal static readonly AcceptanceWholeProjectCheck[] Scheduled = Checks.Select(check =>
        new AcceptanceWholeProjectCheck(check.Name, check.Project!)).ToArray();
    internal static readonly AcceptanceWholeProjectCheckInventory[] Inventories =
    [
        new("cli tests", CliTestsProject, [new("Candidate.CliReached", null), new("Candidate.CliGuard", null), new("Candidate.CliOther", null)]),
        new("owner console tests", OwnerConsoleTestsProject, [new("Candidate.ConsoleReached", null), new("Candidate.ConsoleGuard", null), new("Candidate.ConsoleOther", null)])
    ];

    internal static AcceptanceSelectedExecutionSession Session(AcceptanceSelectedExecutionMode mode = AcceptanceSelectedExecutionMode.Enforce,
        IReadOnlyList<string>? reached = null, IReadOnlyList<string>? guards = null, IReadOnlyList<string>? obligations = null,
        Func<string, IReadOnlyList<AcceptanceTestClassDescriptor>>? projectClasses = null) =>
        new(mode, "in-memory candidate", AcceptanceSelectedExecutionFixture.Changes, read: (_, _) =>
            new(ReverseDependencyTestSelection.Resolved(reached ?? ["A"]), AcceptanceSelectedExecutionFixture.Classes,
                guards ?? ["Candidate.Guard"], obligations,
                projectClasses ?? (project => Inventories.Single(item => item.Project == project).Classes)));

    internal static AcceptanceGateLanePlan Observe(AcceptanceSelectedExecutionSession session,
        IReadOnlyList<Check>? checks = null)
    {
        var selected = session.GateTransform?.Invoke(AcceptanceSelectedExecutionFixture.Lanes, Scheduled)
            ?? AcceptanceSelectedExecutionFixture.Lanes;
        var executed = session.ProjectCheckTransform?.Invoke(checks ?? Checks) ?? checks ?? Checks;
        var all = AcceptanceSelectedExecutionFixture.BuildChecks(selected).Concat(executed).ToArray();
        return AcceptanceGateLanePlanPublisher.Classify("attempt", AcceptanceSelectedExecutionFixture.Lanes,
            selected, Checks, [], PolicyShardPlan.NotApplicable("candidate"), DotnetShardDisposition.RunDotnetShards,
            [], all, all, all, selectedExecution: session);
    }

    internal static GoalAcceptanceVerifier.EffectiveGatePlan EffectivePlan(string root, AcceptanceSelectedExecutionSession session,
        IReadOnlyList<string>? changes = null) => GoalAcceptanceVerifier.CreateEffectiveGatePlan(root,
            changes ?? AcceptanceSelectedExecutionFixture.Changes, AcceptanceGateEngineSettings.Parse("{}"),
            infrastructureLaneExpansion: new(AcceptanceSelectedExecutionFixture.Lanes, null),
            laneTransform: session.GateTransform, projectCheckTransform: session.ProjectCheckTransform);

    internal static RepositoryTestReachTests.RepositoryFixture Repository()
    {
        var fixture = new RepositoryTestReachTests.RepositoryFixture();
        foreach (var check in Checks)
            fixture.Write(check.Project!, """
                <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
                <ProjectReference Include="../../../src/Mcg.AgentOrchestrator.Infrastructure/Mcg.AgentOrchestrator.Infrastructure.csproj" />
                </ItemGroup></Project>
                """);
        fixture.Write("config/acceptance-manifest.json", JsonSerializer.Serialize(new
        {
            version = 1, checks = Checks,
            engine = new { laneTestSourceRoots = new[] { "tests/Mcg.AgentOrchestrator.Infrastructure.Tests" } }
        }));
        return fixture;
    }
}
