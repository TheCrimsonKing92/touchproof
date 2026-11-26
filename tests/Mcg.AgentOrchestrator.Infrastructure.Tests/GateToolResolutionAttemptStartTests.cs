using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GateToolResolutionAttemptStartTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingPowershellDefersAttemptBeforeAnyLaneCallback(bool availableFromConductor)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires Windows bare command resolution.");
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var environment = GateToolResolutionPreflightTests.CreateToolEnvironment(root);
            var goal = new AgentOrchestratorKernel().CreateGoal("gate tool preflight negative control");
            var worktree = Directory.CreateDirectory(GoalWorktreeLayout.WorktreePath(root, goal.Id)).FullName;
            if (availableFromConductor)
            {
                MovePowershellToRelativePath(root, environment);
                Assert.NotNull(AcceptanceBareCommandResolver.Resolve("powershell", environment, root));
            }
            else
            {
                File.Delete(PowershellPath(environment));
            }
            Assert.Null(AcceptanceBareCommandResolver.Resolve("powershell", environment, worktree));
            var preSlotInvocations = 0;
            var laneInvocations = 0;
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(root, "attempts"), TrunkBranchName.Default, executionDirectory: root,
                runInline: true,
                tryRunPreSlot: (_, _) =>
                {
                    preSlotInvocations++;
                    return null;
                },
                createGateToolEnvironmentForTests: () => environment);
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/Widget.cs"]);

            var decision = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Permissive,
                (_, _, _, _, _) =>
                {
                    laneInvocations++;
                    throw new InvalidOperationException("The lane callback ran after a missing tool.");
                });

            Assert.Equal(0, preSlotInvocations);
            Assert.Equal(0, laneInvocations);
            AssertMissingPowershell(decision, environment["PATH"]!.Length);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FocusedAttemptDefersAtTheArmMissingPowershell(bool candidateHasPowershell)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires Windows bare command resolution.");
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var environment = GateToolResolutionPreflightTests.CreateToolEnvironment(root);
            var conductorPowershell = MovePowershellToRelativePath(root, environment);
            var candidatePath = Directory.CreateDirectory(Path.Combine(root, "candidate-worktree")).FullName;
            var baselinePath = Directory.CreateDirectory(Path.Combine(root, "detached-baseline")).FullName;
            if (candidateHasPowershell)
                File.Copy(conductorPowershell, RelativePowershellPath(candidatePath));
            Assert.NotNull(AcceptanceBareCommandResolver.Resolve("powershell", environment, root));

            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(root, "attempts"), TrunkBranchName.Default, executionDirectory: root,
                runInline: true, acquireStableSlotLease: (_, _) => null,
                createGateToolEnvironmentForTests: () => new Dictionary<string, string?>(environment, StringComparer.OrdinalIgnoreCase));
            var goal = new AgentOrchestratorKernel().CreateGoal("focused tool preflight negative control");
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [], "candidate", "baseline");
            var launchedFrom = new List<string>();
            GoalAcceptanceVerifier.AcceptanceManifestCheck[] checks = [new() { Name = "focused lane" }];
            var coverage = new FocusedEvidenceCoverage([new("Infrastructure.Tests:FocusedTests", [checks[0].Name])]);

            var decision = coordinator.EvaluateFocusedEvidence(candidate, ConductorAutonomyPolicy.Permissive,
                "Infrastructure.Tests: FocusedTests", (_, request, _, token) =>
                {
                    var candidateArm = RunArm(FindingEvidenceArm.Candidate, candidatePath, token);
                    var baselineArm = RunArm(FindingEvidenceArm.Baseline, baselinePath, token);
                    return new FocusedEvidenceRunResult(request, true, true, "both arms passed", candidateArm.Checks,
                        coverage, Arms: [candidateArm, baselineArm]);
                });

            Assert.Equal(candidateHasPowershell ? [candidatePath] : Array.Empty<string>(), launchedFrom);
            AssertMissingPowershell(decision, environment["PATH"]!.Length);

            FocusedEvidenceArmRunResult RunArm(FindingEvidenceArm arm, string path, CancellationToken token) =>
                FocusedEvidenceExecution.RunFocusedEvidenceArmAsync(
                    _ => throw new InvalidOperationException("No execution owner was supplied."),
                    (workingDirectory, _, _, _, _, _, _, _, _) =>
                    {
                        launchedFrom.Add(workingDirectory);
                        return Task.FromResult<IReadOnlyList<AcceptanceCheckResult>>([new(checks[0].Name, true, 0, "passed")]);
                    },
                    _ => string.Empty, arm, arm.ToString(), path, goal.Id, checks, coverage,
                    null, null, null, token).GetAwaiter().GetResult();
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    private static string MovePowershellToRelativePath(string root, Dictionary<string, string?> environment)
    {
        var relativePowershell = RelativePowershellPath(root);
        File.Move(PowershellPath(environment), relativePowershell);
        environment["PATH"] += ";relative-tools";
        return relativePowershell;
    }

    private static string RelativePowershellPath(string root) =>
        Path.Combine(Directory.CreateDirectory(Path.Combine(root, "relative-tools")).FullName, "powershell.exe");

    private static string PowershellPath(Dictionary<string, string?> environment) =>
        Path.Combine(environment["SystemRoot"]!, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");

    private static void AssertMissingPowershell(ConductorParallelAcceptanceAttemptDecision decision, int pathLength)
    {
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.InfrastructureDeferred, decision.Attempt.Outcome);
        var exception = Assert.IsType<AcceptanceInfrastructureDeferredException>(decision.Run?.Exception);
        Assert.Equal("gate-tool-resolution-missing", exception.ReasonCode);
        Assert.Contains("powershell", exception.OutputTail);
        Assert.Contains($"PATH length: {pathLength}", exception.OutputTail);
    }
}
