using System.Globalization;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerGoalProgressDetail
{
    internal static IReadOnlyList<OwnerConsoleField> Fields(OwnerConductEvent source)
    {
        var fields = new List<OwnerConsoleField>();
        if (source.GoalProgress is { } progress)
        {
            Add("goal", "Goal ID", progress.GoalId);
            Add("result", "Result", progress.Result);
            Add("state", "State", progress.State);
            Add("slot", "Slot", progress.Slot?.ToString(CultureInfo.InvariantCulture));
            Add("reason", "Reason", progress.Reason);
            if (progress.Decision is { } decision)
            {
                Add("decision", "Decision", $"{decision.Stage}/{decision.Rung.ToString(CultureInfo.InvariantCulture)}/{decision.DiscriminatingEvidence}; action={decision.Action}; reason={decision.Reason}");
                for (var i = 0; i < decision.Facts.Count; i++)
                    Add($"decision/fact/{i}", $"Decision fact {i + 1}: {decision.Facts[i].Name}", decision.Facts[i].Value);
            }
            if (progress.Failure is { } failure)
            {
                Add("failure-kind", "Failure kind", failure.Kind);
                Add("rule", "Rule", failure.Rule);
                for (var i = 0; i < failure.Evidence.Count; i++)
                {
                    var entry = failure.Evidence[i];
                    var label = entry.Name == "worker_build_applicability" ? "Worker build applicability" : entry.Name;
                    var value = string.IsNullOrWhiteSpace(entry.NextStep) ? entry.Value : $"{entry.Value}; next={entry.NextStep}";
                    Add($"evidence/{i}", $"{label} {i + 1}", value);
                }
            }
        }
        else if (source.Detail.StartsWith("GOAL goal=", StringComparison.Ordinal))
        {
            // Compatibility only: never infer nested rule/evidence structure from a legacy reason.
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var token in source.Detail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Skip(1))
            {
                var equals = token.IndexOf('=');
                if (equals <= 0) continue;
                var name = token[..equals];
                var occurrence = occurrences.GetValueOrDefault(name) + 1;
                occurrences[name] = occurrence;
                var label = name == "goal" ? "Goal ID" : char.ToUpperInvariant(name[0]) + name[1..];
                Add($"legacy/{name}/{occurrence}", occurrence == 1 ? label : $"{label} {occurrence}", token[(equals + 1)..].Replace('_', ' '));
            }
        }
        return fields;

        void Add(string key, string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) fields.Add(new(key, label, value));
        }
    }
}
