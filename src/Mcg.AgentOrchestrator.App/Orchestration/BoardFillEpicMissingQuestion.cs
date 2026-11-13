using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Membership observations and question episodes are shared by the tick and filing threads.
internal sealed class BoardFillEpicMissingQuestion(
    Func<IReadOnlyList<BacklogItem>, IReadOnlySet<string>> read,
    ConductEventLogWriter events, ICollaborationItemStore? attention = null)
{
    private const string Prefix = "board-fill-epic-missing:";
    private readonly object _gate = new();
    private readonly HashSet<string> _announced = new(StringComparer.Ordinal);
    private readonly HashSet<string> _openQuestions = new(StringComparer.Ordinal);
    private bool _attentionLoaded;
    private string? _lastReadFailure;

    internal IReadOnlySet<string>? Read(IReadOnlyList<BacklogItem> items, DateTimeOffset now)
    {
        lock (_gate)
        {
            IReadOnlySet<string> members;
            try { members = read(items); }
            catch (Exception exception)
            {
                var detail = SingleLine($"BOARD_FILL_EPIC_READ_FAILED reason={exception.GetType().Name}: {exception.Message}");
                if (detail != _lastReadFailure && events.AppendRequired("board-fill-epic-read-failed", null, detail, now))
                    _lastReadFailure = detail;
                return null;
            }
            _lastReadFailure = null;
            if (attention is not null && !_attentionLoaded)
            {
                foreach (var question in attention.ListAsync().GetAwaiter().GetResult().Where(question =>
                    !CollaborationItemLifecycle.IsTerminal(question.Status) &&
                    question.CorrelationKey?.StartsWith(Prefix, StringComparison.Ordinal) == true))
                    _openQuestions.Add(question.CorrelationKey![Prefix.Length..]);
                _attentionLoaded = true;
            }
            foreach (var id in members)
            {
                if (_openQuestions.Contains(id))
                {
                    attention?.TryResolveAsync(Prefix + id, "backlog item assigned to an epic").GetAwaiter().GetResult();
                    _openQuestions.Remove(id);
                }
                _announced.Remove(id);
            }
            return members;
        }
    }

    internal void Ask(BacklogItem item, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_announced.Contains(item.Id)) return;
            var label = SingleLine($"{BoardFillFiledEvent.Id8(item.Id)} {item.Title}");
            var question = $"Which epic does backlog item {label} belong to?";
            attention?.RaiseAsync(CollaborationItemType.Decision, null, $"Backlog epic missing: {label}",
                question, Prefix + item.Id).GetAwaiter().GetResult();
            if (attention is not null) _openQuestions.Add(item.Id);
            if (events.AppendRequired("board-fill-epic-missing", null,
                $"BOARD_FILL_EPIC_MISSING backlog={BoardFillFiledEvent.Id8(item.Id)} id={item.Id} title={SingleLine(item.Title)} question={question}", now))
                _announced.Add(item.Id);
        }
    }

    private static string SingleLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ')
        .Replace('\u0085', ' ').Replace('\u2028', ' ').Replace('\u2029', ' ');

    internal static IReadOnlySet<string> ReadMemberships(string path, IReadOnlyList<BacklogItem> items)
    {
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { return new HashSet<string>(StringComparer.Ordinal); }
        catch (DirectoryNotFoundException) { return new HashSet<string>(StringComparer.Ordinal); }
        using var session = new SqliteReadSession(path);
        var schema = StoreSchemaVersions.Verify(session.Connection, StoreSchemaRegistry.Portfolio);
        if (schema != StoreSchemaState.Current)
            throw new InvalidOperationException($"Portfolio membership schema is {schema}; run setup.");
        using var command = session.Connection.CreateCommand();
        var ids = SqliteReadSession.BindIds(command, items.Select(item => item.Id));
        command.CommandText = $"SELECT m.backlog_item_id FROM backlog_epic_memberships m JOIN epics e ON e.id=m.epic_id WHERE m.backlog_item_id IN ({ids})";
        using var reader = command.ExecuteReader();
        var members = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) members.Add(reader.GetString(0));
        return members;
    }
}
