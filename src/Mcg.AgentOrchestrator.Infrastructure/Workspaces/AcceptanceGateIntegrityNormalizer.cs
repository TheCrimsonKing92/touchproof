namespace Mcg.AgentOrchestrator.Infrastructure;

// A gate owns the workspace after worker dispatch has ended. Worker preparation restores Low
// on the next writable dispatch; the gate must not restore it while its checks are running.
internal static class AcceptanceGateIntegrityNormalizer
{
    internal const string MediumInheritableLevel = "(OI)(CI)M";
    private static readonly AsyncLocal<IWorkerIntegrityLabeler?> IntegrityLabelerOverrideForTests = new();

    internal static IDisposable PushIntegrityLabelerForTests(IWorkerIntegrityLabeler labeler)
    {
        ArgumentNullException.ThrowIfNull(labeler);
        var previous = IntegrityLabelerOverrideForTests.Value;
        IntegrityLabelerOverrideForTests.Value = labeler;
        return new RestoreAction(() => IntegrityLabelerOverrideForTests.Value = previous);
    }

    internal static void Normalize(string path, IWorkerIntegrityLabeler? labeler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        labeler ??= IntegrityLabelerOverrideForTests.Value;
        if (labeler is null && !OperatingSystem.IsWindows())
            return;

        labeler ??= new IcaclsIntegrityLabeler();
        try
        {
            SetAndConfirm(path, recursive: false, directory: true, labeler);
            // Existing build outputs can have explicit Low labels even after the root changes.
            // Keep worker-private scratch and Git metadata under their existing owners.
            foreach (var child in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                if (child.Name.Equals(".mcg-sandbox", StringComparison.OrdinalIgnoreCase) ||
                    child.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                    continue;

                var directory = (child.Attributes & FileAttributes.Directory) != 0;
                SetAndConfirm(child.FullName, recursive: directory, directory, labeler);
            }
        }
        catch (AcceptanceGateIntegrityPreparationException) { throw; }
        catch (Exception error)
        {
            throw new AcceptanceGateIntegrityPreparationException(path, error);
        }
    }

    internal static void PrepareBuildArtifacts(
        DotnetBuildEnvironment environment, IAcceptanceRunExecutionContext? context)
    {
        // Derived baseline artifact paths are allocated lazily, before their first build.
        Directory.CreateDirectory(environment.ArtifactsPath);
        while (context is AcceptanceRunExecutionContextView view)
            context = view.Owner;
        if (context is AcceptanceRunExecutionOwner owner)
            owner.PrepareBuildArtifactIntegrity(environment.ArtifactsPath);
        else
            Normalize(environment.ArtifactsPath);
    }

    private static void SetAndConfirm(string path, bool recursive, bool directory, IWorkerIntegrityLabeler labeler)
    {
        if (!labeler.SetIntegrity(path, directory ? MediumInheritableLevel : "M", recursive))
            throw new AcceptanceGateIntegrityPreparationException(path);
        var state = labeler.Query(path);
        if (!state.Exists || !state.Medium || state.Low || (directory && !state.Inheritable))
            throw new AcceptanceGateIntegrityPreparationException(path);
    }

    private sealed class RestoreAction(Action restore) : IDisposable
    {
        private Action? _restore = restore;
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}
