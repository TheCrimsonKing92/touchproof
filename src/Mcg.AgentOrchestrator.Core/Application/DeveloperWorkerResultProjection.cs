namespace Mcg.AgentOrchestrator.Core;

// Selects reported fields without inferring missing claims or granting them evidence authority.
internal static class DeveloperWorkerResultProjection
{
    private static readonly string[] SelectedFields = ["files", "tests", "criteria_self_check", "blockers"];

    internal static IReadOnlyList<string> Project(string block)
    {
        // DogfoodLogRenderer's single-field parser reads the preview/first block and drops empty values.
        var fields = SelectedFields.ToDictionary(field => field, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        List<string>? current = null;
        foreach (var line in block.Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
        {
            if (WorkerResultMarkers.IsWorkerResultOpener(line))
            {
                continue;
            }

            if (WorkerResultMarkers.IsWorkerResultEndMarker(line))
            {
                break;
            }

            if (WorkerResultMarkdownEmphasis.TrySplitField(line.Trim(), out var label, out _) &&
                (AgentOutputDirectives.WorkerResultFieldNames.Contains(label, StringComparer.OrdinalIgnoreCase) ||
                 label.Equals("commit", StringComparison.OrdinalIgnoreCase) ||
                 label.Equals("assigned_scope_complete", StringComparison.OrdinalIgnoreCase) ||
                 label.Equals("criteria_self_check", StringComparison.OrdinalIgnoreCase)))
            {
                fields.TryGetValue(label, out current);
            }

            current?.Add(line);
        }

        return SelectedFields.SelectMany(field => fields[field]).ToArray();
    }
}
