using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixture-owned repositories, stores, processes and clock; no wall-clock verdicts.
public sealed class WorkerBuildRequestStructuralElapsedTests
{
    [Theory]
    [InlineData(WorkerBuildStructuralStatus.Passed)]
    [InlineData(WorkerBuildStructuralStatus.Violation)]
    [InlineData(WorkerBuildStructuralStatus.NotApplicable)]
    public async Task CompletedEvaluation_PersistsElapsedAndPrintsReceipt(WorkerBuildStructuralStatus status)
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake);
        var reached = false;
        var invocation = await fixture.RunHelper(structuralEvaluator: () =>
        {
            reached = true;
            fixture.Time.Advance(TimeSpan.FromMilliseconds(125.5));
            return new(status == WorkerBuildStructuralStatus.Violation, "fixture findings",
                IsApplicable: status != WorkerBuildStructuralStatus.NotApplicable);
        });

        Assert.True(reached);
        Assert.Equal(status, invocation.StructuralCheck!.Status);
        Assert.Equal("fixture findings", invocation.StructuralCheck.Diagnostic);
        Assert.Equal(125.5, invocation.StructuralCheck.ElapsedMilliseconds);
        Assert.Equal(125.5, Assert.Single(fixture.Responses).StructuralElapsedMilliseconds);
        var stored = Assert.Single(fixture.Operations.Load(fixture.GoalId.Value)
            .GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations);
        Assert.Equal(invocation.StructuralCheck, stored.StructuralCheck);
        Assert.Contains("structural_elapsed_ms=125.5", File.ReadAllText(Path.Combine(fixture.Root, "worker.out.log")));
    }

    [Fact]
    public async Task NonReturningEvaluation_ReturnsUnavailableWithBoundElapsed()
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Time.TimerScheduled = due => { if (due == BoundedStructuralPreflight.DefaultBound) timer.TrySetResult(); };
        var running = fixture.RunHelper(structuralEvaluator: () =>
        {
            entered.SetResult();
            try
            {
                Assert.True(release.Wait(TimeSpan.FromMinutes(3)), "Structural evaluator teardown release did not arrive.");
                return new(false, "late result");
            }
            finally { finished.TrySetResult(); }
        });

        try
        {
            // These waits detect a missing event; only the injected clock decides timeout.
            await Task.WhenAll(entered.Task, timer.Task).WaitAsync(TimeSpan.FromMinutes(3));
            fixture.Time.Advance(BoundedStructuralPreflight.DefaultBound);
            var invocation = await running.WaitAsync(TimeSpan.FromMinutes(3));
            Assert.Equal(WorkerBuildOutcome.Success, invocation.Outcome);
            Assert.Equal(WorkerBuildStructuralStatus.Unavailable, invocation.StructuralCheck!.Status);
            Assert.Equal(BoundedStructuralPreflight.DefaultBound.TotalMilliseconds,
                invocation.StructuralCheck.ElapsedMilliseconds);
            Assert.Contains("exceeded 10000 ms bound", invocation.StructuralCheck.Diagnostic);
            Assert.Equal(invocation.StructuralCheck.ElapsedMilliseconds,
                Assert.Single(fixture.Responses).StructuralElapsedMilliseconds);
            Assert.Contains("structural_elapsed_ms=10000", File.ReadAllText(Path.Combine(fixture.Root, "worker.out.log")));
        }
        finally
        {
            release.Set();
            await running.WaitAsync(TimeSpan.FromMinutes(3));
            await finished.Task.WaitAsync(TimeSpan.FromMinutes(3));
        }
    }

    [Fact]
    public void LegacyEvidence_DeserializesWithoutElapsed()
    {
        const string legacy = """
            {"Status":1,"Diagnostic":"legacy findings","InvocationId":"invocation","DispatchId":"dispatch",
             "TreeDigest":"tree","ScopeIdentity":"scope","ProjectHomeDirectory":null,"PolicyDigest":"policy"}
            """;
        var check = JsonSerializer.Deserialize<WorkerBuildStructuralCheck>(legacy)!;
        Assert.True(check.IsWellFormed);
        Assert.Null(check.ElapsedMilliseconds);
        Assert.Null(new WorkerBuildResponse("invocation", WorkerBuildOutcome.Success, "compiler", 0, check)
            .StructuralElapsedMilliseconds);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidElapsed_MakesEvidenceUnavailable(double elapsed)
    {
        var check = new WorkerBuildStructuralCheck(WorkerBuildStructuralStatus.Passed, "findings",
            "invocation", "dispatch", "tree", "scope", null, "policy", elapsed);
        var response = new WorkerBuildResponse("invocation", WorkerBuildOutcome.Success, "compiler", 0, check);
        Assert.False(check.IsWellFormed);
        Assert.Equal("Unavailable", response.StructuralStatus);
        Assert.Null(response.StructuralElapsedMilliseconds);
        Assert.Equal(1, response.ExitCode);
    }
}
