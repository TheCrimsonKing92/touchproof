using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixture-owned repository, compiler processes, state, pipe and slot grid.
public sealed class WorkerBuildRequestStructuralIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealCompileViolationReplayAndFreshCorrection(bool aggregate)
    {
        using var fixture = new WorkerBuildRequestFixture(seedRepository: root => WorkerBuildRequestFixture.SeedSizePolicy(root, aggregate));
        var expected = DeveloperCompletionStructuralPreflight.Evaluate(fixture.RepositoryRoot);
        Assert.True(expected.EvaluationComplete);
        Assert.True(expected.HasViolation);
        var adapter = Path.Combine(WorkerBuildRequestFixture.FindRepository(), "scripts", "Request-HostWorkerBuild.ps1").Replace("'", "''");
        var key = Guid.NewGuid().ToString("N");
        var fresh = Guid.NewGuid().ToString("N");
        var call = $"& '{adapter}' -Projects '{WorkerBuildRequestFixture.Project}' -RequestKey '{key}'";
        var scans = 0;
        var invocation = await fixture.RunHelper(command: call + "; if($LASTEXITCODE -ne 1){throw 'violation adapter exit'}; " +
            call + "; if($LASTEXITCODE -ne 1){throw 'replay adapter exit'}; " +
            $"[IO.File]::WriteAllText('{WorkerBuildRequestFixture.Source}', 'public sealed partial class Feature {{ }} public sealed class Added {{ }}'); " +
            $"& '{adapter}' -Projects '{WorkerBuildRequestFixture.Project}' -RequestKey '{fresh}'; exit $LASTEXITCODE",
            structuralEvaluator: () =>
            {
                Interlocked.Increment(ref scans);
                fixture.AssertSlotsFree();
                var pending = fixture.Operations.Load(fixture.GoalId.Value).GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations[^1];
                Assert.Equal(WorkerBuildOutcome.Success, pending.Outcome);
                Assert.False(pending.CustodyReleased);
                return DeveloperCompletionStructuralPreflight.Evaluate(fixture.RepositoryRoot);
            });
        Assert.Equal(2, scans);
        Assert.Equal(3, fixture.Responses.Count);
        var failed = fixture.Responses[0];
        Assert.Equal(WorkerBuildOutcome.Success, failed.Outcome);
        Assert.Equal(0, failed.CompilerExitCode);
        Assert.Equal(1, failed.ExitCode);
        Assert.Equal(WorkerBuildStructuralStatus.Violation, failed.StructuralCheck!.Status);
        Assert.Equal(expected.Message, failed.StructuralDiagnostic);
        Assert.Contains(aggregate ? "class:Feature" : WorkerBuildRequestFixture.Source, failed.StructuralDiagnostic);
        Assert.Contains("2", failed.StructuralDiagnostic);
        Assert.Contains("ceiling of 1", failed.StructuralDiagnostic);
        Assert.Contains(aggregate ? "Extract members into a separately owned type" : "Extract", failed.StructuralDiagnostic);
        Assert.Equal(failed, fixture.Responses[1]);
        Assert.Equal(WorkerBuildStructuralStatus.Passed, invocation.StructuralCheck!.Status);
        Assert.Equal(0, fixture.Responses[2].ExitCode);
        Assert.NotEqual(failed.InvocationId, invocation.Id);
        Assert.NotEqual(failed.StructuralCheck.TreeDigest, invocation.TreeDigest);
        Assert.Equal(fixture.Reference.DispatchId, invocation.StructuralCheck.DispatchId);
        Assert.Equal(invocation.ScopeIdentity, invocation.StructuralCheck.ScopeIdentity);
        Assert.All(fixture.Operations.Load(fixture.GoalId.Value).GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations,
            item => { Assert.True(item.CustodyReleased); fixture.AssertCompilerImages(item); });
        var output = File.ReadAllText(Path.Combine(fixture.Root, "worker.out.log"));
        Assert.Contains("compiler_exit=0; structural_status=Violation; verification_exit=1", output);
        Assert.Contains("compiler_exit=0; structural_status=Passed; verification_exit=0", output);
    }

    [Fact]
    public async Task CandidateChangedDuringScanInvalidatesPassAndReleasesCustody()
    {
        using var fixture = new WorkerBuildRequestFixture();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = fixture.RunHelper(structuralEvaluator: () =>
        {
            var findings = DeveloperCompletionStructuralPreflight.Evaluate(fixture.RepositoryRoot);
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromMinutes(3)), "Structural scan release event did not arrive.");
            return findings;
        });
        WorkerBuildInvocation invocation;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromMinutes(3));
            fixture.AssertSlotsFree();
            var pending = Assert.Single(fixture.Operations.Load(fixture.GoalId.Value).GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations);
            Assert.False(pending.CustodyReleased);
            File.AppendAllText(Path.Combine(fixture.RepositoryRoot, WorkerBuildRequestFixture.Source), "\n// source drift");
        }
        finally { release.Set(); invocation = await running.WaitAsync(TimeSpan.FromMinutes(3)); }
        Assert.Equal(WorkerBuildOutcome.Success, invocation.Outcome);
        Assert.Equal(0, invocation.CompilerExitCode);
        Assert.Equal(WorkerBuildStructuralStatus.Unavailable, invocation.StructuralCheck!.Status);
        Assert.Contains("WorkerBuildCandidateChangedException", invocation.StructuralCheck.Diagnostic);
        Assert.Equal(1, Assert.Single(fixture.Responses).ExitCode);
        Assert.True(invocation.CustodyReleased);
        fixture.AssertSlotsFree();
    }

    [Fact]
    public async Task ChangedCandidateCannotReplayPassingRequest()
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake,
            seedRepository: root =>
            {
                WorkerBuildRequestFixture.SeedSizePolicy(root);
                var path = Path.Combine(root, SourceSizeRatchet.SourcePath);
                File.WriteAllText(path, File.ReadAllText(path).Replace(", 1)", ", 100)", StringComparison.Ordinal));
            });
        var adapter = Path.Combine(WorkerBuildRequestFixture.FindRepository(), "scripts", "Request-HostWorkerBuild.ps1").Replace("'", "''");
        var call = $"& '{adapter}' -Projects '{WorkerBuildRequestFixture.Project}' -RequestKey '{Guid.NewGuid():N}'";
        var service = new ReplayRejectionService(fixture.Host());
        var invocation = await fixture.RunHelper(assertAdapterExit: false, command: "$ErrorActionPreference='Stop'; " +
            call + "; if($LASTEXITCODE -ne 0){throw 'initial adapter exit'}; " +
            $"[IO.File]::AppendAllText('{WorkerBuildRequestFixture.Source}', '// changed'); " +
            "$rejected=$false; try { " + call + " } catch { $rejected=$true }; " +
            "if(-not $rejected){throw 'replay unexpectedly accepted'}; exit 0", service: service);
        Assert.Equal(WorkerBuildStructuralStatus.Passed, invocation.StructuralCheck!.Status);
        Assert.Contains("idempotency key was reused for another payload/candidate",
            Assert.IsType<UnauthorizedAccessException>(service.Rejection).Message);
        Assert.Single(fixture.Responses);
        Assert.Equal(0, fixture.Worker!.TryReadOwnedExitCode());
        Assert.Single(fixture.Operations.Load(fixture.GoalId.Value).GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations);
    }

    private sealed class ReplayRejectionService(WorkerBuildRequestHost host) : IWorkerBuildRequestService
    {
        internal Exception? Rejection { get; private set; }

        public async Task<WorkerBuildResponse> ExecuteAsync(WorkerBuildRequest request, WorkerBuildProcess worker,
            CancellationToken lifetime)
        {
            try { return await host.ExecuteAsync(request, worker, lifetime); }
            catch (UnauthorizedAccessException error)
            {
                Rejection = error;
                throw;
            }
        }
    }

    [Fact]
    public async Task NonHomeRepositoryHasProvenNonApplicability()
    {
        using var fixture = new WorkerBuildRequestFixture();
        var reached = false;
        var invocation = await fixture.RunHelper(structuralEvaluator: () =>
        {
            reached = true;
            return DeveloperCompletionStructuralPreflight.Evaluate(fixture.RepositoryRoot);
        });
        Assert.True(reached);
        Assert.Equal(WorkerBuildStructuralStatus.NotApplicable, invocation.StructuralCheck!.Status);
        Assert.Equal(0, Assert.Single(fixture.Responses).ExitCode);
        Assert.True(invocation.CustodyReleased);
    }
}
