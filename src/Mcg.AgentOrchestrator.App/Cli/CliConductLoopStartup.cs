using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliConductLoopStartup
{
    internal static PreTesterResolverObservationWindow? CreateResolverObservation(
        IReadOnlyList<string> parts, bool noDispatch, OrchestratorWorkspace workspace,
        Func<int, int?>? readParentProcessId = null)
    {
        var supervisedChild = parts.Contains(ConductorContinuitySupervisor.ChildFlag, StringComparer.OrdinalIgnoreCase);
        if (!supervisedChild || noDispatch)
            return null;
        var prefix = ConductorContinuitySupervisor.ResolverObservationHandoffPrefix;
        var checkpoint = parts.FirstOrDefault(part => part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
        var launch = ConductorResolverObservationLaunch.Claim(
            workspace.OrchestratorDirectory,
            CliCommandHandlers.GetFlagValue(parts, ConductorContinuitySupervisor.ExitArtifactFlag), checkpoint, readParentProcessId);
        if (launch is null)
        {
            if (checkpoint is not null)
                Console.WriteLine("PRE_TESTER_RESOLVER_WINDOW status=continuation-unverified");
            return null;
        }
        return PreTesterResolverObservationWindow.ForConductLoop(workspace, launch);
    }

    internal static ConductLoopHandoffOptions CreateHandoffOptions(CliExecutionContext context,
        IReadOnlyList<string> parts, string stopFilePath) => new(
        Args: parts.ToArray(),
        ExecutionDirectory: context.Workspace.ExecutionDirectory,
        OrchestratorDirectory: context.Workspace.OrchestratorDirectory,
        LogDirectory: context.Workspace.LogDirectory,
        RunEventStorePath: context.Workspace.RunEventStorePath,
        StopFilePath: stopFilePath,
        RenewalCount: ConductorLoopHandoff.ParseRenewalCount(parts),
        MaxRenewals: ConductorLoopHandoff.DefaultMaxRenewalsWithoutLanding,
        ReleaseCurrentLease: context.ReleaseConductLoopLease,
        ReacquireCurrentLease: context.ReacquireConductLoopLease,
        StopFailedSuccessor: ConductorLoopHandoff.StopFailedSuccessor);

    internal static void PrintConductorPolicyWarnings(ConductorPolicyResolution resolution)
    {
        foreach (var warning in resolution.Warnings)
        {
            Console.WriteLine($"[conduct] WARNING: {warning}");
        }
    }

    internal static ConductorPolicyResolution? PrepareConductLoopPolicy(
        string orchestratorDirectory,
        string? policyName,
        TextWriter error,
        Func<string, IReadOnlyList<StoreSetupResult>>? storeSetup = null,
        string? projectPolicyTemplatePath = null,
        TextWriter? seedOutput = null)
    {
        try
        {
            ConductorLoopStoreSetup.Run(orchestratorDirectory, storeSetup);
        }
        catch (InvalidOperationException ex)
        {
            error.WriteLine($"Error: {ex.Message}");
            return null;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Warning: store setup deferred: {ex.Message}");
        }

        if (projectPolicyTemplatePath is not null)
        {
            var seeded = ProjectConductorPolicySeeder.Seed(projectPolicyTemplatePath, orchestratorDirectory);
            var dropped = seeded.DroppedKeys.Count == 0 ? "none" : string.Join(",", seeded.DroppedKeys);
            var line = seeded.Outcome switch
            {
                ProjectConductorPolicySeeder.Outcome.Seeded =>
                    $"[conduct] POLICY_SEEDED policy={seeded.PolicyName} template={projectPolicyTemplatePath} path={seeded.WrittenPath} dropped={dropped}",
                ProjectConductorPolicySeeder.Outcome.TemplateMissing =>
                    $"[conduct] POLICY_SEED_SKIPPED reason=template-missing template={projectPolicyTemplatePath} detail={seeded.Detail.ReplaceLineEndings(" ")}",
                ProjectConductorPolicySeeder.Outcome.TemplateInvalid =>
                    $"[conduct] POLICY_SEED_SKIPPED reason=template-invalid template={projectPolicyTemplatePath} detail={seeded.Detail.ReplaceLineEndings(" ")}",
                _ => null
            };
            if (line is not null)
                (seedOutput ?? Console.Out).WriteLine(line);
        }

        try
        {
            return ResolveConductorPolicy(policyName, orchestratorDirectory);
        }
        catch (FormatException ex)
        {
            var policyPath = Path.GetFullPath(Path.Combine(orchestratorDirectory, "conductor-policy.json"));
            var snapshotPath = ConductorPolicyLastValidSnapshot.PathFor(orchestratorDirectory);
            var snapshot = ConductorPolicyLastValidSnapshot.TryRead(orchestratorDirectory, out var failure);
            var fallback = snapshot is null ? "Conservative" : "last-valid";
            var warning = $"POLICY_INVALID path={policyPath} fallback={fallback} snapshot={snapshotPath} " +
                $"message={ex.Message.ReplaceLineEndings(" ")}";
            if (failure is not null)
                warning += $" snapshotUnavailable={failure}";
            return new ConductorPolicyResolution(snapshot ?? ConductorAutonomyPolicy.Conservative,
                snapshot is null ? "fallback:Conservative" : $"last-valid:{snapshotPath}", [warning]);
        }
    }

    internal static ConductorPolicyResolution ResolveConductorPolicy(string? presetName, string orchestratorDirectory)
    {
        var policyPath = Path.GetFullPath(Path.Combine(orchestratorDirectory, "conductor-policy.json"));
        if (presetName is not null)
        {
            var preset = ConductorAutonomyPolicy.All.FirstOrDefault(
                policy => policy.Name.Equals(presetName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Unknown conductor policy '{presetName}'. Valid: {string.Join(", ", ConductorAutonomyPolicy.All.Select(policy => policy.Name))}");
            var warnings = new List<string>();
            try
            {
                var filePolicy = ConductorAutonomyPolicy.LoadFromOrchestratorDirectory(new DirectoryInfo(orchestratorDirectory));
                if (!ReferenceEquals(filePolicy, ConductorAutonomyPolicy.Default))
                {
                    var snapshotWarning = ConductorPolicyLastValidSnapshot.Write(orchestratorDirectory, filePolicy);
                    if (snapshotWarning is not null)
                        warnings.Add(snapshotWarning);
                    var differences = DescribeConductorPolicyDifferences(preset, filePolicy);
                    if (differences.Count > 0)
                    {
                        warnings.Add(
                            $"Policy file '{policyPath}' (policy '{filePolicy.Name}') disagrees with explicit --policy {preset.Name}; " +
                            $"--policy {preset.Name} wins and will be applied to the whole run. Differences: {string.Join("; ", differences)}.");
                    }
                }
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                warnings.Add(
                    $"Policy file '{policyPath}' is invalid and was ignored because --policy {preset.Name} was supplied: {ex.Message}");
            }

            return new ConductorPolicyResolution(preset, "preset", warnings);
        }

        var loaded = ConductorAutonomyPolicy.LoadFromOrchestratorDirectory(new DirectoryInfo(orchestratorDirectory));
        if (ReferenceEquals(loaded, ConductorAutonomyPolicy.Default))
        {
            return new ConductorPolicyResolution(loaded, "default", []);
        }

        var fileWarnings = new List<string>();
        var highestPresetWorkerCap = ConductorAutonomyPolicy.All.Max(policy => policy.MaxConcurrentPaidWorkers);
        if (loaded.MaxConcurrentPaidWorkers > ConductorBatchLoop.WorkerAdmissionCapacity)
        {
            fileWarnings.Add(
                $"Policy file '{policyPath}' sets maxConcurrentPaidWorkers={loaded.MaxConcurrentPaidWorkers}, " +
                $"above worker admission capacity {ConductorBatchLoop.WorkerAdmissionCapacity}; configured concurrency will be clamped " +
                $"to {ConductorBatchLoop.WorkerAdmissionCapacity} normally and " +
                $"{Math.Max(0, ConductorBatchLoop.WorkerAdmissionCapacity - 1)} while a gate-ready goal reserves one admission slot.");
        }
        else if (loaded.MaxConcurrentPaidWorkers > highestPresetWorkerCap)
        {
            fileWarnings.Add(
                $"Policy file '{policyPath}' sets maxConcurrentPaidWorkers={loaded.MaxConcurrentPaidWorkers}, " +
                $"above the highest preset value {highestPresetWorkerCap}; honoring the configured value.");
        }

        var writeWarning = ConductorPolicyLastValidSnapshot.Write(orchestratorDirectory, loaded);
        if (writeWarning is not null)
            fileWarnings.Add(writeWarning);
        return new ConductorPolicyResolution(loaded, $"file:{policyPath}", fileWarnings);
    }

    private static IReadOnlyList<string> DescribeConductorPolicyDifferences(
        ConductorAutonomyPolicy preset,
        ConductorAutonomyPolicy file)
    {
        var differences = new List<string>();

        void Add<T>(string field, T presetValue, T fileValue)
        {
            if (!EqualityComparer<T>.Default.Equals(presetValue, fileValue))
            {
                differences.Add($"{field}: preset={Format(presetValue)} file={Format(fileValue)}");
            }
        }

        static string Format<T>(T value) => value is null ? "<null>" : value.ToString() ?? "<null>";

        Add(nameof(ConductorAutonomyPolicy.Name), preset.Name, file.Name);
        Add(nameof(ConductorAutonomyPolicy.MaxConcurrentPaidWorkers), preset.MaxConcurrentPaidWorkers, file.MaxConcurrentPaidWorkers);
        Add(nameof(ConductorAutonomyPolicy.MaxCriterionRetries), preset.MaxCriterionRetries, file.MaxCriterionRetries);
        Add(nameof(ConductorAutonomyPolicy.AutoPromoteRiskThreshold), preset.AutoPromoteRiskThreshold, file.AutoPromoteRiskThreshold);
        Add(nameof(ConductorAutonomyPolicy.MaxEmptyOutputDispatchRetries), preset.MaxEmptyOutputDispatchRetries, file.MaxEmptyOutputDispatchRetries);
        Add(nameof(ConductorAutonomyPolicy.MaxEmptyOutputAutoRecoverCycles), preset.MaxEmptyOutputAutoRecoverCycles, file.MaxEmptyOutputAutoRecoverCycles);
        Add(nameof(ConductorAutonomyPolicy.EmptyOutputRetryInitialDelaySeconds), preset.EmptyOutputRetryInitialDelaySeconds, file.EmptyOutputRetryInitialDelaySeconds);
        Add(nameof(ConductorAutonomyPolicy.EmptyOutputRetryBackoffMultiplier), preset.EmptyOutputRetryBackoffMultiplier, file.EmptyOutputRetryBackoffMultiplier);
        Add(nameof(ConductorAutonomyPolicy.EmptyOutputRetryMaxDelaySeconds), preset.EmptyOutputRetryMaxDelaySeconds, file.EmptyOutputRetryMaxDelaySeconds);
        Add(nameof(ConductorAutonomyPolicy.ReviewAutoRetryWarningRound), preset.ReviewAutoRetryWarningRound, file.ReviewAutoRetryWarningRound);
        Add(nameof(ConductorAutonomyPolicy.ReviewAutoRetryStopRound), preset.ReviewAutoRetryStopRound, file.ReviewAutoRetryStopRound);
        Add(nameof(ConductorAutonomyPolicy.ReviewAutoRetryLifetimeMultiplier), preset.ReviewAutoRetryLifetimeMultiplier, file.ReviewAutoRetryLifetimeMultiplier);
        Add(nameof(ConductorAutonomyPolicy.PlannerSampleCount), preset.PlannerSampleCount, file.PlannerSampleCount);
        foreach (var state in Enum.GetValues<GoalLifecycleState>())
        {
            Add($"{nameof(ConductorAutonomyPolicy.TransitionMap)}[{state}]", preset.TransitionMap[state], file.TransitionMap[state]);
        }

        return differences;
    }
}
