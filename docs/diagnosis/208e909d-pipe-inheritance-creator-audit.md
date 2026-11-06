# Pipe inheritance creator audit (goal 208e909d)

Candidate starts from 17c3db0f6. Source capability is established below; the actual holder in the historical failed gate was never recorded and is not attributed here.

The protection is the existing .NET `Process.s_createProcessLock` monitor, accessed without new static state. Local metadata inspection on .NET 10.0.9 found the private static object field. Every Windows managed `Process.Start` with `UseShellExecute=false`, including instance starts and library calls, takes it. ShellExecute does not inherit the handle set. This is the same design independently recommended by the supplied Anthropic/claude-opus-5-5 Planner (`context/planner-plan.md`, task 8136800a).

## Product creators and producers

| Source location | Capability and treatment |
| --- | --- |
| `src/Mcg.AgentOrchestrator.Execution/Processes/ProcessTreeGuiSuppression.cs:203` | Static managed start; inherits all handles. Keeps its existing launch lock and suppression; now excluded by the shared runtime monitor while native pipe copies are inheritable. |
| `src/Mcg.AgentOrchestrator.Execution/Processes/ProcessTreeGuiSuppression.cs:206` | Instance managed start delegate; same treatment and lock order as static start. |
| `src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceCheckProcessInvoker.cs:502` | Environment-gated legacy negative control delegates to suppressed managed start; same runtime protection, unchanged. |
| `src/Mcg.AgentOrchestrator.Execution/Processes/ProcessTreeGuiSuppression.cs:185` | Direct managed start only on non-Windows; no Windows inheritance window. |
| `src/Mcg.AgentOrchestrator.Execution/Processes/HandleListLaunchedProcess.cs:89` | Non-Windows fallback delegates to managed start; no Windows inheritance window. |
| `src/Mcg.AgentOrchestrator.Execution/Processes/HandleListProcessLauncher.cs:148` | Native handle-list create inherits only listed handles, never arbitrary peer handles. No all-handles native creator remains in product source. |
| `src/Mcg.AgentOrchestrator.Execution/Processes/HandleListProcessLauncher.cs:231` | Pipe producer now creates both ends non-inheritable. `HandleListLaunchedProcess.cs:140` protects enabling inheritance, creation, and closing every child copy (including failure). Observers and event attachment run outside the monitor. |
| `src/Mcg.AgentOrchestrator.Execution/Processes/OwnedProcessGroup.cs:132` | Anonymous pipes now have non-inheritable clients. The suspended start duplicates all three streams privately; the core at `:879` enables inheritance and disposes duplicates under the same runtime monitor. Original client copies never become inheritable. |
| `src/Mcg.AgentOrchestrator.Execution/Processes/OwnedProcessGroup.cs:1036` | Capture duplicates initially non-inheritable. Actual handle type selects protection: pipe duplicates (including mixed disk/pipe captures) get the monitor; disk-file captures retain their existing observer semantics. |
| `src/Mcg.AgentOrchestrator.App/Orchestration/WindowsHandleListProcessLauncher.cs:34` | Inheritable disk-file log producer, not a pipe. Detached successor launch uses the explicit native handle list; unchanged. |

Suppression and its legacy launch lock are unchanged. Lock order stays suppression/legacy lock before runtime monitor. There are no callbacks inside the pipe protection, no new partial types or mutable static fields, and the production 5000 ms drain bound is unchanged. `GetFileType` failure is loud, rather than falling through to unprotected creation.

## Test-host creators

Each managed creator below either shares the runtime monitor automatically or is classified as source text / a separate child-process fixture. This conservative lexical inventory includes non-executable source samples so their exclusion is reviewable. Instance starts are included. Existing fixture sources need no edits.

Native declarations found under tests belong to separately built ConsoleIoProbe fixtures (`Program.cs:177`, `StartupPipeProbe.cs:49`, the latter has a handle list), or to the external PowerShell breakaway probe string (`DisposableTrialRootNegativeControlTests.cs:345`, inheritance=false); none executes an unprotected all-handles native create inside the test host.

