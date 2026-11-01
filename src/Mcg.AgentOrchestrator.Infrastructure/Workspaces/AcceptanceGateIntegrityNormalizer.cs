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

        var report = new AcceptanceGateIntegrityNormalizeReport();
        labeler ??= new IcaclsIntegrityLabeler();
        if (labeler is IcaclsIntegrityLabeler native)
            labeler = new AcceptanceGateIntegrityLabeler(native);
        try
        {
            report.MeasureEntry(".", () => Relabel(path, directory: true));
            // Existing build outputs can have explicit Low labels even after the root changes.
            // Keep worker-private scratch and Git metadata under their existing owners.
            var root = new DirectoryInfo(path);
            foreach (var topLevel in root.EnumerateFileSystemInfos())
            {
                // Measure the whole subtree, including label reads and traversal.
                report.MeasureEntry(topLevel.Name, () =>
                {
                    var pending = new Stack<DirectoryInfo>();
                    Visit(topLevel, topLevelEntry: true, pending);
                    while (pending.TryPop(out var parent))
                        foreach (var child in parent.EnumerateFileSystemInfos())
                            Visit(child, topLevelEntry: false, pending);
                });
            }
            report.WriteSuccess();

            void Visit(FileSystemInfo child, bool topLevelEntry, Stack<DirectoryInfo> pending)
            {
                var attributes = child.Attributes;
                // Never label or traverse links, including dangling junctions. Recursive
                // icacls calls cannot enforce this boundary, so visit each entry ourselves.
                if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                    (topLevelEntry &&
                     (child.Name.Equals(".mcg-sandbox", StringComparison.OrdinalIgnoreCase) ||
                      child.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))))
                {
                    report.CountSkip();
                    return;
                }

                var directory = (attributes & FileAttributes.Directory) != 0;
                Relabel(child.FullName, directory);
                if (directory)
                    pending.Push((DirectoryInfo)child);
            }

            void Relabel(string entryPath, bool directory)
            {
                report.CountEntry();
                if (labeler is IAcceptanceGateSelectiveIntegrityLabeler &&
                    !AcceptanceGateIntegritySelection.NeedsRelabel(labeler.Query(entryPath), directory))
                    return;
                SetAndConfirm(entryPath, recursive: false, directory, labeler);
                report.CountRelabel();
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
        while (context is AcceptanceRunExecutionContextView view)
            context = view.Owner;
        if (context is AcceptanceRunExecutionOwner owner)
            owner.PrepareBuildArtifactIntegrity(environment.ArtifactsPath);
        else
            PrepareBuildArtifactRoot(environment.ArtifactsPath);
    }

    internal static void PrepareBuildArtifactRoot(string artifactsPath)
    {
        // Leased and derived baseline roots can be allocated before their first build.
        Directory.CreateDirectory(artifactsPath);
        Normalize(artifactsPath);
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
