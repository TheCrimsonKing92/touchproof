using System.Diagnostics;
using System.Text;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixture-owned repositories, stores, slots and contained children.
public sealed class WorkerBuildRestoreRefusedHostTests
{
    private const string Refusal = "error : Access to the path 'C:\\Users\\fixture\\.nuget\\packages\\libgit2sharp\\0.32.0' is denied.";

    [Fact]
    public async Task Execute_RefusedCacheWrite_NamesPackageAndPreservesExit()
    {
        var invocation = await Execute(Refusal, 3);
        Assert.Equal(WorkerBuildOutcome.PackageRestoreRefused, invocation.Outcome);
        Assert.Equal(3, invocation.CompilerExitCode);
        Assert.Contains("package=libgit2sharp", invocation.Diagnostic);
        Assert.Contains("version=0.32.0", invocation.Diagnostic);
        Assert.Contains("code=ACCESS_DENIED", invocation.Diagnostic);
        Assert.Throws<InvalidOperationException>(() => (invocation with { CompilerExitCode = 0 }).RequireValid());
        Assert.Throws<InvalidOperationException>(() => (invocation with { CompilerExitCode = null }).RequireValid());
        Assert.Throws<InvalidOperationException>(() => (invocation with
        {
            Observations = invocation.Observations.Where(o => o.Phase != WorkerBuildPhase.CompilerStarted).ToArray()
        }).RequireValid());
    }

    [Fact]
    public async Task Execute_OrdinaryCompileError_PreservesFailureDiagnostic()
    {
        var invocation = await Execute("Feature.cs(2,1): error CS1002: ; expected", 1);
        Assert.Equal(WorkerBuildOutcome.CompilerFailure, invocation.Outcome);
        Assert.Equal(1, invocation.CompilerExitCode);
        Assert.Equal("Host observed compiler/build exit failure.", invocation.Diagnostic);
    }

    [Fact]
    public async Task Execute_ZeroExitWithRestoreLookingOutput_PreservesSuccess()
    {
        var invocation = await Execute(Refusal, 0);
        Assert.Equal(WorkerBuildOutcome.Success, invocation.Outcome);
        Assert.Equal(0, invocation.CompilerExitCode);
        Assert.Equal("Host observed all scoped compiler/build exits successfully.", invocation.Diagnostic);
    }

    private static async Task<WorkerBuildInvocation> Execute(string line, int exit)
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake);
        var launches = 0;
        var host = new WorkerBuildRequestHost(fixture.Reference, storageRoot: fixture.Storage,
            launchOverride: (project, compiler) =>
            {
                Assert.Equal(WorkerBuildRequestFixture.Project, project);
                launches++;
                var info = new ProcessStartInfo(WorkerShell.Executable)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = compiler.WorkingDirectory
                };
                info.Environment.Clear();
                foreach (var variable in compiler.Environment) info.Environment[variable.Key] = variable.Value;
                var command = $"[Console]::Error.WriteLine('{line.Replace("'", "''")}'); exit {exit}";
                foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
                    Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) }) info.ArgumentList.Add(arg);
                return info;
            });
        using var current = Process.GetCurrentProcess();
        var response = await host.ExecuteAsync(new WorkerBuildRequest(1, "restore-test",
            [WorkerBuildRequestFixture.Project], "Debug"), WorkerBuildRequestChannel.ReadIdentity(current), CancellationToken.None);
        var invocation = Assert.Single(fixture.Operations.Load(fixture.GoalId.Value)
            .GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations);
        Assert.Equal(invocation.Outcome, response.Outcome);
        Assert.Equal(invocation.CompilerExitCode, response.CompilerExitCode);
        Assert.True(invocation.CustodyReleased);
        Assert.Single(invocation.Children!);
        Assert.Equal(1, launches);
        fixture.AssertCompilerImages(invocation);
        return invocation;
    }
}
