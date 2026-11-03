using System.Globalization;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record FleetAttentionError(string Code, string Message);
internal sealed record FleetAttentionItem(string Project, string Source, string? GoalId, string ItemId,
    string Kind, string? RaisedAt, string Question, string? NextAction);
internal sealed record FleetAttentionProject(string Project, IReadOnlyList<string> Aliases, string Status,
    int? ItemCount, FleetAttentionError? Error);
internal sealed record FleetAttentionResult(IReadOnlyList<FleetAttentionItem> Items,
    IReadOnlyList<FleetAttentionProject> Projects, int Total, int Omitted, bool Complete,
    IReadOnlyList<FleetAttentionError> Errors);
internal sealed class FleetAttentionStoreException(string code, string message, Exception inner)
    : IOException(message, inner)
{
    internal string Code { get; } = code;
}

// One invocation, independent project reads, no cross-project state or writer lifetime.
internal static class FleetAttentionReader
{
    internal static async Task<FleetAttentionResult> ReadAsync(AttentionWorkspaceCatalog catalog, int limit,
        DateTimeOffset now, CancellationToken cancellationToken = default,
        Func<OrchestratorWorkspace, DateTimeOffset, CancellationToken, Task<AttentionJsonReadContext>>? read = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        cancellationToken.ThrowIfCancellationRequested();
        read ??= AttentionJsonReadContext.ReadExistingAsync;
        var projects = new List<FleetAttentionProject>();
        var items = new List<FleetAttentionItem>();
        var errors = new List<FleetAttentionError>();
        if (catalog.RegistryError is { } registryError)
            errors.Add(new("registry-unavailable", Concise(registryError.Message)));
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        // Unresolved entries cannot establish equivalence. Group only resolved canonical state paths.
        var groups = catalog.Projects.Where(entry => entry.Workspace is not null)
            .GroupBy(entry => WorkspaceInvocationBinding.CanonicalPath(entry.Workspace!.SqliteStatePath), pathComparer)
            .Select(group => group.OrderBy(entry => entry.Name.Equals("home", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Name, StringComparer.Ordinal).ToArray())
            .Concat(catalog.Projects.Where(entry => entry.Workspace is null).Select(entry => new[] { entry }))
            .OrderBy(group => group[0].Name, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var canonical = group[0];
            var aliases = group.Skip(1).Select(entry => entry.Name).Distinct(StringComparer.Ordinal)
                .Where(name => name != canonical.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(name => name, StringComparer.Ordinal).ToArray();
            try
            {
                if (canonical.Error is { } resolutionError) throw resolutionError;
                var workspace = canonical.Workspace ?? throw new InvalidOperationException("Workspace did not resolve.");
                var context = await read(workspace, now, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var rows = Project(canonical.Name, workspace, context)
                    .DistinctBy(item => (item.Project, item.Source, item.GoalId, item.ItemId)).ToArray();
                items.AddRange(rows);
                projects.Add(new(canonical.Name, aliases, "available", rows.Length, null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                projects.Add(new(canonical.Name, aliases, "unavailable", null, Failure(ex)));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var ordered = items.OrderBy(item => item.RaisedAt is null)
            .ThenBy(item => item.RaisedAt, StringComparer.Ordinal)
            .ThenBy(item => item.Project, StringComparer.Ordinal)
            .ThenBy(item => item.GoalId, StringComparer.Ordinal)
            .ThenBy(item => item.Source, StringComparer.Ordinal)
            .ThenBy(item => item.ItemId, StringComparer.Ordinal).Take(limit).ToArray();
        return new(ordered, projects, items.Count, items.Count - ordered.Length,
            errors.Count == 0 && projects.All(project => project.Status == "available"), errors);
    }

    private static IEnumerable<FleetAttentionItem> Project(string project, OrchestratorWorkspace workspace,
        AttentionJsonReadContext context)
    {
        foreach (var wait in context.Waits.Where(wait => !wait.IsCompleted && HumanWaitPolicyDefaults.BlocksActiveWork(wait.Kind)))
            yield return new(project, "human-wait", wait.GoalId.Value, wait.Id.Value, wait.Kind.ToString(),
                Timestamp(wait.RequestedAt), wait.Question,
                Qualify(wait.ResumeCommand, project, workspace, wait.GoalId.Value, wait.Id.Value));
        foreach (var item in context.Items.Where(item => !CollaborationItemLifecycle.IsTerminal(item.Status)))
        {
            // These are the existing attention view's answer/dismiss templates, with full IDs.
            var command = CliCommandHandlers.IsSpecClarification(item) && item.GoalId is not null
                ? $"attention answer {Quote(item.GoalId)} {Quote(item.Id)} --text-file <path>"
                : $"attention dismiss --item {Quote(item.Id)}";
            yield return new(project, "collaboration-item", item.GoalId, item.Id, item.Type.ToString(),
                Timestamp(item.RaisedAt), string.IsNullOrWhiteSpace(item.Body) ? item.Subject : $"{item.Subject}\n{item.Body}",
                Qualify(command, project, workspace, item.GoalId, item.Id));
        }
        foreach (var hold in context.OwnerQuestions)
        {
            if (context.Goals?.FirstOrDefault(goal => goal.Id == hold.GoalId) is { } goal &&
                goal.Status is GoalStatus.Parked or GoalStatus.Failed or GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Superseded)
                continue;
            yield return new(project, "owner-question", hold.GoalId.Value, hold.Identity, hold.State,
                Timestamp(hold.StartedAt), hold.Question,
                Qualify(hold.NextAction, project, workspace, hold.GoalId.Value, null));
        }
    }

    private static string? Timestamp(DateTimeOffset value) => value == DateTimeOffset.MinValue ? null :
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static string? Qualify(string? command, string project, OrchestratorWorkspace workspace,
        string? goalId, string? itemId)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        // Do not guess how to rewrite shell pipelines or an already bound command.
        if (command.IndexOfAny(['|', ';', '&', '\r', '\n']) >= 0 ||
            command.Contains("--project", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("--tenant", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var identity in new[] { goalId, itemId }.OfType<string>().Where(id => id.Length >= 8))
            command = Regex.Replace(command, $@"(?<!\S){Regex.Escape(identity[..8])}(?!\S)", Quote(identity));
        var binding = project == "home" ? OrchestratorWorkspace.DefaultProjectName : project;
        return $"--project={binding} --tenant={workspace.TenantName} {command}";
    }

    private static string Quote(string value) => value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or ':' or '.')
        ? value : "\"" + value.Replace("\"", "\\\"") + "\"";

    private static FleetAttentionError Failure(Exception error) => new(error switch
    {
        FleetAttentionStoreException store => store.Code,
        UnauthorizedAccessException => "unreadable-store",
        FileNotFoundException or DirectoryNotFoundException => "missing-store",
        IOException => "unreadable-store",
        SqliteException { SqliteErrorCode: 3 or 14 or 23 or 26 } => "unreadable-store",
        _ => "read-failed"
    }, Concise(error.Message));

    private static string Concise(string message) => message.ReplaceLineEndings(" ");
}
