namespace Mcg.AgentOrchestrator.App.Orchestration;

// The single startup sweep and first tick belong to this process's startup lifetime.
internal static class StartupSweepTotal
{
    private static long _milliseconds = -1;
    internal static long? Milliseconds
    {
        get
        {
            var value = Interlocked.Read(ref _milliseconds);
            return value >= 0 ? value : null;
        }
    }
    internal static void Record(long milliseconds) => Interlocked.Exchange(ref _milliseconds, milliseconds);
}
