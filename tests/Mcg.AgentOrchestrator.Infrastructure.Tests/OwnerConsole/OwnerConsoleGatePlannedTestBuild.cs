using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Infrastructure;

// Estimator fixtures explicitly publish the schedule they intend to measure.
// Parallel-safe: unique files, no processes or ambient repository lookup.
internal static class OwnerConsoleGatePlannedTestBuild
{
    internal static OwnerConsoleGateProgress Build(DateTimeOffset now, IEnumerable<OwnerConductEvent> events,
        IReadOnlyList<OwnerConsoleGateManifestLane> manifest, IEnumerable<OwnerConsoleGateDuration> durations,
        Func<string, string?>? title = null, IReadOnlyDictionary<string, double>? quietSeconds = null,
        OwnerConsoleGateLaneResolver? lanes = null, int? capacity = null,
        IReadOnlyList<OwnerConsoleGateExecutorSlots>? executors = null)
    {
        const string execution = "776a132e-gate-20261016010000000-0123456789abcdef0123456789abcdef";
        var input = events.ToArray();
        var entry = input.FirstOrDefault(item => item.Detail.Contains("members=", StringComparison.Ordinal));
        var members = entry is null ? input.FirstOrDefault()?.GoalId ?? "776a132e" :
            Regex.Match(entry.Detail, @"members=([^\s]+)").Groups[1].Value;
        var root = Path.Combine(Path.GetTempPath(), "planned-gate-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, execution + AcceptanceGateLanePlan.ArtifactSuffix);
            File.WriteAllText(path, new AcceptanceGateLanePlan(AcceptanceGateLanePlan.CurrentSchemaVersion, execution,
                manifest.Select(row => row.Name).ToArray(), []).ToJson());
            var observations = input.Select(item => item.Detail.Contains("phase=gate-phase-breakdown", StringComparison.Ordinal) ? item :
                item with { Detail = item.Detail + (item.EventKind == "remote-lane" ? " attempt=" + execution :
                    " execution=" + execution + " members=" + members) }).ToList();
            observations.Add(new(input.Where(item => !item.Detail.Contains("phase=gate-phase-breakdown", StringComparison.Ordinal))
                .Select(item => item.Timestamp).DefaultIfEmpty(now).Min(), "gate-progress", members.Split([',', '+'])[0],
                $"phase=lane-plan execution={execution} members={members} heartbeat={JsonSerializer.Serialize(path)}"));
            var gate = OwnerConsoleGateProgress.Build(now, observations, manifest, durations, title, quietSeconds,
                lanes, capacity, executors, new OwnerConsoleGateLanePlanReader());
            // Existing estimator/column fixtures render an unnamed snapshot. The
            // execution replay suites separately assert the full identity surfaces.
            return gate with { ExecutionId = null };
        }
        finally { Directory.Delete(root, true); }
    }
}
