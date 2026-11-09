using System.Text;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ImpactReplaySourceGuardReport(IReadOnlyList<string> Classes)
{
    public int Count => Classes.Count;
    public IReadOnlyList<string> HelperSymbols => ["RepositorySourceSnapshotCache.Get", "RepositorySourceSnapshot.EnumerateFiles",
        "RepositorySourceSnapshot.GetText", "RepositorySourceSnapshot.GetLines", "RepositorySourceSnapshot.GetSyntaxTree"];

    internal void AppendTo(StringBuilder text)
    {
        text.AppendLine();
        text.AppendLine("## Source-reading guards");
        text.AppendLine();
        text.AppendLine($"Helpers: {string.Join(", ", HelperSymbols)}");
        text.AppendLine($"Always-selected guard classes: {Count}");
        if (Count == 0) text.AppendLine("None.");
        foreach (var name in Classes) text.AppendLine($"- {name}");
    }
}
