namespace Mcg.AgentOrchestrator.Infrastructure;

// One process-wide destination for transient idle diagnostics; reports are never retained.
internal static class GitProgressSink
{
    private static readonly Action<string> Drop = static _ => { };
    private static Action<string> callback = Drop;

    internal static Action<string> Callback => Volatile.Read(ref callback);

    // Callers own restoration of the returned destination, normally in a finally block.
    internal static Action<string> Replace(Action<string>? destination) =>
        Interlocked.Exchange(ref callback, destination ?? Drop);

    internal static void Report(string text)
    {
        var destination = Callback;
        try { destination(text); }
        catch (Exception) { /* Diagnostics must not change git completion or emit secondary output. */ }
    }
}
