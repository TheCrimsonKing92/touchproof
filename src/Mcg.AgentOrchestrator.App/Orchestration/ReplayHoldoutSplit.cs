namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ReplayHoldoutSplit
{
    private ReplayHoldoutSplit(ReplayHarnessCase[] cases, DateTimeOffset splitAt)
    {
        Training = Array.AsReadOnly(cases.Where(item => item.RecordedAt <= splitAt).ToArray());
        Evaluation = Array.AsReadOnly(cases.Where(item => item.RecordedAt > splitAt).ToArray());
    }

    internal IReadOnlyList<ReplayHarnessCase> Training { get; }
    internal IReadOnlyList<ReplayHarnessCase> Evaluation { get; }

    internal static ReplayHoldoutSplit Create(IEnumerable<ReplayHarnessCase> cases, DateTimeOffset splitAt) =>
        new(cases.OrderBy(item => item.RecordedAt).ThenBy(item => item.CaseId, StringComparer.Ordinal).ToArray(), splitAt);

    internal T Learn<T>(Func<IReadOnlyList<ReplayHarnessCase>, T> learn) => learn(Training);
}
