using System.Collections.Concurrent;
using System.Diagnostics;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class DotnetBuildOwnedRunRootRegistration
{
    private static readonly Func<DotnetBuildStorageRoot, IOwnedRunRootRegistrar?> DefaultOwnedRunRootRegistrarFactory =
        CreateDefaultOwnedRunRootRegistrar;
    private static readonly ConcurrentDictionary<string, IOwnedRunRootRegistrar> OwnedRunRootRegistrarsForTests =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly OwnedRunRootWriteFailureBuffer OwnedRunRootWriteFailures = new();
    internal static readonly AsyncLocal<Func<DotnetBuildStorageRoot, IOwnedRunRootRegistrar?>?> OwnedRunRootRegistrarFactoryOverride = new();
    private static Func<DotnetBuildStorageRoot, IOwnedRunRootRegistrar?> OwnedRunRootRegistrarFactory =>
        OwnedRunRootRegistrarFactoryOverride.Value ?? DefaultOwnedRunRootRegistrarFactory;

    internal static IOwnedRunRootRegistrar? RegisterOwnedRunRoot(
        DotnetBuildStorageRoot storageRoot,
        string root,
        OwnedRunRootPurpose purpose)
    {
        var canonicalRoot = Path.GetFullPath(root);
        try
        {
            var registrar = ResolveOwnedRunRootRegistrar(storageRoot);
            if (registrar is null)
            {
                EnqueueOwnedRunRootWriteFailure(canonicalRoot,
                    $"owned-root registration unavailable; created as unregistered path={canonicalRoot}");
                return null;
            }

            using var process = Process.GetCurrentProcess();
            if (!SpawnProcessIdentityReader.TryReadForRegistration(process, out var owner))
            {
                throw new InvalidOperationException(
                    $"Cannot register owned build run root '{canonicalRoot}' because the owner process identity is unavailable.");
            }

            registrar.Register(
                canonicalRoot,
                purpose,
                owner,
                goalId: null,
                dispatchId: null,
                DotnetBuildEnvironmentManager.OwnedRunRootTimeProvider.GetUtcNow());
            return registrar;
        }
        catch (Exception ex)
        {
            EnqueueOwnedRunRootWriteFailure(canonicalRoot,
                $"owned-root registration failed; created as unregistered path={canonicalRoot} error={ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    internal static IOwnedRunRootRegistrar? SetOwnedRunRootRegistrarForTests(
        DotnetBuildStorageRoot storageRoot,
        IOwnedRunRootRegistrar? registrar)
    {
        ArgumentNullException.ThrowIfNull(storageRoot);
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storageRoot.RootPath));
        OwnedRunRootRegistrarsForTests.TryGetValue(key, out var prior);
        if (registrar is null)
            OwnedRunRootRegistrarsForTests.TryRemove(key, out _);
        else
            OwnedRunRootRegistrarsForTests[key] = registrar;
        return prior;
    }

    private static IOwnedRunRootRegistrar? ResolveOwnedRunRootRegistrar(DotnetBuildStorageRoot storageRoot)
    {
        var factory = OwnedRunRootRegistrarFactory;
        try
        {
            if (!ReferenceEquals(factory, DefaultOwnedRunRootRegistrarFactory))
                return factory(storageRoot);

            var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storageRoot.RootPath));
            return OwnedRunRootRegistrarsForTests.TryGetValue(key, out var registrar)
                ? registrar
                : factory(storageRoot);
        }
        catch
        {
            return null;
        }
    }

    internal static void RecordOwnedRunRootRelease(
        DotnetBuildEnvironment environment,
        OwnedRunRootReleaseOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (environment.OwnedRootRegistrar is not { } registrar)
            return;

        try
        {
            registrar.Release(
                environment.RootPath,
                outcome,
                DotnetBuildEnvironmentManager.OwnedRunRootTimeProvider.GetUtcNow());
        }
        catch (Exception ex)
        {
            EnqueueOwnedRunRootWriteFailure(environment.RootPath,
                $"owned-root release persistence failed path={environment.RootPath} outcome={outcome} error={ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void EnqueueOwnedRunRootWriteFailure(string runRootPath, string message)
    {
        var storageRootPath = Path.GetDirectoryName(Path.GetDirectoryName(runRootPath)!)!;
        OwnedRunRootWriteFailures.Enqueue(storageRootPath, message);
    }

    internal static IReadOnlyList<string> DrainOwnedRunRootWriteFailures(
        int maxCount, DotnetBuildStorageRoot? storageRoot = null)
    {
        return OwnedRunRootWriteFailures.Drain(maxCount, storageRoot?.RootPath);
    }

    private static IOwnedRunRootRegistrar? CreateDefaultOwnedRunRootRegistrar(DotnetBuildStorageRoot _)
    {
        var repositoryRoot = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_REPOSITORY_ROOT");
        if (string.IsNullOrWhiteSpace(repositoryRoot))
            repositoryRoot = Directory.GetCurrentDirectory();

        var stateDbPath = Path.Combine(Path.GetFullPath(repositoryRoot), ".orchestrator", "state.db");
        if (!File.Exists(stateDbPath) || !StateDbMigrations.IsUpToDate(stateDbPath))
            return null;

        return new OwnedRunRootRegistry(stateDbPath);
    }

    internal static void TryRecordOwnedRunRoot(
        IOwnedRunRootRegistrar? registrar,
        string canonicalPath,
        string operation,
        Action<IOwnedRunRootRegistrar> write)
    {
        if (registrar is null)
            return;

        try
        {
            write(registrar);
        }
        catch (Exception ex)
        {
            EnqueueOwnedRunRootWriteFailure(canonicalPath,
                $"owned-root cleanup persistence failed path={canonicalPath} operation={operation} error={ex.GetType().Name}: {ex.Message}");
        }
    }
}
