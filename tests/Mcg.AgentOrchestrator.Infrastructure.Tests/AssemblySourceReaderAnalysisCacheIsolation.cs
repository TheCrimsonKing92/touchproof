using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.Infrastructure;

internal static class AssemblySourceReaderAnalysisCacheIsolation
{
    internal static string Root { get; private set; } = "";
    internal static bool Installed { get; private set; }
    internal static Exception? InstallationError { get; private set; }

    [ModuleInitializer]
    internal static void Install()
    {
        try
        {
            Root = Path.Combine(Path.GetTempPath(), "mcg-orchestrator-tests", "source-reader-analysis",
                Guid.NewGuid().ToString("N"));
            Installed = SourceReaderAnalysisCache.OverrideDefaultDirectory(Root);
        }
        catch (Exception error)
        {
            // Preserve discovery; the isolation regression test reports initialization failures.
            InstallationError = error;
        }
    }
}
