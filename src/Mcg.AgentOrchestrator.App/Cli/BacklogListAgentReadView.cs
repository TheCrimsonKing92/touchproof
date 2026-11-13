using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class BacklogListAgentReadView
{
    internal static IReadOnlyList<BacklogItem> SelectItems(
        IReadOnlyList<BacklogItem> items, bool newestFirst, int? limit,
        EpicMembershipIndex.Lookup? noEpic = null)
    {
        IEnumerable<BacklogItem> eligible = noEpic is null
            ? items
            : items.Where(item => noEpic.FindBacklogItem(item.Id) is null);
        IEnumerable<BacklogItem> selected = newestFirst
            ? eligible.OrderByDescending(item => item.CreatedAt)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
            : eligible;
        if (limit is { } cap)
            selected = selected.Take(cap);
        return selected.ToArray();
    }

    internal sealed record Row(string Id, string Title, string Status, string? GoalId,
        bool OwnerAmbiguous, bool Eligible, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
        string? Priority, string Tags, string? SupersededBy, string? EpicId = null);

    internal static Row CreateRow(BacklogItem item, string status, string? goalId,
        bool ownerAmbiguous, bool eligible, string? epicId = null) =>
        new(item.Id, item.Title, status, goalId, ownerAmbiguous, eligible,
            item.CreatedAt, item.UpdatedAt, item.Priority, item.Tags, item.SupersededBy, epicId);

    internal static string FormatEpic(EpicMembershipIndex.Membership? membership) =>
        membership is null ? "none" : $"{membership.ShortId} {membership.EpicTitle}";

    internal static string Serialize(IReadOnlyList<Row> rows, TimeProvider? time = null) =>
        AgentReadEnvelope.Serialize(AgentReadEnvelope.BacklogListSchema, AgentReadEnvelope.BacklogListSchemaVersion,
            new
            {
                items = rows.Select(row => new
                {
                    id = row.Id,
                    status = row.Status,
                    goalId = row.OwnerAmbiguous ? null : row.GoalId,
                    ownerAmbiguous = row.OwnerAmbiguous,
                    eligible = row.Eligible,
                    title = row.Title,
                    createdAt = row.CreatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                    updatedAt = row.UpdatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                    priority = string.IsNullOrEmpty(row.Priority) ? null : row.Priority,
                    tags = row.Tags,
                    supersededBy = string.IsNullOrEmpty(row.SupersededBy) ? null : row.SupersededBy,
                    epicId = row.EpicId
                }).ToArray()
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web), time);
}
