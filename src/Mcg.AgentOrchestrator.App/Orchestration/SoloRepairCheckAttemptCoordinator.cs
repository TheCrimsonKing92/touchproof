using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record SoloRepairCheckAttempt(Guid LaunchId, SoloRepairCheckRequest Request,
    string StateDirectory, string ExecutionDirectory, string IntegrationBranch, string? ProjectName,
    SpawnProcessIdentity? Owner = null, string Outcome = "running", string? Detail = null);

internal sealed class SoloRepairCheckAttemptCoordinator(
    OrchestratorWorkspace workspace,
    DotnetBuildStorageRoot buildStorageRoot,
    Func<string, Guid, SpawnProcessIdentity?>? launch = null,
    Func<SpawnProcessIdentity, SpawnTrackedProcessStatus>? classifyHolder = null)
{
    internal const string OwnedProcessSubcommandName = "__solo-repair-check-attempt";
    private const string MetadataFile = "attempt.json";
    private readonly string _root = Path.Combine(workspace.OrchestratorDirectory, "solo-repair-check-attempts");

    internal void StartOrObserve(SoloBaselineRepairStore store, SoloRepairCheckRequest request)
    {
        var directory = Path.Combine(_root, SoloBaselineRepairStore.Key(workspace.SqliteStatePath, request.Key));
        using var writer = StorageRetentionMaintenance.TryAcquireAttemptWriterLease(directory, TimeSpan.Zero);
        if (writer is null) return;

        var path = Path.Combine(directory, MetadataFile);
        var prior = Read(path);
        if (HasLiveOwner(prior) || store.ReadVerifiedCheck(request.Key, request.ProofIdentity) is not null)
            return;
        if (prior is { Outcome: "failed" } && prior.Request.ProofIdentity == request.ProofIdentity)
            throw new InvalidOperationException($"Solo repair check {request.Key} failed to execute. {prior.Detail}");

        using var execution = SoloRepairCheckRunner.OpenOwnership(workspace.SqliteStatePath, request.Key);
        if (!SoloRepairCheckRunner.TryTakeOwnership(execution)) return;
        execution.ReleaseMutex();

        Directory.CreateDirectory(directory);
        var attempt = new SoloRepairCheckAttempt(Guid.NewGuid(), request, workspace.OrchestratorDirectory,
            workspace.ExecutionDirectory, workspace.IntegrationBranch, OwnedChildWorkspaceResolver.RecordedProjectName(workspace));
        Save(path, attempt);
        SpawnProcessIdentity? identity;
        try
        {
            identity = (launch ?? LaunchOwnedProcess)(path, attempt.LaunchId);
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            Save(path, attempt with { Outcome = "launch-failed", Detail = ex.Message });
            return;
        }
        if (identity is not null) Save(path, attempt with { Owner = identity });
    }

    internal int CountLiveAttempts()
    {
        if (!Directory.Exists(_root)) return 0;
        return Directory.EnumerateDirectories(_root).Count(directory =>
        {
            using var writer = StorageRetentionMaintenance.TryAcquireAttemptWriterLease(directory, TimeSpan.Zero);
            return writer is null || HasLiveOwner(Read(Path.Combine(directory, MetadataFile)));
        });
    }

    private bool HasLiveOwner(SoloRepairCheckAttempt? attempt) =>
        attempt is { Outcome: "running", Owner: { } owner } &&
        (classifyHolder?.Invoke(owner) ??
            AcceptanceHolderIdentity.Classify(owner.ProcessId, owner.StartedAt, owner.ImagePath))
            != SpawnTrackedProcessStatus.DeadOrRecycled;

    internal static int RunOwnedProcess(string metadataPath, Guid launchId)
    {
        try
        {
            return RunAttempt(metadataPath, launchId, attempt => RunCheck(metadataPath, attempt),
                AcceptanceHolderIdentity.Capture(Environment.ProcessId));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    internal static int RunAttempt(string metadataPath, Guid launchId,
        Func<SoloRepairCheckAttempt, SoloRepairCheckReceipt> execute, SpawnProcessIdentity? owner = null)
    {
        // A child must still own the published launch when it acquires the writer lease.
        // This also fences a child created just before its parent died recording the PID.
        using var writer = StorageRetentionMaintenance.AcquireAttemptWriterLease(Path.GetDirectoryName(metadataPath)!);
        if (Read(metadataPath) is not { Outcome: "running" } attempt || attempt.LaunchId != launchId)
            return 0;
        if (owner is not null)
        {
            attempt = attempt with { Owner = owner };
            Save(metadataPath, attempt);
        }

        SoloRepairCheckReceipt receipt;
        try
        {
            receipt = execute(attempt);
        }
        catch (Exception ex)
        {
            Save(metadataPath, attempt with { Outcome = "failed", Detail = ex.Message });
            return 1;
        }
        Save(metadataPath, attempt with { Outcome = receipt.Outcome == "pending" ? "deferred" : "completed" });
        return 0;
    }

    private static SoloRepairCheckReceipt RunCheck(string metadataPath, SoloRepairCheckAttempt attempt)
    {
        // The conductor can restart while this process is running. Keep verifier output
        // independent of the parent's startup pipes.
        var directory = Path.GetDirectoryName(metadataPath)!;
        using var stdout = File.AppendText(Path.Combine(directory, "stdout.log"));
        using var stderr = File.AppendText(Path.Combine(directory, "stderr.log"));
        var originalOut = Console.Out;
        var originalError = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var childWorkspace = OwnedChildWorkspaceResolver.ForSoloRepairCheckAttempt(attempt);
            using var heartbeatOwner = GateHeartbeatOwner.Push(childWorkspace.OrchestratorDirectory);
            var storageRoot = DotnetBuildEnvironmentManager.CaptureStorageRoot();
            var verifier = new GoalAcceptanceVerifier(CrosswalkExecutableComposition.ForProcess, storageRoot,
                childWorkspace.OrchestratorDirectory);
            var store = new SoloBaselineRepairStore(Path.Combine(childWorkspace.OrchestratorDirectory,
                SoloBaselineRepairStore.FileName));
            return SoloRepairCheckRunner.Run(childWorkspace, store, verifier, (key, token) =>
                DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                    DotnetBuildEnvironmentManager.DefaultSlotBusyPollTimeout, cancellationToken: token,
                    slotCount: DotnetBuildEnvironmentManager.StableSlotCount, storageRoot: storageRoot,
                    claimant: new BuildPermitClaimant("gate:" + key, BuildPermitClaimantKind.Gate,
                        Environment.ProcessId, OwnerBound: false)), attempt.Request);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private SpawnProcessIdentity? LaunchOwnedProcess(string metadataPath, Guid launchId)
    {
        var executable = Environment.ProcessPath ?? "dotnet";
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workspace.ExecutionDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(SoloRepairCheckAttemptCoordinator).Assembly.Location);
        info.ArgumentList.Add(OwnedProcessSubcommandName);
        info.ArgumentList.Add(metadataPath);
        info.ArgumentList.Add(launchId.ToString("D"));
        AcceptanceHermeticEnvironment.ConfigureHermeticVerificationEnvironment(info.Environment, workspace.ExecutionDirectory);
        CrosswalkExecutableComposition.PropagateDataRoot(info.Environment);
        BuildGcEnvironment.Apply(info.Environment, BuildGcEnvironment.ForStatePath(workspace.SqliteStatePath));
        info.Environment[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] = buildStorageRoot.RootPath;

        var process = HandleListProcessLauncher.StartSuppressed(info);
        var identity = AcceptanceHolderIdentity.Capture(process.Id);
        _ = DrainAndReleaseAsync(process);
        return identity;
    }

    private static async Task DrainAndReleaseAsync(HandleListLaunchedProcess process)
    {
        using (process)
        {
            try
            {
                process.StandardInput.Close();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception)
            {
                // The child owns execution and publishes its terminal status in metadata.
            }
        }
    }

    private static SoloRepairCheckAttempt? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize<SoloRepairCheckAttempt>(stream);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return null;
        }
    }

    private static void Save(string path, SoloRepairCheckAttempt attempt)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(attempt));
        File.Move(temp, path, overwrite: true);
    }
}
