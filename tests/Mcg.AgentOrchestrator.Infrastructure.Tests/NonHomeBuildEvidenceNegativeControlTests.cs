using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns its worktree, state, build storage and child processes.
public sealed class NonHomeBuildEvidenceNegativeControlTests : WorkerDispatchTestSupport
{
    private const string CompilerError = "Non-home completion build compiler error";

    [Fact]
    public async Task PromptedBuildFailureFailsRoundWithCompilerDiagnostic()
    {
        using var fixture = new WorkerBuildRequestFixture(compilerFailure: true, isNonHome: true, linkedWorktree: true);
        var root = fixture.RepositoryRoot;
        Assert.False(Directory.Exists(Path.Combine(root, "scripts")));
        var goal = fixture.Kernel.GetGoal(fixture.GoalId);
        var task = fixture.Kernel.GetTask(fixture.GoalId, fixture.TaskId);
        var context = WorkerContextArtifacts.Write(goal, task, root, targetHome: fixture.TargetHome);
        var guidance = Assert.Single(File.ReadLines(Path.Combine(context, "current-task.md"))
            .Where(line => line.Contains("worker-build-request --selected-targets", StringComparison.Ordinal)));

        var invocation = await fixture.RunHelper(command: guidance.Split('`')[1] + "; exit $LASTEXITCODE");

        Assert.Equal(WorkerBuildOutcome.CompilerFailure, invocation.Outcome);
        Assert.Equal([WorkerBuildRequestFixture.Project], invocation.BuildTargets);
        Assert.Equal(1, invocation.CompilerExitCode);
        var process = task.LastProcess!;
        File.WriteAllText(process.StandardOutputPath,
            WorkerResultBlock(WorkerBuildRequestFixture.Source, "host build requested", "deferred - Acceptance owns tests", commit: "none"));
        // The worker wrapper reports success, so only the host's compiler verdict can fail the round.
        File.WriteAllText(process.ExitCodePath, "0");
        var runner = new BackgroundDispatchRunner(new CompletionBuildEvidenceFixture.FixtureClock(DateTimeOffset.UtcNow),
            isStillRunning: _ => false) { WorkerBuildInvocations = fixture.Operations };

        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);

        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Null(task.CompletionBuildEvidence);
        Assert.True(Assert.Single(task.WorkerBuildInvocations).Consumed);
        var diagnostic = task.LastVerification!.StandardError;
        Assert.Contains(DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed), diagnostic);
        Assert.Contains("Feature.cs(2,1): error CS1002: ; expected", diagnostic);
    }

    [Fact]
    public async Task AutomaticBuildFailureFailsRoundWithCompilerDiagnostic()
    {
        var fixture = new CompletionBuildEvidenceFixture();
        var root = fixture.Task.LastProcess!.WorkingDirectory;
        var receipt = Path.Combine(DotnetBuildStorageLayout.GoalArtifactsPath(fixture.GoalId, fixture.Storage), WorkerBuildReceipt.FileName);
        Process? child = null;
        var coordinator = new CompletionBuildEvidenceCoordinator(fixture.StatePath, fixture.Root,
            storageRoot: fixture.Storage, startProcess: Start);
        try
        {
            File.WriteAllText(Path.Combine(root, WorkerBuildRequestFixture.Project), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <RestoreConfigFile>$(MSBuildThisFileDirectory)../../NuGet.Config</RestoreConfigFile>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(root, WorkerBuildRequestFixture.Source), "#error " + CompilerError);
            File.WriteAllText(Path.Combine(root, "NuGet.Config"), "<configuration><packageSources><clear /></packageSources></configuration>");
            var staged = GitCli.RunForRepository(root, "add", "--", "NuGet.Config", "src");
            Assert.True(staged.Succeeded, staged.Error);
            Assert.False(Directory.Exists(Path.Combine(root, "scripts")));
            var runner = new BackgroundDispatchRunner(fixture.Clock, isStillRunning: _ => false,
                completionBuildEvidence: coordinator, resolveWorkerBuildReceiptPath: _ => receipt)
            {
                WorkerBuildInvocations = new WorkerBuildInvocationOperations(fixture.StatePath, fixture.Root,
                    fixture.Storage, isNonHome: true)
            };

            runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
            var operation = Assert.IsType<CompletionBuildEvidenceOperation>(fixture.Task.CompletionBuildEvidence);
            Assert.Equal(CompletionBuildEvidencePhase.Pending, operation.Phase);
            Assert.Equal([WorkerBuildRequestFixture.Project], operation.BuildTargets);
            Assert.NotNull(child);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5));
            runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);

            var terminal = fixture.Task.CompletionBuildEvidence!;
            Assert.Equal(operation.Id, terminal.Id);
            Assert.True(terminal.Phase == CompletionBuildEvidencePhase.CompilerFailure, terminal.Diagnostic);
            Assert.True(terminal.Applied);
            Assert.False(File.Exists(receipt));
            Assert.Equal(WorkTaskStatus.Failed, fixture.Task.Status);
            var diagnostic = fixture.Task.LastVerification!.StandardError;
            Assert.Contains(DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed), diagnostic);
            Assert.Contains("error CS1029", diagnostic);
            Assert.Contains(CompilerError, diagnostic);
        }
        finally
        {
            coordinator.Cancel(fixture.Kernel, fixture.GoalId, fixture.TaskId);
            child?.Dispose();
            SharedTestSupport.RemoveTempDirectory(fixture.Root);
        }

        Process Start(ProcessStartInfo info)
        {
            info.Environment.Remove("MCG_ORCHESTRATOR_HOME");
            info.Environment[DotnetBuildStorageLayout.IsolatedRootOverrideVariable] = fixture.Storage.RootPath;
            var launched = Process.Start(info) ?? throw new InvalidOperationException("Completion build child could not start.");
            child = Process.GetProcessById(launched.Id);
            _ = child.Handle;
            return launched;
        }
    }

    [Fact]
    public void NoTargetsCompletesRoundWithCandidateBoundProof()
    {
        var fixture = new CompletionBuildEvidenceFixture();
        try
        {
            var root = fixture.Task.LastProcess!.WorkingDirectory;
            File.Delete(Path.Combine(root, WorkerBuildRequestFixture.Project));
            Assert.True(WorktreeTreeDigest.TryCompute(root, out var digest, out var reason), reason);
            var runner = DeclaredNonHomeRunner(fixture);

            runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);

            Assert.Equal(WorkTaskStatus.Completed, fixture.Task.Status);
            Assert.Null(fixture.Goal.CurrentHold);
            Assert.Null(fixture.Task.CompletionBuildEvidence);
            Assert.Equal(0, fixture.Launches);
            var verification = fixture.Task.LastVerification!;
            Assert.True(verification.Succeeded, verification.StandardError);
            Assert.Contains($"worker_build_target_selection=NotApplicable; root={root}; tree_digest={digest}; " +
                "tracked_root_solutions=0; tracked_projects=0", verification.StandardError);
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(fixture.Root);
        }
    }

    [Fact]
    public void MalformedSolutionFailsRoundWithSelectionError()
    {
        var fixture = new CompletionBuildEvidenceFixture();
        try
        {
            var root = fixture.Task.LastProcess!.WorkingDirectory;
            File.Delete(Path.Combine(root, WorkerBuildRequestFixture.Project));
            File.WriteAllText(Path.Combine(root, "Broken.slnx"), "<Broken />");
            var staged = GitCli.RunForRepository(root, "add", "--", "Broken.slnx");
            Assert.True(staged.Succeeded, staged.Error);
            var runner = DeclaredNonHomeRunner(fixture);

            runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);

            Assert.Equal(WorkTaskStatus.Failed, fixture.Task.Status);
            Assert.Null(fixture.Task.CompletionBuildEvidence);
            Assert.Equal(0, fixture.Launches);
            var diagnostic = fixture.Task.LastVerification!.StandardError;
            Assert.Contains(DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing), diagnostic);
            Assert.Contains("worker_build_target_selection=error; solutions/Broken.slnx: " +
                "Confirm solution membership: Invalid Solution root element.", diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("worker_build_target_selection=NotApplicable", diagnostic);
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(fixture.Root);
        }
    }

    private static BackgroundDispatchRunner DeclaredNonHomeRunner(CompletionBuildEvidenceFixture fixture)
    {
        var projectHome = Path.Combine(fixture.Root, "registered-project");
        Directory.CreateDirectory(projectHome);
        File.WriteAllText(Path.Combine(projectHome, "project-model.json"), """{"stack":"dotnet"}""");
        return new BackgroundDispatchRunner(fixture.Clock, isStillRunning: _ => false,
            completionBuildEvidence: fixture.Coordinator(), resolveWorkerBuildReceiptPath: _ => fixture.ReceiptPath)
        {
            WorkerBuildInvocations = new WorkerBuildInvocationOperations(fixture.StatePath, fixture.Root,
                fixture.Storage, projectHomeDirectory: projectHome, isNonHome: true)
        };
    }
}
