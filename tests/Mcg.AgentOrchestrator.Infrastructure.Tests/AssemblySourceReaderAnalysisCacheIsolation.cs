using Mcg.AgentOrchestrator.Infrastructure;

internal static partial class AssemblyTempRedirect
{
    static partial void InstallSourceReaderCache(string? ownedRoot) =>
        AssemblySourceReaderAnalysisCacheIsolation.Install(ownedRoot);
}

internal static class AssemblySourceReaderAnalysisCacheIsolation
{
    internal static string Root { get; private set; } = "";
    internal static bool Installed { get; private set; }
    internal static Exception? InstallationError { get; private set; }

    internal static void Install(string? ownedRoot)
    {
        try
        {
            // Register after temp-root selection: the test host already cleans its leased root
            // on exit and reaps abandoned roots on subsequent runs.
            Root = ownedRoot is not null
                ? Path.Combine(ownedRoot, "source-reader-analysis")
                : Path.Combine(Path.GetTempPath(), "mcg-orchestrator-tests", "source-reader-analysis", Guid.NewGuid().ToString("N"));
            Installed = SourceReaderAnalysisCache.OverrideDefaultDirectory(Root);
            if (Installed && ownedRoot is null)
                AppDomain.CurrentDomain.ProcessExit += (_, _) => RemoveFallbackDirectory();
        }
        catch (Exception error)
        {
            // Preserve discovery; the isolation regression test reports initialization failures.
            InstallationError = error;
        }
    }

    private static void RemoveFallbackDirectory()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
