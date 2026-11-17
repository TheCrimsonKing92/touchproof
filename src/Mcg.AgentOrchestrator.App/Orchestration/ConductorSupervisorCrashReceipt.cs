namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ConductorSupervisorCrashReceipt
{
    private readonly Action<string, string?, string> _appendConductEvent;

    public ConductorSupervisorCrashReceipt(Action<string, string?, string> appendConductEvent)
    {
        _appendConductEvent = appendConductEvent;
    }

    public void Record(string source, Exception exception)
    {
        try
        {
            var message = exception.Message.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
            _appendConductEvent("loop-handoff", null,
                $"SUPERVISOR_CRASHED source={source} exception={exception.GetType().Name} message={message}");
        }
        catch (Exception)
        {
            // Crash diagnostics must not introduce another failure or retry a possibly persisted event.
        }
    }
}
