using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Serial verifier lane: fixture owns its git repo; Crosswalk fixture scopes/restores the prefix env var.
[Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class GoalAcceptanceVerifierTestsAcceptanceAttemptResultRecordTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MintedPrefix_TerminalGate_WritesSharedSchemaAndCandidate(bool passed)
    {
        using var fixture = Fixture();
        fixture.Output = CrosswalkAcceptanceFixture.Report((CrosswalkAcceptanceFixture.Id, passed ? "passed" : "failed"));
        fixture.Exit = passed ? 0 : 1;
        await using var owner = AcceptanceExecutionOwners.CreateAttempt(fixture.Root,
            options: new(IntegrationBranch: "main"));
        var identity = Assert.IsType<AcceptanceAttemptExecutionOwner>(owner).Identity;
        Assert.False(File.Exists(identity.ResultsPrefix + ".result.json"));

        var verification = await Run(fixture, owner);

        Assert.NotEmpty(fixture.Calls);
        Assert.Equal(passed, verification.Passed);
        var artifact = Read(identity.ResultsPrefix);
        Assert.Equal("accepted", artifact.Kind);
        Assert.Equal("gate", artifact.DispatchKind);
        Assert.Equal(passed, artifact.Acceptance!.Passed);
        Assert.Equal(identity.AttemptId, artifact.Acceptance.AttemptId);
        var head = RunGitWithCommitPostcondition(fixture.Root, ["rev-parse", "HEAD"]).StandardOutput.Trim();
        var main = RunGitWithCommitPostcondition(fixture.Root, ["rev-parse", "main"]).StandardOutput.Trim();
        Assert.Equal(40, head.Length);
        Assert.Equal(head, artifact.BranchHeadSha);
        Assert.Equal(main, artifact.MainHeadSha);
        Assert.Equal(head, artifact.Acceptance.BranchHeadSha);
        Assert.Equal(main, artifact.Acceptance.MainHeadSha);
        Assert.Equal(JsonSerializer.Serialize(verification.LaneWaits), JsonSerializer.Serialize(artifact.Acceptance.LaneWaits));
        Assert.Equal(verification.TestResultPaths, artifact.Acceptance.TestResultPaths);
        if (!passed) Assert.Contains("crosswalk selection", artifact.Acceptance.FailedChecks!);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(identity.ResultsPrefix)!, "*.tmp"));
    }

    [Fact]
    public async Task MintedPrefix_CancelledGate_RecordsCancellationBeforeRethrow()
    {
        using var fixture = Fixture();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return fixture.Result(fixture.Discovery);
        };
        await using var owner = AcceptanceExecutionOwners.CreateAttempt(fixture.Root,
            cancellationToken: cancellation.Token, options: new(IntegrationBranch: "main"));
        var identity = Assert.IsType<AcceptanceAttemptExecutionOwner>(owner).Identity;
        var pending = Run(fixture, owner);
        try
        {
            await entered.Task.WaitAsync(TestHangGuard.HangSafetyBound); // hang-only: discovery did not enter
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await pending.WaitAsync(TestHangGuard.HangSafetyBound)); // hang-only: cancelled gate did not finish
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
        }
        var artifact = Read(identity.ResultsPrefix);
        Assert.Single(fixture.Calls);
        Assert.Equal("fault", artifact.Kind);
        Assert.Equal("cancelled", artifact.FaultKind);
        Assert.Equal(identity.VerifyingCommitSha, artifact.BranchHeadSha);
        Assert.Equal(identity.MainSha, artifact.MainHeadSha);
    }

    [Fact]
    public async Task MintedPrefix_FaultedGate_RecordsOriginalFault()
    {
        using var fixture = Fixture();
        await using var owner = AcceptanceExecutionOwners.CreateAttempt(fixture.Root,
            options: new(IntegrationBranch: "main"));
        var prefix = Assert.IsType<AcceptanceAttemptExecutionOwner>(owner).Identity.ResultsPrefix;
        File.WriteAllText(Path.Combine(fixture.Root, "config", "acceptance-manifest.json"), "{broken");

        var fault = await Assert.ThrowsAsync<AcceptanceGateEngineException>(() => Run(fixture, owner));

        var artifact = Read(prefix);
        Assert.Equal("fault", artifact.Kind);
        Assert.Equal("gate-engine-fault", artifact.FaultKind);
        Assert.Equal(fault.Message, artifact.FaultMessage);
        Assert.Equal(fault.FaultType, artifact.FaultType);
    }

    [Fact]
    public async Task SuppliedPrefix_ChildGate_DoesNotPublishWrapperRecord()
    {
        using var fixture = Fixture();
        await using var owner = AcceptanceExecutionOwners.CreateAttempt(fixture.Root,
            options: new(ResultsPrefix: fixture.Prefix, IntegrationBranch: "main"));

        Assert.True((await Run(fixture, owner)).Passed);

        Assert.NotEmpty(fixture.Calls);
        Assert.False(File.Exists(fixture.Prefix + ".result.json"));
    }

    [Fact]
    public async Task InheritedPrefix_CompatibilityChild_DoesNotPublishWrapperRecord()
    {
        using var fixture = new CrosswalkAcceptanceFixture();

        Assert.True((await fixture.Verifier.RunAsync(fixture.Root)).Passed);

        Assert.NotEmpty(fixture.Calls);
        Assert.False(File.Exists(fixture.Prefix + ".result.json"));
    }

    [Fact]
    public void Wrapper_RunAttempt_IsOnlyWriterBeforeAndAfterChildReturns()
    {
        using var fixture = Fixture();
        var goal = new AgentOrchestratorKernel().CreateGoal("record the wrapper result once");
        var head = RunGitWithCommitPostcondition(fixture.Root, ["rev-parse", "HEAD"]).StandardOutput.Trim();
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [], head, head);
        var published = 0;
        var calls = 0;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(fixture.Root, "attempts"), "main", executionDirectory: fixture.Root,
            acquireStableSlotLease: (_, _) => null, resultPublishedForTests: _ => published++);
        var attempt = coordinator.CreateAttemptForTests(candidate);

        coordinator.RunAttemptForTests(attempt, candidate, ConductorAutonomyPolicy.Permissive,
            (current, _, _, _, options) =>
            {
                calls++;
                Assert.Equal(attempt.ResultPath, options.ResultsPrefix + ".result.json");
                var result = fixture.Verifier.RunOwnedAsync(fixture.Root, null, null, null, null,
                    CancellationToken.None, options with { IntegrationBranch = "main" })
                    .WaitAsync(TestHangGuard.HangSafetyBound).GetAwaiter().GetResult(); // hang-only: child gate did not finish
                Assert.True(result.Passed);
                // Final-file counts cannot detect a child write that the wrapper overwrites.
                Assert.False(File.Exists(attempt.ResultPath));
                return ConductorParallelAcceptanceRunResult.Accepted(current,
                    AcceptanceVerificationSummary.FromVerification(result, [], [], head, head,
                        result.TestResultPaths, null, null));
            });

        Assert.Equal(1, calls);
        Assert.Equal(1, published);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(attempt.ResultPath)!, "*.result.json"));
        Assert.Equal("accepted", Read(attempt.ResultPath[..^".result.json".Length]).Kind);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(attempt.ResultPath)!, "*.tmp"));
    }

    private static CrosswalkAcceptanceFixture Fixture()
    {
        var fixture = new CrosswalkAcceptanceFixture();
        try
        {
            File.WriteAllText(Path.Combine(fixture.Root, ".gitignore"), ".orchestrator/\nattempts/\n");
            RunGitWithCommitPostcondition(fixture.Root, ["init", "-b", "main"]);
            RunGitWithCommitPostcondition(fixture.Root, ["add", "."]);
            RunGitWithCommitPostcondition(fixture.Root, ["-c", "user.name=Fixture", "-c", "user.email=fixture@example.test",
                "-c", "commit.gpgsign=false", "commit", "-m", "fixture"]);
            return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }

    private static Task<AcceptanceVerificationResult> Run(CrosswalkAcceptanceFixture fixture,
        IAcceptanceAttemptExecutionOwner owner) =>
        fixture.Verifier.RunOwnedAsync(fixture.Root, null, null, null, null, owner);

    private static ConductorParallelAcceptanceRunArtifact Read(string prefix) =>
        Assert.IsType<ConductorParallelAcceptanceRunArtifact>(JsonSerializer.Deserialize<ConductorParallelAcceptanceRunArtifact>(
            File.ReadAllText(prefix + ".result.json"), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
}
