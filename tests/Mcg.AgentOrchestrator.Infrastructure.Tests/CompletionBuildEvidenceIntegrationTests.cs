using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its repository, SQLite, child handles and build storage.
// Compilation is real; only the launch environment is pinned through the process seam.
public sealed class CompletionBuildEvidenceIntegrationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, CompletionBuildEvidencePhase.Success)]
    [InlineData(true, CompletionBuildEvidencePhase.CompilerFailure)]
    public async Task RealChild_CompilesCandidateAndPersistsApplicableOutcome(
        bool invalidSource, CompletionBuildEvidencePhase expectedPhase)
    {
        var fixture = new CompletionBuildEvidenceFixture();
        var storage = new DotnetBuildStorageRoot(Path.Combine(Path.GetTempPath(), "cbe-" + Guid.NewGuid().ToString("N")));
        var worktree = fixture.Task.LastProcess!.WorkingDirectory;
        var repository = WorkerBuildRequestFixture.FindRepository();
        var receiptPath = Path.Combine(DotnetBuildStorageLayout.GoalArtifactsPath(fixture.GoalId, storage), WorkerBuildReceipt.FileName);
        Process? child = null;
        var launches = 0;
        var coordinator = new CompletionBuildEvidenceCoordinator(fixture.StatePath, fixture.Root,
            storageRoot: storage, remoteBuildBudget: _ => TimeSpan.Zero, startProcess: Start);
        try
        {
            Assert.True(File.Exists(Path.Combine(repository, "scripts", "Invoke-WorkerBuildCheck.ps1")));
            Assert.True(File.Exists(Path.ChangeExtension(typeof(CompletionBuildEvidenceCoordinator).Assembly.Location, ".runtimeconfig.json")));
            File.WriteAllText(Path.Combine(worktree, "src", "Feature", "Feature.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <UseSharedCompilation>false</UseSharedCompilation>
                    <RestoreConfigFile>$(MSBuildThisFileDirectory)../../NuGet.Config</RestoreConfigFile>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(worktree, "NuGet.Config"),
                "<configuration><packageSources><clear /></packageSources></configuration>");
            File.WriteAllText(Path.Combine(worktree, "src", "Feature", "Feature.cs"), invalidSource
                ? "#error completion-build-negative-control\npublic sealed class Feature { }"
                : "public sealed class Feature { }");
            WorkerBuildProjectScope.Prepare(worktree, fixture.GoalId.Value, fixture.TaskId.Value, "main");
            var head = Head();
            WorkerBuildScopeDispatchPreparation.RecordDispatchBaseCommit(
                fixture.Kernel, fixture.GoalId, fixture.Task, worktree, head);
            new SqliteOrchestratorStateRepository(fixture.StatePath).SaveAsync(fixture.Kernel).GetAwaiter().GetResult();
            fixture.Reload();
            var scope = Assert.IsType<WorkerBuildScopeIdentity>(fixture.Task.LastDispatch!.WorkerBuildScope);
            Assert.Equal(fixture.Task.LastDispatch.DispatchedAt, scope.CapturedDispatchAt);
            Assert.NotNull(scope.RetainedMetadataBase64);
            var runner = new BackgroundDispatchRunner(fixture.Clock, isStillRunning: _ => false,
                completionBuildEvidence: coordinator, resolveWorkerBuildReceiptPath: _ => receiptPath);

            runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);

            var pending = Assert.IsType<CompletionBuildEvidenceOperation>(fixture.Task.CompletionBuildEvidence);
            Assert.Equal(CompletionBuildEvidencePhase.Pending, pending.Phase);
            Assert.Equal(1, launches);
            Assert.NotNull(child);
            Assert.Equal(child.Id, pending.ChildPid);
            Assert.Equal(new[] { "src/Feature/Feature.csproj" }, pending.Projects);
            Assert.False(pending.Applied);
            Assert.False(pending.CustodyReleased);
            Assert.Null(fixture.Task.LastVerification);
            Assert.Equal(head, Head());
            fixture.Reload();
            Assert.Equal(pending.Id, fixture.Task.CompletionBuildEvidence!.Id);

            // Child exit is the event gate. The timeout only detects a hung real invocation.
            using (var guard = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
                await child.WaitForExitAsync(guard.Token);
            var result = JsonSerializer.Deserialize<CompletionBuildEvidenceOperation>(File.ReadAllText(pending.ResultPath))!;
            output.WriteLine($"operation={pending.Id}; dispatch={pending.DispatchId}; candidate={pending.TreeDigest}; child={pending.ChildPid}; phase={result.Phase}");
            output.WriteLine(result.Diagnostic);
            Assert.Equal(invalidSource ? 1 : 0, child.ExitCode);
            Assert.True(CompletionBuildEvidenceCoordinator.MatchesResult(pending, result));
            Assert.Equal(expectedPhase, result.Phase);
            Assert.Equal(invalidSource ? 1 : 0, result.BuildExitCode);

            using (var receipt = JsonDocument.Parse(File.ReadAllText(receiptPath)))
            {
                var payload = receipt.RootElement;
                Assert.Equal(pending.TreeDigest, payload.GetProperty("treeDigest").GetString());
                Assert.Equal(pending.TreeDigest, payload.GetProperty("finalTreeDigest").GetString());
                Assert.Equal(worktree, payload.GetProperty("worktreeRoot").GetString());
                Assert.Equal(invalidSource ? "failure" : "success", payload.GetProperty("buildOutcome").GetString());
                // The build helper records native separators; operation project identities use '/'.
                Assert.Equal(pending.Projects, payload.GetProperty("projects").EnumerateArray()
                    .Select(p => p.GetString()!.Replace('\\', '/')));
                var project = Assert.Single(payload.GetProperty("projectResults").EnumerateArray());
                Assert.Equal(pending.Projects[0], project.GetProperty("project").GetString()!.Replace('\\', '/'));
                Assert.Equal(invalidSource ? 1 : 0, project.GetProperty("exitCode").GetInt32());
                var compilerLog = File.ReadAllText(project.GetProperty("logPath").GetString()!);
                Assert.NotEmpty(compilerLog);
                if (invalidSource)
                {
                    Assert.Contains("error CS1029", compilerLog);
                    Assert.Contains("completion-build-negative-control", compilerLog);
                }
                else Assert.NotEmpty(Directory.GetFiles(payload.GetProperty("artifactsPath").GetString()!,
                    "Feature.dll", SearchOption.AllDirectories));
            }
            if (!invalidSource)
                Assert.True(WorkerBuildReceipt.Evaluate(receiptPath, worktree).Matches);

            runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);

            var terminal = fixture.Task.CompletionBuildEvidence!;
            Assert.Equal(expectedPhase, terminal.Phase);
            Assert.True(terminal.CustodyReleased);
            Assert.True(terminal.Applied);
            Assert.Equal(CompletionBuildChildLiveness.Dead, CompletionBuildEvidenceChildIdentity.Probe(terminal));
            Assert.Null(new ReconcileSweepRemediationStore(fixture.StatePath).TryGetAcceptanceLeaseOwner(fixture.GoalId.Value));
            Assert.Equal(invalidSource ? WorkTaskStatus.Failed : WorkTaskStatus.Completed, fixture.Task.Status);
            Assert.Equal(!invalidSource, fixture.Task.LastVerification!.Succeeded);
            if (invalidSource)
            {
                Assert.Equal(head, Head());
                Assert.Contains(DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed),
                    fixture.Task.LastVerification.StandardError);
            }
            else Assert.NotEqual(head, Head());
            new SqliteOrchestratorStateRepository(fixture.StatePath).SaveAsync(fixture.Kernel).GetAwaiter().GetResult();
            fixture.Reload();
            Assert.True(fixture.Task.CompletionBuildEvidence!.Applied);
            Assert.True(fixture.Task.CompletionBuildEvidence.CustodyReleased);
            Assert.Equal(expectedPhase, fixture.Task.CompletionBuildEvidence.Phase);
            var verification = fixture.Task.LastVerification;
            var terminalHead = Head();
            runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
            Assert.Equal(verification, fixture.Task.LastVerification);
            Assert.Equal(terminalHead, Head());
            Assert.Equal(1, launches);
        }
        finally
        {
            // Settle through the owner before removing artifacts; retain them on unknown custody.
            coordinator.Cancel(fixture.Kernel, fixture.GoalId, fixture.TaskId);
            if (child is not null)
            {
                Assert.True(child.HasExited, "Completion build child still holds fixture custody.");
                child.Dispose();
            }
            SharedTestSupport.RemoveTempDirectory(fixture.Root);
            SharedTestSupport.RemoveTempDirectory(storage.RootPath);
            Assert.False(Directory.Exists(fixture.Root));
            Assert.False(Directory.Exists(storage.RootPath));
        }

        string Head()
        {
            var result = GitCli.RunForRepository(worktree, ["rev-parse", "HEAD"]);
            Assert.True(result.Succeeded, result.Error);
            return result.Output.Trim();
        }

        Process Start(ProcessStartInfo info)
        {
            Assert.Equal(CompletionBuildEvidenceCoordinator.Subcommand, info.ArgumentList[1]);
            Assert.Equal(fixture.StatePath, info.ArgumentList[2]);
            Assert.Equal(worktree, info.WorkingDirectory);
            Assert.True(info.RedirectStandardInput && info.RedirectStandardOutput && info.RedirectStandardError);
            Assert.False(info.UseShellExecute);
            info.Environment.Clear();
            foreach (var name in new[] { "SystemRoot", "WINDIR", "PATH", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" })
                if (Environment.GetEnvironmentVariable(name) is { } value) info.Environment[name] = value;
            // The isolated LOCALAPPDATA below hides per-user PowerShell installations.
            // Keep the production-selected shell discoverable by the coordinator child.
            info.Environment["PATH"] = Path.GetDirectoryName(WorkerShell.Executable) + Path.PathSeparator +
                info.Environment["PATH"];
            // PowerShell needs PATHEXT to resolve the helper's extensionless dotnet invocation.
            if (OperatingSystem.IsWindows()) info.Environment["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
            var profile = Path.Combine(storage.RootPath, "profile");
            Directory.CreateDirectory(profile);
            info.Environment["USERPROFILE"] = profile;
            info.Environment["APPDATA"] = profile;
            info.Environment["LOCALAPPDATA"] = profile;
            info.Environment["TEMP"] = profile;
            info.Environment["TMP"] = profile;
            info.Environment["DOTNET_CLI_HOME"] = profile;
            info.Environment["NUGET_PACKAGES"] = Path.Combine(profile, "packages");
            info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            info.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
            info.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "0";
            info.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            info.Environment["MCG_ORCHESTRATOR_HOME"] = repository;
            info.Environment[DotnetBuildStorageLayout.IsolatedRootOverrideVariable] = storage.RootPath;
            launches++;
            var launched = Process.Start(info) ?? throw new InvalidOperationException("Real completion build child did not start.");
            // Capture owns and disposes the launched Process. Retain a separate OS handle
            // so the test can prove exit even after coordinator collection disposes it.
            child = Process.GetProcessById(launched.Id);
            _ = child.Handle;
            return launched;
        }
    }
}
