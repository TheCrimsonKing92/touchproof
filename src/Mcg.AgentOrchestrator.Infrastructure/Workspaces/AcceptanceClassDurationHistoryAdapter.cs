namespace Mcg.AgentOrchestrator.Infrastructure;

// The upstream store contains Infrastructure gate observations only. Freeze it for this consumer.
internal sealed class AcceptanceClassDurationHistoryAdapter : IClassDurationHistory
{
    private readonly IReadOnlyDictionary<string, double> _seconds;

    internal AcceptanceClassDurationHistoryAdapter(IReadOnlyDictionary<string, double> seconds) =>
        _seconds = seconds.Where(pair => double.IsFinite(pair.Value) && pair.Value >= 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    internal static IClassDurationHistory Read(string worktreePath) => new AcceptanceClassDurationHistoryAdapter(
        AcceptanceClassDurationHistoryStore.ReadMedians(worktreePath, new AcceptanceBalancedLanePoolSettings().HistoryWindow));

    public double? PredictSeconds(string projectQualifiedClass)
    {
        const string prefix = "Infrastructure.Tests:";
        if (!projectQualifiedClass.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var name = projectQualifiedClass[prefix.Length..];
        if (_seconds.TryGetValue(name, out var exact)) return exact;
        var shortName = name[(name.LastIndexOf('.') + 1)..];
        var matches = _seconds.Where(pair => pair.Key[(pair.Key.LastIndexOf('.') + 1)..] == shortName).Take(2).ToArray();
        return matches.Length == 1 ? matches[0].Value : null;
    }
}
