namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class ProcessErrorModeRepairLog
{
    private const int Capacity = 256;
    private static readonly object Sync = new();
    private static readonly Queue<string> Notices = new(Capacity);
    private static Action<string>? observer;

    // Repair can notify while holding the Windows launch lock. Observers must not wait on another launch.
    internal static Action<string>? Observer
    {
        get { lock (Sync) return observer; }
        set { lock (Sync) observer = value; }
    }

    internal static void Record(string notice)
    {
        Action<string>? notify;
        lock (Sync)
        {
            if (Notices.Count == Capacity)
                Notices.Dequeue();

            Notices.Enqueue(notice);
            notify = observer;
        }

        try { notify?.Invoke(notice); }
        catch (Exception) { }
    }

    internal static string[] GetSnapshot()
    {
        lock (Sync) return Notices.ToArray();
    }

    internal static void Clear()
    {
        lock (Sync) Notices.Clear();
    }
}
