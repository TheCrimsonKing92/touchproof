using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ImpactReplayTrxEvidence
{
    internal static (IReadOnlyList<ImpactReplayLaneOutcome> LaneOutcomes,
        IReadOnlyList<ImpactReplayFailingClass> Failures, IReadOnlyList<string> Warnings,
        IReadOnlyList<ImpactReplayLaneOutcome> BalancedLaneOutcomes,
        IReadOnlyList<ImpactReplayUnresolvedFailure> UnresolvedFailures,
        IReadOnlyDictionary<(string ClassName, string Lane), IReadOnlyList<string>> FailingIdentities) Read(
        ImpactReplayStoredAttempt attempt, IReadOnlyList<AcceptanceTestLane> lanes,
        IReadOnlyList<string> checkNames, IReadOnlyList<AcceptanceTestClassDescriptor> classes,
        IReadOnlyList<string>? poolLaneNames = null)
    {
        var laneNames = lanes.Select(lane => lane.Name).Concat(poolLaneNames ?? []).ToArray();
        var pathsByLane = laneNames.ToDictionary(name => name, _ => new List<string>(), StringComparer.Ordinal);
        var failures = new List<ImpactReplayFailingClass>();
        var unresolved = new List<ImpactReplayUnresolvedFailure>();
        var warnings = new List<string>();
        var outcomes = new Dictionary<string, string>(StringComparer.Ordinal);
        var identities = new Dictionary<(string ClassName, string Lane), HashSet<string>>();
        foreach (var rawPath in attempt.TestResultPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.GetFullPath(rawPath.Replace('\\', Path.DirectorySeparatorChar), attempt.GoalDirectory);
            var stem = Path.GetFileNameWithoutExtension(path);
            var matching = laneNames.Where(name => checkNames.Any(check => Regex.IsMatch(stem,
                    @"(?:^|\.)" + Regex.Escape(AcceptanceArtifactFileNames.Slug(check + ": " + name)) + @"(?:-run-\d+)?$",
                    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)))
                .ToArray();
            foreach (var lane in matching) pathsByLane[lane].Add(path);
            var read = AcceptanceTrxFailureReader.Read(path);
            if (read.Status != AcceptanceTrxReadStatus.Readable)
            {
                warnings.Add($"TRX {read.Status}: {path}");
                foreach (var lane in matching) outcomes[lane] = "Incomplete";
                continue;
            }
            var fatal = read.Failures.Where(failure => AcceptanceTrxOutcomeTaxonomy.IsFatal(failure.Outcome)).ToArray();
            foreach (var lane in matching)
            {
                if (fatal.Length > 0) outcomes[lane] = "Failed";
                else outcomes.TryAdd(lane, "Passed");
            }
            foreach (var failure in fatal)
            {
                var className = ResolveClass(failure.TestName, classes);
                if (className is null)
                {
                    warnings.Add($"Failing test class could not be resolved: {path}: {failure.TestName ?? "unknown test"}");
                    failures.Add(new(failure.TestName ?? "unknown test", null, null));
                    unresolved.Add(new(failure.TestName ?? "unknown test", path, "Class identity unresolved"));
                    continue;
                }
                // The TRX's lane identifies where the failure actually happened, including overlapping filters.
                if (matching.Length == 1)
                {
                    failures.Add(new(className, matching[0], null));
                    var key = (className, matching[0]);
                    if (!identities.TryGetValue(key, out var tests)) identities[key] = tests = new(StringComparer.Ordinal);
                    if (!string.IsNullOrWhiteSpace(failure.TestName)) tests.Add(failure.TestName);
                }
                else
                {
                    warnings.Add($"Failing class has no unambiguous lane receipt: {className}: {path}");
                    failures.Add(new(className, null, null));
                    unresolved.Add(new(className, path, "No unambiguous lane receipt"));
                }
            }
        }
        foreach (var name in laneNames)
        {
            if (!checkNames.Any(check => attempt.FailedChecks.Contains(check + ": " + name, StringComparer.Ordinal))) continue;
            outcomes[name] = "Failed";
            if (pathsByLane[name].Count == 0) warnings.Add($"Failed lane has no TRX: {name}");
        }
        var ran = laneNames.Where(name => pathsByLane[name].Count > 0 || outcomes.ContainsKey(name))
            .Select(name => new ImpactReplayLaneOutcome(name, outcomes.GetValueOrDefault(name, "Incomplete"),
                pathsByLane[name])).ToArray();
        if (ran.Length > 0 && ran.All(lane => lane.TrxPaths.Count == 0)) warnings.Add("No lane TRX evidence was available.");
        return (ran.Where(lane => !(poolLaneNames ?? []).Contains(lane.Lane, StringComparer.Ordinal)).ToArray(),
            failures.Distinct().ToArray(), warnings,
            ran.Where(lane => (poolLaneNames ?? []).Contains(lane.Lane, StringComparer.Ordinal)).ToArray(), unresolved,
            identities.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.Order(StringComparer.Ordinal).ToArray()));
    }

    internal static string? ResolveClass(string? identity, IReadOnlyList<AcceptanceTestClassDescriptor> classes)
    {
        if (string.IsNullOrWhiteSpace(identity)) return null;
        var normalized = AcceptanceTrxTestIdentityResolver.NormalizeSelector(identity);
        var known = classes.Where(item => normalized.StartsWith(item.FullName + ".", StringComparison.Ordinal) ||
                                         normalized.StartsWith(item.FullName + "+", StringComparison.Ordinal))
            .OrderByDescending(item => item.FullName.Length).FirstOrDefault();
        if (known is not null) return known.FullName;
        var methodStart = normalized.LastIndexOf('.');
        return methodStart > 0 ? normalized[..methodStart] : null;
    }
}
