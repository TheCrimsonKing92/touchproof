using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each dispatch, pipe, child process and artifact has a unique case root.
public sealed class DispatchProcessHostWorkerBuildClientPathTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Launch_BuildEnrollment_ControlsHostAppOutputHandoff(bool enrolled)
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            var inherited = Directory.CreateDirectory(Path.Combine(root, "inherited app")).FullName;
            var reference = new WorkerBuildHostReference(Path.Combine(root, "unused.db"), root,
                "goal", "task", DateTimeOffset.UnixEpoch, "dispatch");
            var parameters = new DispatchProcessHost.DispatchRunParameters(
                "[Console]::WriteLine('started')", root,
                Path.Combine(root, "worker.out.log"), Path.Combine(root, "worker.err.log"),
                Path.Combine(root, "worker.exit.txt"), Path.Combine(root, "worker.heartbeat.json"), false,
                WorkerBuildHost: enrolled ? reference : null);
            ProcessStartInfo? captured = null;
            var serviceCreated = false;
            var descriptorObserved = false;

            var exit = DispatchProcessHost.RunWithHeartbeatHooksForTests(parameters,
                new DispatchProcessHost.HeartbeatTestHooks
                {
                    BeforeTaskIntentAdmission = start =>
                    {
                        AcceptanceHermeticEnvironment.ConfigureHermeticVerificationEnvironment(
                            start.Environment, VerifiedRepositoryRoot.Find());
                        start.Environment["TEMP"] = root;
                        start.Environment["TMP"] = root;
                        start.Environment[WorkerMcgEnvironmentAllowlist.OrchestratorAppOutputVariable] = inherited;
                        start.Environment["MCG_ORCHESTRATOR_HOME"] = root;
                        captured = start;
                        descriptorObserved = File.Exists(Path.Combine(root, WorkerBuildRequestChannel.DescriptorRelativePath));
                    }
                }, buildService: supplied =>
                {
                    Assert.Equal(reference, supplied);
                    serviceCreated = true;
                    return new UnusedBuildService();
                });

            Assert.Equal(0, exit);
            Assert.True(DispatchExitArtifacts.TryRead(parameters.ExitCodePath, out var artifact));
            Assert.Equal(0, artifact.ExitCode);
            Assert.Equal($"started{Environment.NewLine}", File.ReadAllText(parameters.StdoutPath));
            Assert.NotNull(captured);
            Assert.Equal(enrolled, serviceCreated);
            Assert.Equal(enrolled, descriptorObserved);
            Assert.False(captured.Environment.ContainsKey("MCG_ORCHESTRATOR_HOME"));
            if (enrolled)
            {
                var output = captured.Environment[WorkerMcgEnvironmentAllowlist.OrchestratorAppOutputVariable];
                Assert.False(string.IsNullOrWhiteSpace(output));
                Assert.NotEqual(inherited, output);
                Assert.False(Path.GetFullPath(output!).StartsWith(root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));
                var appFolder = Path.GetDirectoryName(typeof(WorkerBuildInvocationOperations).Assembly.Location);
                Assert.Equal(appFolder, Path.TrimEndingDirectorySeparator(output!));
                Assert.True(File.Exists(Path.Combine(output!, "Mcg.AgentOrchestrator.App.dll")));
            }
            else
                Assert.False(captured.Environment.ContainsKey(WorkerMcgEnvironmentAllowlist.OrchestratorAppOutputVariable));
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    private sealed class UnusedBuildService : IWorkerBuildRequestService
    {
        public Task<WorkerBuildResponse> ExecuteAsync(WorkerBuildRequest request, WorkerBuildProcess worker,
            CancellationToken workerLifetime) => throw new InvalidOperationException("The probe worker must not request a build.");
    }
}
