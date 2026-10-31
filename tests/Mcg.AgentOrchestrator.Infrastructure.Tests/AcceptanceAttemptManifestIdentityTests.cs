using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its filesystem tree and uses async-local console capture.
public sealed class AcceptanceAttemptManifestIdentityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unreadable_manifest_Attempt_is_persisted_with_unknown_identity(bool malformed)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, ".git"), "gitdir: isolated-fixture");
            if (malformed)
            {
                Directory.CreateDirectory(Path.Combine(root, "config"));
                File.WriteAllText(Path.Combine(root, "config", "acceptance-manifest.json"), "{");
            }
            var goal = new AgentOrchestratorKernel().CreateGoal("capture gate provenance");
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/Widget.cs"]);
            void ComputeIdentity() =>
                GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(root, candidate.ScopePaths);
            Exception failure = malformed
                ? Assert.ThrowsAny<JsonException>(ComputeIdentity)
                : Assert.Throws<MissingAcceptanceManifestException>(ComputeIdentity);
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(root, "attempts"), TrunkBranchName.Default, executionDirectory: root);
            ConductorParallelAcceptanceAttempt? attempt = null;

            var errors = AsyncLocalConsoleRouter.CaptureError(() =>
                attempt = coordinator.CreateAttemptForTests(candidate));

            Assert.NotNull(attempt);
            var persisted = Assert.IsType<ConductorParallelAcceptanceAttempt>(
                coordinator.TryReadLatest(goal.Id.Value));
            Assert.Equal(attempt.AttemptId, persisted.AttemptId);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Running, persisted.Outcome);
            Assert.Null(persisted.EffectiveManifestIdentity);
            Assert.Contains("ACCEPTANCE_ATTEMPT_MANIFEST_IDENTITY_UNAVAILABLE", errors);
            Assert.Contains($"goal={goal.Id.Value} attempt={attempt.AttemptId}", errors);
            Assert.Contains($"exception={failure.GetType().Name}", errors);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public void Readable_manifest_Attempt_persists_the_effective_identity()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "config"));
            File.WriteAllText(Path.Combine(root, "config", "acceptance-manifest.json"), """
                { "version": 1, "checks": [{ "name": "check", "type": "command", "command": "git", "arguments": ["diff", "--check"] }] }
                """);
            var goal = new AgentOrchestratorKernel().CreateGoal("capture gate provenance");
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/Widget.cs"]);
            var expected = GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(root, candidate.ScopePaths);
            Assert.StartsWith("effective-manifest-sha256-", expected);
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(root, "attempts"), TrunkBranchName.Default, executionDirectory: root);

            var errors = AsyncLocalConsoleRouter.CaptureError(() =>
                coordinator.CreateAttemptForTests(candidate));

            var persisted = Assert.IsType<ConductorParallelAcceptanceAttempt>(
                coordinator.TryReadLatest(goal.Id.Value));
            Assert.Equal(expected, persisted.EffectiveManifestIdentity);
            Assert.Empty(errors);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }
}
