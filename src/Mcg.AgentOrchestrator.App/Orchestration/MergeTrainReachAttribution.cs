using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Reach is attribution evidence only when the complete failing set has one unique member.
internal static class MergeTrainReachAttribution
{
    internal static MergeTrainMemberBinding? TryAttribute(
        IReadOnlyList<string> resultPaths, string workspacePath,
        IReadOnlyList<MergeTrainMemberBinding> members, out IReadOnlyList<string> failingClasses,
        IReadOnlySet<string>? confirmedIdentities = null)
    {
        failingClasses = [];
        var classes = ReadFailingClasses(resultPaths, confirmedIdentities);
        if (classes is null) return null;
        try
        {
            var reaches = new List<IReadOnlySet<string>>();
            foreach (var member in members)
            {
                var sources = member.LandingPaths.Select(path => NormalizePath(workspacePath, path))
                    .Where(path => path.StartsWith("src/", StringComparison.OrdinalIgnoreCase) &&
                        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var reach = new HashSet<string>(StringComparer.Ordinal);
                // Read the complete member reach together; a member without sources has no reach.
                if (sources.Length > 0)
                {
                    var selected = RepositoryTestReach.TryReadReachableTestClasses(workspacePath, sources);
                    if (selected is null) return null;
                    reach.UnionWith(selected);
                }
                reaches.Add(reach);
            }

            MergeTrainMemberBinding? attributed = null;
            foreach (var className in classes)
            {
                var owners = members.Where((_, index) => reaches[index].Contains(className)).ToArray();
                if (owners.Length != 1 || (attributed is not null && attributed.GoalId != owners[0].GoalId))
                    return null;
                attributed = owners[0];
            }
            failingClasses = classes;
            return attributed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    // Keep parity with the impact path: never authorize from partial or guarded evidence.
    internal static IReadOnlyList<string>? ReadFailingClasses(IReadOnlyList<string> resultPaths,
        IReadOnlySet<string>? confirmedIdentities = null)
    {
        var failures = ReadFailures(resultPaths, confirmedIdentities);
        if (failures is null) return null;
        var classes = failures.Select(failure => AcceptanceTestSourceResolver.ExtractClassName(failure.TestName ?? string.Empty))
            .Distinct(StringComparer.Ordinal).ToArray();
        return classes.Any(string.IsNullOrWhiteSpace) ? null : classes.Select(name => name!).Order(StringComparer.Ordinal).ToArray();
    }

    internal static IReadOnlyList<AcceptanceTrxFailure>? ReadFailures(IReadOnlyList<string> resultPaths,
        IReadOnlySet<string>? confirmedIdentities = null)
    {
        var results = resultPaths.Select(AcceptanceTrxFailureReader.Read).ToArray();
        if (results.Any(result => result.Status != AcceptanceTrxReadStatus.Readable)) return null;
        var failures = results.SelectMany(result => result.Failures)
            .Where(failure => AcceptanceTrxOutcomeTaxonomy.IsFatal(failure.Outcome)).ToArray();
        if (confirmedIdentities is not null)
        {
            // Gate confirmation includes apparatus rows. Check coverage before
            // excluding those rows from attribution, as the ownership stage does.
            var observed = failures.Where(failure => !string.IsNullOrWhiteSpace(failure.TestName))
                .Select(failure => AcceptanceTrxTestIdentityResolver.NormalizeSelector(failure.TestName!))
                .ToHashSet(StringComparer.Ordinal);
            if (!confirmedIdentities.All(observed.Contains)) return null;
            failures = failures.Where(failure => failure.TestName is not null &&
                confirmedIdentities.Contains(AcceptanceTrxTestIdentityResolver.NormalizeSelector(failure.TestName))).ToArray();
        }
        failures = failures.Where(failure =>
            ApparatusInfrastructureSignatures.Match(failure.Message, failure.StackTrace) is null).ToArray();
        return failures.Length == 0 || failures.Any(MergeTrainRedAttribution.IsMessageSubjectGuard) ? null : failures;
    }

    private static string NormalizePath(string workspacePath, string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(normalized))
            normalized = Path.GetRelativePath(workspacePath, normalized).Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        return normalized;
    }
}
