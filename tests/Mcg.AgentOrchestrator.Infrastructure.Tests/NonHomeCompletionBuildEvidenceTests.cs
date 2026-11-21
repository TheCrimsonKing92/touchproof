using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class NonHomeCompletionBuildEvidenceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AutomaticBuildCompletesWithAReceiptForTheSelectedTargets(bool withSolution)
    {
        var fixture = new CompletionBuildEvidenceFixture();
        var root = fixture.Task.LastProcess!.WorkingDirectory;
        var storage = fixture.Storage;
        var receiptPath = Path.Combine(DotnetBuildStorageLayout.GoalArtifactsPath(fixture.GoalId, storage), WorkerBuildReceipt.FileName);
        Process? child = null;
        var coordinator = new CompletionBuildEvidenceCoordinator(fixture.StatePath, fixture.Root,
            storageRoot: storage, startProcess: Start);
        try
        {
            const string cliProject = "src/cli/cli.csproj";
            var secondProject = withSolution ? WorkerBuildRequestFixture.OtherProject : cliProject;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, secondProject))!);
            foreach (var project in new[] { WorkerBuildRequestFixture.Project, secondProject })
                File.WriteAllText(Path.Combine(root, project), """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <TargetFramework>net10.0</TargetFramework>
                        <RestoreConfigFile>$(MSBuildThisFileDirectory)../../NuGet.Config</RestoreConfigFile>
                      </PropertyGroup>
                    </Project>
                    """);
            File.WriteAllText(Path.Combine(root, "NuGet.Config"), "<configuration><packageSources><clear /></packageSources></configuration>");
            if (withSolution)
            {
                File.WriteAllText(Path.Combine(root, "Fixture.slnx"), """
                    <Solution>
                      <Project Path="src/Feature/Feature.csproj" />
                      <Project Path="src/Other/Other.csproj" />
                    </Solution>
                    """);
            }
            else
            {
                File.WriteAllText(Path.Combine(root, "src", "cli", "Cli.cs"), "public sealed class Cli { }");
            }
            var git = withSolution
                ? GitCli.RunForRepository(root, "add", "--", "Fixture.slnx", "NuGet.Config", "src")
                : GitCli.RunForRepository(root, "add", "--", "NuGet.Config");
            Assert.True(git.Succeeded, git.Error);
            Assert.False(Directory.Exists(Path.Combine(root, "scripts")));

            var workspace = OrchestratorWorkspace.ForProject("fixture", fixture.Root,
                dataRootDirectory: Path.GetDirectoryName(fixture.Root)) with { SqliteStatePath = fixture.StatePath };
            var runner = new BackgroundDispatchRunner(fixture.Clock, isStillRunning: _ => false,
                completionBuildEvidence: coordinator, resolveWorkerBuildReceiptPath: _ => receiptPath)
            {
                WorkerBuildInvocations = WorkerBuildInvocationOperations.ForWorkspace(
                    workspace, OrchestratorHome.ForInstallRoot(AppContext.BaseDirectory), storage)
            };
            runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
            var pending = Assert.IsType<CompletionBuildEvidenceOperation>(fixture.Task.CompletionBuildEvidence);
            Assert.Equal(CompletionBuildEvidencePhase.Pending, pending.Phase);
            string[] expectedTargets = withSolution ? ["Fixture.slnx"] : [WorkerBuildRequestFixture.Project, cliProject];
            string[] expectedProjects = withSolution
                ? [WorkerBuildRequestFixture.Project, WorkerBuildRequestFixture.OtherProject]
                : [cliProject, WorkerBuildRequestFixture.Project];
            Assert.Equal(expectedTargets, pending.BuildTargets);
            Assert.Equal(expectedProjects, pending.Projects);
            Assert.NotNull(child);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5));

            runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
            var terminal = fixture.Task.CompletionBuildEvidence!;
            Assert.True(terminal.Phase == CompletionBuildEvidencePhase.Success, terminal.Diagnostic);
            Assert.True(terminal.Applied);
            Assert.Equal(WorkTaskStatus.Completed, fixture.Task.Status);
            var evidence = WorkerBuildReceipt.Evaluate(receiptPath, root);
            Assert.True(evidence.Matches, evidence.Reason);
            Assert.Equal(expectedProjects, evidence.Projects);
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
            info.Environment[DotnetBuildStorageLayout.IsolatedRootOverrideVariable] = storage.RootPath;
            var launched = Process.Start(info) ?? throw new InvalidOperationException("Completion build child could not start.");
            child = Process.GetProcessById(launched.Id);
            _ = child.Handle;
            return launched;
        }
    }
}
