namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleLandedTodayScan(Func<int> scan, TimeProvider clock)
{
    private DateTime? _date;
    private int _count;
    private Func<IReadOnlySet<string>>? _readBaseline;
    private IReadOnlySet<string> _baseline = new HashSet<string>();

    internal OwnerConsoleLandedTodayScan(string directory, TimeProvider clock) : this(() => 0, clock)
    {
        _readBaseline = () => OwnerConsoleLandingBaseline.Read(directory, clock);
    }

    // Retain the supplied startup history, and capture scan membership for late deliveries.
    internal int Seed(int count)
    {
        _date = null;
        _count = _readBaseline is null ? count : Math.Max(count, Current());
        _date = clock.GetLocalNow().Date;
        return _count;
    }

    internal bool Includes(OwnerConductEvent item) => item.GoalId is not null && _baseline.Contains(item.GoalId);

    internal int Current()
    {
        var date = clock.GetLocalNow().Date;
        if (_date == date) return _count;
        var count = _readBaseline is null ? scan() : ReadBaseline();
        _date = date;
        return _count = count;
    }

    private int ReadBaseline()
    {
        _baseline = _readBaseline!();
        return _baseline.Count;
    }
}
