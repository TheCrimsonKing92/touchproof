using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: composition is pure, selectors ignore ambient environment, and all artifacts are fixture-owned.
public sealed class ConductorContinuityChildArgumentAcceptanceTests
{
    public static IEnumerable<object?[]> RestartCases()
    {
        string[][] inputs =
        [
            ["conduct", "--loop"],
            ["conduct", "--loop", "--watch", "--poll-seconds", "30"],
            ["conduct", "--loop", "--continuity-child"],
            ["conduct", "--loop", "--home-build-blocked-commit=old"]
        ];
        foreach (var input in inputs)
        foreach (var commit in new[] { null, new string('f', 40) })
        foreach (var requireCurrentStores in new[] { false, true })
            yield return [input, commit, requireCurrentStores];
    }

    [Xunit.Theory]
    [Xunit.MemberData(nameof(RestartCases))]
    public void ComposedRestart_AllBranchesPassConductValidation(string[] input,
        string? blockedCommit, bool requireCurrentStores)
    {
        var artifactPath = Path.Combine(Path.GetTempPath(), "continuity child", "exit.json");
        var arguments = ConductorContinuitySupervisor.ComposeChildArguments(input, "project", "tenant",
            artifactPath, blockedCommit, requireCurrentStores);

        Assert.Equal(1, arguments.Count(arg => arg == ConductorContinuitySupervisor.ChildFlag));
        var artifactIndex = Array.IndexOf(arguments, ConductorContinuitySupervisor.ExitArtifactFlag);
        Assert.True(artifactIndex >= 0);
        Assert.Equal(artifactPath, arguments[artifactIndex + 1]);
        Assert.Equal(requireCurrentStores, arguments.Contains("--require-current-stores"));
        Assert.Equal(blockedCommit is null ? Array.Empty<string>() : [$"--home-build-blocked-commit={blockedCommit}"],
            arguments.Where(arg => arg.StartsWith("--home-build-blocked-commit=", StringComparison.Ordinal)));

        AssertAcceptedByConduct(arguments, requireCurrentStores);
    }

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task FailedFollowerUpgrade_RetainedChildArgumentsPassConductValidation()
    {
        using var fixture = new HomeBuildAdoptionFixture();
        var stateDirectory = Path.Combine(fixture.Root, "project-state");
        var database = Path.Combine(stateDirectory, "fixture.db");
        var planCalls = 0;
        var upgrade = new ConductorHandoffSchemaUpgrade(stateDirectory, fixture.Root,
            run: (_, arguments, _) =>
            {
                Assert.Contains("--plan", arguments);
                planCalls++;
                return new(0, $"fixture: recorded 1; required 2; action migrate ({database})", "");
            });
        var requests = new List<ConductorSupervisorProcessRequest>();
        var lines = new List<string>();
        var supervisor = new ConductorContinuitySupervisor(new ScriptedHost(request =>
        {
            requests.Add(request);
            Assert.InRange(requests.Count, 1, 2);
            if (requests.Count == 1)
            {
                // Replay the historical follower: it exited for handoff without a quiescence line.
                File.WriteAllText(request.StdoutPath, "LOOP_STOP reason=self-relaunch-handoff");
                ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                    new("self-relaunch-handoff", 1, 0, true, HomeBuildTarget: fixture.Candidate));
            }
            else
            {
                Assert.Null(request.CommandPrefix);
                Assert.Contains($"--home-build-blocked-commit={fixture.Candidate.AdoptedCommit}", request.Arguments);
                AssertAcceptedByConduct(request.Arguments, requireCurrentStores: false);
                ConductorContinuityExitArtifact.Write(request.ExitArtifactPath, new("stop-file", 1, 0, false));
            }
            return Task.FromResult(new ConductorSupervisorProcessResult(0, 100 + requests.Count));
        }), new RecordingEvents(),
            homeBuildFollower: new HomeBuildFollowerSuccessorSource(fixture.Root), schemaUpgrade: upgrade,
            inspectStores: () => [new("fixture", database, 1, 1)],
            delay: (_, _) => Task.CompletedTask,
            appendConductEvent: (_, _, line) => lines.Add(line),
            verifyRunClosure: (folder, _, _) => new(true, folder, 0, 0, "none", "fixture"));

        Assert.Equal(0, await supervisor.RunAsync(["conduct", "--loop"], fixture.Root,
            Path.Combine(stateDirectory, "continuity"), "project", "tenant")
            .WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, requests.Count);
        Assert.Equal(1, planCalls);
        Assert.Contains(lines, line => line.Contains("phase=schema-upgrade reason=quiescence", StringComparison.Ordinal));
    }

    private static void AssertAcceptedByConduct(IReadOnlyList<string> arguments, bool requireCurrentStores)
    {
        // The production startup chain consumes workspace and identity options before conduct validation.
        var binding = WorkspaceInvocationBinding.Parse(arguments);
        Assert.Equal(requireCurrentStores, binding.RequireCurrentStores);
        var project = OrchestratorProjectSelection.FromArgs(binding.CommandArgs, environmentProject: null);
        var tenant = OrchestratorTenantSelection.FromArgs(project.CommandArgs, environmentTenant: null);
        var command = CliArgumentParser.NormalizeArgs(tenant.CommandArgs.ToArray());
        Assert.Equal("conduct", command[0]);
        CliCommandHelp.ThrowIfInvalidFlags(command);
    }

    private sealed class ScriptedHost(Func<ConductorSupervisorProcessRequest, Task<ConductorSupervisorProcessResult>> run)
        : IConductorSupervisorProcessHost
    {
        public Task<ConductorSupervisorProcessResult> RunAsync(ConductorSupervisorProcessRequest request,
            CancellationToken cancellationToken) => run(request);
    }

    private sealed class RecordingEvents : IRunEventStore
    {
        private long _sequence;
        public Task<RunEventRecord> AppendAsync(RunEventAppend evt, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RunEventRecord(++_sequence, evt.EventId ?? Guid.NewGuid().ToString("N"),
                evt.OccurredAt ?? DateTimeOffset.MinValue, evt.EventType, evt.GoalId,
                evt.Operation, evt.Status, evt.Detail, evt.PayloadJson));

        public Task<IReadOnlyList<RunEventRecord>> ReadSinceAsync(long afterSequence = 0,
            string? goalId = null, int maxCount = 500, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RunEventRecord>>([]);
    }
}
