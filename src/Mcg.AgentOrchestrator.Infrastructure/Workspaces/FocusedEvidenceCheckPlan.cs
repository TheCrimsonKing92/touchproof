using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Selection and its read-only cost snapshot travel together through the existing execution seams.
internal sealed class FocusedEvidenceCheckPlan(IEnumerable<Check> checks, IClassDurationHistory history) : IReadOnlyList<Check>
{
    private readonly Check[] _checks = checks.ToArray();
    internal IClassDurationHistory History { get; } = history;
    public int Count => _checks.Length;
    public Check this[int index] => _checks[index];
    public IEnumerator<Check> GetEnumerator() => ((IEnumerable<Check>)_checks).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
