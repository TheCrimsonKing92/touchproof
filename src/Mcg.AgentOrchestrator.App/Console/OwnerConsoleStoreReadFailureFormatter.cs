using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleStoreReadFailureFormatter
{
    internal static string Message(Exception error)
    {
        var diagnostic = StoreSchemaDiagnostics.Message(error);
        // Only Newer schema failures carry a separate compatibility diagnostic.
        return string.Equals(diagnostic, error.Message, StringComparison.Ordinal)
            ? diagnostic
            : "This console build is older than the shared store. You must restart the console on a current build.";
    }
}
