namespace Mcg.AgentOrchestrator.App.Orchestration;

// Publication reporting must never interrupt adoption or another reporting channel.
internal static class AdoptedBuildPublicationReporting
{
    internal static bool Try(Action report, string operation)
    {
        try { report(); return true; }
        catch (Exception ex)
        {
            // Exception messages can contain pattern contents or git diagnostics.
            try { Console.Error.WriteLine($"[adopted build publication] {operation} failed: {ex.GetType().Name}"); }
            catch (Exception) { } // A closed diagnostic stream cannot terminate the supervisor either.
            return false;
        }
    }
}