| Source location | Treatment | Matching source |
| --- | --- | --- |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/AcceptanceFailureCensusScriptTests.cs:362` | Shared runtime monitor; no per-site edit. | `using var process = Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/AcceptanceGateFlakeInventoryGeneratorTests.cs:176` | Shared runtime monitor; no per-site edit. | `using var process = Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/AcceptanceOutputCaptureTests.cs:297` | Shared runtime monitor; no per-site edit. | `using var process = Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/AdvanceLoopTestsBlockingWorkerRelease.cs:69` | Shared runtime monitor; no per-site edit. | `Assert.True(process.Start());` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/AgentRosterReloadScenarioTests.cs:263` | Shared runtime monitor; no per-site edit. | `_capture = TestChildProcessCapture.Start(start, start: info => _process = Process.Start(info));` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/AssemblyTempRedirectTests.cs:721` | Shared runtime monitor; no per-site edit. | `Assert.True(process.Start(), $"Failed to start MTP assembly '{testAssembly}' with '{executable}'.");` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ChaosGateHermeticGitProbeTests.cs:30` | Shared runtime monitor; no per-site edit. | `return process.Start();` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliChildProcessRunner.cs:23` | Shared runtime monitor; no per-site edit. | `using var process = Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliChildProcessBoundSourceGuardTests.cs:34` | Shared runtime monitor; no per-site edit. | `const string oldSeconds = "class C { void M() { var start = new ProcessStartInfo(\"dotnet\"); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); using var process = Process.Start(start); } }";` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliChildProcessBoundSourceGuardTests.cs:37` | Shared runtime monitor; no per-site edit. | `const string allowed = "class C { void M() { var start = new ProcessStartInfo(\"dotnet\"); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120)); var result = CliChildProcessRunner.Run(start); } void Sentinel() { var worker = Process.Start(workerInfo); var text = \"60 seconds\"; } }";` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.cs:206` | Shared runtime monitor; no per-site edit. | `using var process = Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.cs:516` | Shared runtime monitor; no per-site edit. | `using var process = Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.cs:554` | Shared runtime monitor; no per-site edit. | `using var process = Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliHelpTests.cs:860` | Shared runtime monitor; no per-site edit. | `worker = Process.Start(new ProcessStartInfo` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliOwnerDigestCommandTests.cs:71` | Shared runtime monitor; no per-site edit. | `using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start owner digest process.");` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliStableSlotDotnetCommandOwnershipTests.cs:113` | Source-text assertion/sample; no host creator. | `Assert.DoesNotContain("Process.Start(", handlerSource);` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CompletionBuildEvidenceLaunchTests.cs:486` | Shared runtime monitor; no per-site edit. | `Process = System.Diagnostics.Process.Start(info)!;` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorBatchLoopTestsParallelAcceptance.cs:3423` | Shared runtime monitor; no per-site edit. | `var process = Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorBatchLoopTestsSelfHandoff.cs:106` | Shared runtime monitor; no per-site edit. | `successor = Process.Start(new ProcessStartInfo("powershell")` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorBatchLoopTestsSelfHandoff.cs:1052` | Shared runtime monitor; no per-site edit. | `parent = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start parent conductor process.");` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorBatchLoopTestsSelfHandoff.cs:1150` | Shared runtime monitor; no per-site edit. | `parent = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start synthetic failed-handoff parent.");` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorLoopHandoffTests.cs:40` | Shared runtime monitor; no per-site edit. | `successor = Process.Start(startInfo)!;` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorLoopHandoffTests.cs:137` | Shared runtime monitor; no per-site edit. | `successor = Process.Start(new ProcessStartInfo("powershell")` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorLoopHandoffTests.cs:240` | Shared runtime monitor; no per-site edit. | `successor = Process.Start(new ProcessStartInfo("powershell")` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorStartHandleIsolationTests.cs:148` | Shared runtime monitor; no per-site edit. | `Caller = Process.Start(start) ?? throw new InvalidOperationException("Caller probe did not start.");` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConfirmLegacyAcceptanceHolderSettlementScriptTests.cs:150` | Shared runtime monitor; no per-site edit. | `using var process = Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConsoleIoPreservationTests.cs:70` | Shared runtime monitor; no per-site edit. | `using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start native startup fixture.");` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConsoleIoPreservationTests.cs:154` | Shared runtime monitor; no per-site edit. | `using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start ConsoleIoProbe.");` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/DirectGitLaunchSourceGuardTests.cs:33` | Source-text assertion/sample; no host creator. | `"Process.Start(" + quote + "git" + quote + ")",` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/DispatchHostLifetimeHandoffTests.cs:843` | Shared runtime monitor; no per-site edit. | `var process = Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/DispatchHostLifetimeHandoffTests.cs:923` | Shared runtime monitor; no per-site edit. | `var process = Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/DispatchProcessHostTests.cs:1816` | Shared runtime monitor; no per-site edit. | `hostProcess = Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/DispatchProcessHostTests.cs:2465` | Shared runtime monitor; no per-site edit. | `return Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/DispatchProcessHostTests.cs:2491` | Shared runtime monitor; no per-site edit. | `return Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/DispatchProcessHostTestsDispatchJobQosOptOutTests.cs:104` | Shared runtime monitor; no per-site edit. | `var process = Process.Start(start) ?? throw new InvalidOperationException("Fixture host did not start.");` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/DispatchProcessHostTestsConductorSupervisorRealHandoff.cs:407` | Shared runtime monitor; no per-site edit. | `using var process = Process.Start(info)!;` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/DotnetBuildEnvironmentManagerTests.cs:982` | Shared runtime monitor; no per-site edit. | `return Process.Start(new ProcessStartInfo` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/DotnetBuildEnvironmentManagerTests.cs:1220` | Shared runtime monitor; no per-site edit. | `return Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/DotnetBuildEnvironmentManagerTestsIsolatedDotnetScript.cs:314` | Shared runtime monitor; no per-site edit. | `using var process = Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/DotnetBuildEnvironmentManagerTestsStableSlotArtifacts.cs:52` | Shared runtime monitor; no per-site edit. | `using var holder = Process.Start(new ProcessStartInfo` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ExcessWorkerRoundAnalysisScriptTests.cs:593` | Shared runtime monitor; no per-site edit. | `using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ExecutorBuildCheckScriptTests.cs:154` | Shared runtime monitor; no per-site edit. | `using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell did not start.");` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ExecutorToolsDigestScriptTests.cs:83` | Shared runtime monitor; no per-site edit. | `using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell did not start.");` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ExitReconcileBuildFixture.cs:88` | Shared runtime monitor; no per-site edit. | `_host = Process.Start(start) ?? throw new InvalidOperationException("Owned dispatch host did not start.");` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Fixtures/IsolatedDotnetProbe/Program.cs:30` | Separate fixture/script process; managed starts use that process runtime monitor. | `using var child = Process.Start(startInfo);` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GcEnvironmentBuildChannelTests.cs:75` | Shared runtime monitor; no per-site edit. | `using var process = Process.Start(info) ?? throw new InvalidOperationException("Probe child did not start.");` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalAcceptanceVerifierDotnetBuildSlotTests.cs:234` | Shared runtime monitor; no per-site edit. | `Assert.True(process.Start(), $"Failed to start real shard process '{args[0]}'.");` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalAcceptanceVerifierDotnetBuildSlotTests.cs:550` | Shared runtime monitor; no per-site edit. | `return System.Diagnostics.Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalCreationSummaryTests.cs:343` | Shared runtime monitor; no per-site edit. | `using var process = Process.Start(start) ?? throw new InvalidOperationException("App process did not start.");` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalLifecycleEventWriterTests.cs:526` | Shared runtime monitor; no per-site edit. | `return Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalWorktreeTests.cs:504` | Shared runtime monitor; no per-site edit. | `return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start long-running helper process.");` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalWorktreeTests.cs:536` | Shared runtime monitor; no per-site edit. | `using var process = Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalWorktreeTests.cs:863` | Shared runtime monitor; no per-site edit. | `using var process = Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalWorktreeTests.cs:2525` | Shared runtime monitor; no per-site edit. | `using var process = Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalWorktreeTestsRemoveCleanupProcessProtection.cs:232` | Shared runtime monitor; no per-site edit. | `started = process.Start();` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalWorktreeTestsSqliteTooling.cs:260` | Shared runtime monitor; no per-site edit. | `using var target = Process.Start(targetStartInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalWorktreeTestsSqliteTooling.cs:1283` | Shared runtime monitor; no per-site edit. | `using var process = Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GraphRepairAdmissionTests.cs:171` | Shared runtime monitor; no per-site edit. | `return Process.Start(start)!;` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/HandleListPipeInheritanceStressTests.cs:62` | Shared runtime monitor; no per-site edit. | `case "raw": managedHolder = Process.Start(info)!; break;` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/HandleListPipeInheritanceStressTests.cs:125` | Shared runtime monitor; no per-site edit. | `child = Process.Start(info);` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/HandleListPipeInheritanceReproductionTests.cs:26` | Shared runtime monitor; no per-site edit. | `holder = Process.Start(Probes.Probe(root, "wait", releaseName))!;` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/IcaclsIntegrityLabelerTestsCommand.cs:23` | Shared runtime monitor; no per-site edit. | `return Process.Start(info);` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/IcaclsIntegrityLabelerTestsRecursivePath.cs:36` | Shared runtime monitor; no per-site edit. | `return Process.Start(info);` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/LineEndingPolicySourceGuardTests.cs:88` | Shared runtime monitor; no per-site edit. | `Assert.True(process.Start(), $"Could not start {invocation}");` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/LaneTimingMeasurementScriptTests.cs:517` | Shared runtime monitor; no per-site edit. | `using var process = Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/LauncherScriptTests.cs:483` | Shared runtime monitor; no per-site edit. | `using var firstProcess = Process.Start(CreateStartInfo(firstInvocationPath))` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/LauncherScriptTests.cs:485` | Shared runtime monitor; no per-site edit. | `using var secondProcess = Process.Start(CreateStartInfo(secondInvocationPath))` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/LauncherScriptTests.cs:973` | Shared runtime monitor; no per-site edit. | `using var incumbent = Process.Start(new ProcessStartInfo` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/LauncherScriptTests.cs:1026` | Shared runtime monitor; no per-site edit. | `using var incumbent = Process.Start(new ProcessStartInfo` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/LauncherScriptTests.cs:2371` | Shared runtime monitor; no per-site edit. | `using var launcher = Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/LauncherScriptTests.cs:2457` | Shared runtime monitor; no per-site edit. | `using var launcher = Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/LauncherScriptTests.cs:2529` | Shared runtime monitor; no per-site edit. | `using var launcher = Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/LauncherScriptTests.cs:2595` | Shared runtime monitor; no per-site edit. | `using var bindingProbe = Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/LauncherScriptTests.cs:3071` | Shared runtime monitor; no per-site edit. | `using var process = Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/MtpTestRunnerScriptTests.cs:2059` | Shared runtime monitor; no per-site edit. | `process = Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/MtpTestRunnerScriptTests.cs:2224` | Shared runtime monitor; no per-site edit. | `if (!process.Start())` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/MtpTestRunnerScriptTestsAttributionReplayExecutionPolicy.cs:54` | Shared runtime monitor; no per-site edit. | `Assert.True(process.Start(), "PowerShell probe process did not start.");` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/MtpTestRunnerScriptTestsEnvironmentState.cs:237` | Separate fixture/script process; managed starts use that process runtime monitor. | `$started = $process.Start()` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/MtpTestRunnerScriptTestsWorkerBuildNoBuildBridge.cs:373` | Shared runtime monitor; no per-site edit. | `using var capture = TestChildProcessCapture.Start(start, start: info => child = Process.Start(info));` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OfflineAssignedAgentDispatchScenarioTests.cs:231` | Shared runtime monitor; no per-site edit. | `_capture = TestChildProcessCapture.Start(start, start: info => _process = Process.Start(info));` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OrchestratorSnapshotStatusTimeoutTests.cs:293` | Shared runtime monitor; no per-site edit. | `var child = Process.Start(info)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/PipeDrainThreadPoolSaturationTests.cs:141` | Shared runtime monitor; no per-site edit. | `Assert.True(process.Start(), "Failed to start the managed pipe-drain saturation probe.");` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/PlannerSamplingDispatchTests.cs:1512` | Shared runtime monitor; no per-site edit. | `return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start Planner sample test host.");` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/PostLandingCanaryCaptureAvailabilityTests.cs:325` | Shared runtime monitor; no per-site edit. | `var process = Process.Start(startInfo) ??` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/PowerShellLaunchSourceGuardTests.cs:41` | Source-text assertion/sample; no host creator. | `"foreach (var n in new[] { \"pwsh\" }) Process.Start(n); " +` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/PowerShellLaunchSourceGuardTests.cs:42` | Source-text assertion/sample; no host creator. | `"var shells = new[] { \"pwsh\" }; foreach (var n in shells) Process.Start(n); " +` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProcessTreeGuiSuppressionTests.cs:58` | Shared runtime monitor; no per-site edit. | `process = Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProcessTreeGuiSuppressionTests.cs:307` | Shared runtime monitor; no per-site edit. | `using var process = Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProcessTreeGuiSuppressionTestsGitCliCaptureExclusion.cs:57` | Shared runtime monitor; no per-site edit. | `var didStart = child.Start();` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProcessTreeGuiSuppressionTestsLaunchSiteCensus.cs:51` | Source-text assertion/sample; no host creator. | `"Process.Start(info); process.Start(); ProcessTreeGuiSuppression.Start(info); " +` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProcessTreeGuiSuppressionTestsHandleListIsolation.cs:69` | Shared runtime monitor; no per-site edit. | `raw = Process.Start(Support.Probe(root, "handles", releaseName))!;` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProcessTreeGuiSuppressionTestsWindowlessConsoleGuard.cs:172` | Shared runtime monitor; no per-site edit. | `host = Process.Start(info) ?? throw new InvalidOperationException("Measurement host did not start.");` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderHoldAcrossRestartScenarioTests.cs:441` | Shared runtime monitor; no per-site edit. | `var capture = TestChildProcessCapture.Start(start, start: info => process = Process.Start(info));` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderHoldFallbackScenarioTests.cs:310` | Shared runtime monitor; no per-site edit. | `_capture = TestChildProcessCapture.Start(start, start: info => _process = Process.Start(info));` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderSignInFailoverScenarioTests.cs:286` | Shared runtime monitor; no per-site edit. | `var capture = TestChildProcessCapture.Start(start, start: info => process = Process.Start(info));` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderUsageHoldEventsScenarioTests.cs:292` | Shared runtime monitor; no per-site edit. | `var capture = TestChildProcessCapture.Start(start, start: info => process = Process.Start(info));` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderUsageHoldSelfRetryScenarioTests.cs:376` | Shared runtime monitor; no per-site edit. | `var capture = TestChildProcessCapture.Start(start, start: info => process = Process.Start(info));` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderUsageSelfHealScenarioTests.cs:367` | Shared runtime monitor; no per-site edit. | `var capture = TestChildProcessCapture.Start(start, start: info => process = Process.Start(info));` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/RemoteExecutorToolsDeployScriptTests.cs:230` | Shared runtime monitor; no per-site edit. | `using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell contract process did not start.");` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/RepositorySourceInventoryTests.cs:126` | Shared runtime monitor; no per-site edit. | `using var process = Process.Start(new ProcessStartInfo` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/StorageRetention/StorageRetentionMaintenanceTests.cs:836` | Shared runtime monitor; no per-site edit. | `contender.Start();` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/TestChildProcessCaptureTests.cs:34` | Shared runtime monitor; no per-site edit. | `start: startInfo => owned = Process.Start(startInfo),` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/TestChildProcessCaptureTests.cs:79` | Shared runtime monitor; no per-site edit. | `start: startInfo => owned = Process.Start(startInfo),` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/TestChildProcessCaptureTests.cs:306` | Shared runtime monitor; no per-site edit. | `start: info => owned = Process.Start(info),` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/TestChildProcessCaptureTests.cs:341` | Shared runtime monitor; no per-site edit. | `start: info => owned = Process.Start(info),` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/TrialCompareCliCommandTests.cs:520` | Shared runtime monitor; no per-site edit. | `using var process = System.Diagnostics.Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WindowsProcessCurrentDirectoryReaderTests.cs:44` | Shared runtime monitor; no per-site edit. | `Assert.True(child.Start(), "Owned child shell did not start.");` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerBuildCheckFreeSlotTests.cs:72` | Shared runtime monitor; no per-site edit. | `var process = Process.Start(info);` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerBuildCheckRunHoldTests.cs:64` | Shared runtime monitor; no per-site edit. | `var process = Process.Start(info);` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerBuildProjectScopeProcessSpawningTests.cs:79` | Shared runtime monitor; no per-site edit. | `var process = Process.Start(info);` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerBuildPipeBusyRetryTests.cs:138` | Shared runtime monitor; no per-site edit. | `_process = Process.Start(start) ?? throw new InvalidOperationException("Pipe client process did not start.");` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerBuildRequestRecoveryTests.cs:30` | Shared runtime monitor; no per-site edit. | `host = Process.Start(start);` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerContextPackageTests.cs:332` | Shared runtime monitor; no per-site edit. | `using var process = Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerContextPackageTests.cs:2333` | Shared runtime monitor; no per-site edit. | `using var process = Process.Start(new ProcessStartInfo` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchJobAccountingTests.cs:222` | Shared runtime monitor; no per-site edit. | `wrapper = Process.Start(new ProcessStartInfo` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTests.cs:1781` | Shared runtime monitor; no per-site edit. | `using var process = Process.Start(startInfo)` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTests.cs:2617` | Shared runtime monitor; no per-site edit. | `return Process.Start(new ProcessStartInfo` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTestsDispatchPreparation.cs:1255` | Shared runtime monitor; no per-site edit. | `return Process.Start(new ProcessStartInfo` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTestsDispatchPreparation.cs:1633` | Shared runtime monitor; no per-site edit. | `return Process.Start(startInfo);` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTestsDispatchPreparation.cs:1711` | Shared runtime monitor; no per-site edit. | `return Process.Start(new ProcessStartInfo` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTestsDispatchPreparation.cs:1811` | Shared runtime monitor; no per-site edit. | `spawned = Process.Start(new ProcessStartInfo` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTestsDispatchPreparation.cs:1921` | Shared runtime monitor; no per-site edit. | `spawned = Process.Start(new ProcessStartInfo` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTestsRetryStartGoalEvents.cs:145` | Shared runtime monitor; no per-site edit. | `spawned = Process.Start(gatedStart) ?? throw new InvalidOperationException("Fixture process did not start.");` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTestsSeededRepositoryFactoryTests.cs:905` | Shared runtime monitor; no per-site edit. | `return process.Start();` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTestsSeededRepositoryFactoryTests.cs:1034` | Shared runtime monitor; no per-site edit. | `startProcess: nativeCapture ? null : process => process.Start(),` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTestsSeededRepositoryFactoryTests.cs:1244` | Shared runtime monitor; no per-site edit. | `startProcess: nativeCapture ? null : process => process.Start(),` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerProcessJobsTests.cs:841` | Shared runtime monitor; no per-site edit. | `wrapper = Process.Start(new ProcessStartInfo` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerProcessJobsTests.cs:953` | Shared runtime monitor; no per-site edit. | `using var exited = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c exit 0")` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerProcessJobsTests.cs:1394` | Shared runtime monitor; no per-site edit. | `wrapper = Process.Start(new ProcessStartInfo` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerProcessJobsTests.cs:1476` | Shared runtime monitor; no per-site edit. | `using var exited = Process.Start(new ProcessStartInfo` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerProcessJobsTests.cs:1626` | Shared runtime monitor; no per-site edit. | `oldFast = Process.Start(new ProcessStartInfo` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerProcessJobsTests.cs:2032` | Shared runtime monitor; no per-site edit. | `wrapper = Process.Start(new ProcessStartInfo` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerProcessJobsTests.cs:2585` | Shared runtime monitor; no per-site edit. | `wrapper = Process.Start(new ProcessStartInfo` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerProcessJobsTests.cs:2819` | Shared runtime monitor; no per-site edit. | `var process = Process.Start(new ProcessStartInfo` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerProcessJobsTests.cs:2959` | Shared runtime monitor; no per-site edit. | `wrapper = Process.Start(new ProcessStartInfo` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerProcessJobsTests.cs:3003` | Shared runtime monitor; no per-site edit. | `wrapper = Process.Start(new ProcessStartInfo` |
| `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTestsWorkerResultClassification.cs:352` | Shared runtime monitor; no per-site edit. | `spawned = Process.Start(new ProcessStartInfo` |

## Deferred evidence

`HandleListPipeInheritanceReproductionTests.ChildExited_RawHolderStillAlive_BothDrainsReachEof` coordinates a raw holder on another thread inside the creation observer. On base, the parent child ends are still inheritable until the outer finally after that observer returns. The raw holder therefore inherits both write ends and keeps them until the named release event; it cannot be released before the 2000 ms EOF check. Both drains read all output and then remain running. After this change those copies close before the observer, so the holder cannot retain them.

Baseline execution was NOT performed (Developer may not execute tests). This is a source-based failure demonstration, not an observed RED receipt. Acceptance can apply only `HandleListPipeInheritanceReproductionTests.cs` to base 17c3db0f6: its nested support uses only pre-existing APIs.

`HandleListPipeInheritanceStressTests.ConcurrentPairs_UnrelatedChildRemainsAlive_OutputReachesEof` covers 100 pairs each of launcher/raw, launcher/legacy locked, contained redirected/raw, and launcher/launcher. Each pair starts at a barrier; output exit is gated on stdin until both creators return, while the unrelated child remains alive behind a named event through both EOF checks. Both children have async drains, exact output/exit assertions, and bounded cleanup. `RuntimeLock_RawStartWaitsForRelease` asserts object identity with the runtime field and observes a raw start waiting on that monitor until release.

Review receipt `fe-382c4fa2` on candidate `41866a40f` recorded contained/raw failing at the zero-exit assertion with "Process was not started by this object". The contained `Process` comes from `OwnedProcessGroup.cs:909` via `GetProcessById`. The stress test now acquires and validates `Process.SafeHandle` at `HandleListPipeInheritanceStressTests.cs:47` before resuming the suspended child, retaining its identity for exit observation. The zero-exit assertion at `:86`, both EOF checks and all 100 pairs remain required. This correction has not been test-executed by the Developer; Acceptance owns the rerun.

Tests and full-suite/window-observer checks are deferred to the conductor. Build results are reported in WORKER_RESULT. Watching ten later gates for drain timeouts remains operator follow-up, outside this implementation slice.
