using System.Text;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class FleetAttentionTextView
{
    internal static string Render(FleetAttentionResult result)
    {
        var text = new StringBuilder();
        text.AppendLine($"Fleet attention: total={result.Total} omitted={result.Omitted} complete={result.Complete.ToString().ToLowerInvariant()}");
        foreach (var project in result.Projects)
        {
            var aliases = project.Aliases.Count == 0 ? "" : $" aliases={string.Join(",", project.Aliases)}";
            text.AppendLine(project.Error is { } error
                ? $"project-unavailable: {project.Project}{aliases} [{error.Code}] {error.Message}"
                : $"project: {project.Project}{aliases} items={project.ItemCount}");
        }
        foreach (var error in result.Errors) text.AppendLine($"[{error.Code}] {error.Message}");
        foreach (var item in result.Items)
        {
            text.AppendLine($"[{item.Kind}] project={item.Project} source={item.Source} goal={item.GoalId ?? "none"} item={item.ItemId} raised={item.RaisedAt ?? "unknown"}");
            text.AppendLine($"  {item.Question.ReplaceLineEndings(Environment.NewLine + "  ")}");
            if (item.NextAction is { } command) text.AppendLine($"  next: {command}");
        }
        if (!result.Complete) text.AppendLine("Inventory incomplete; counts cover successfully read projects only.");
        return text.ToString().TrimEnd();
    }
}
