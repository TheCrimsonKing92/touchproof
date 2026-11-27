using System.Globalization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed class AttentionHistoryLimit
{
    internal const int DefaultRows = 50;
    internal static AttentionHistoryLimit Default { get; } = new(DefaultRows);

    private readonly int _rows;

    private AttentionHistoryLimit(int rows) => _rows = rows;

    internal static AttentionHistoryLimit Parse(string token)
    {
        if (!int.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var rows) || rows <= 0)
            throw new ArgumentException(CliCommandHelp.AttentionShowUsage);

        return new AttentionHistoryLimit(rows);
    }

    internal (IReadOnlyList<T> Retained, int Omitted) Apply<T>(
        IReadOnlyList<T> rows, Func<T, DateTimeOffset> createdAt)
    {
        if (rows.Count <= _rows)
            return (rows, 0);

        var retained = rows.Select((row, index) => (Row: row, Index: index))
            .OrderByDescending(entry => createdAt(entry.Row))
            .ThenByDescending(entry => entry.Index)
            .Take(_rows)
            .OrderBy(entry => entry.Index)
            .Select(entry => entry.Row)
            .ToArray();
        return (retained, rows.Count - retained.Length);
    }

    internal void PrintWaits(IReadOnlyList<HumanInputRequest> waits, DateTimeOffset now, IReadOnlyCollection<Goal> goals)
    {
        var (retained, omitted) = Apply(waits, request => request.CreatedAt);
        HumanWaitsTextView.Print(retained, now, includeHistory: true, goals);
        PrintOmitted(omitted);
    }

    internal void PrintItems(IReadOnlyList<CollaborationItem> items, Func<CollaborationItem, string?>? itemId)
    {
        var (retained, omitted) = Apply(items, item => item.RaisedAt);
        CollaborationItemsTextView.Print(retained, includeHistory: true, itemId);
        PrintOmitted(omitted);
    }

    private static void PrintOmitted(int omitted)
    {
        if (omitted > 0)
            Console.WriteLine($"{omitted} older rows omitted; use --limit <n> to widen the view.");
    }
}
