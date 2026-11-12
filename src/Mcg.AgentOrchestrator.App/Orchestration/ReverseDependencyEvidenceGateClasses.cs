using System.IO.Enumeration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ReverseDependencyEvidenceGateClasses
{
    internal static IReadOnlyList<string> Read(string worktree, IReadOnlyList<string> changedFiles,
        string? projectHomeDirectory)
    {
        var settings = AcceptanceGateEngineSettings.Load(worktree, projectHomeDirectory);
        var checks = GoalAcceptanceVerifier.CreateEffectiveGatePlan(worktree, changedFiles, settings, projectHomeDirectory,
                AcceptanceInfrastructureLaneResolver.ResolveIdentity(settings, worktree)).Checks
            .Where(check => check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase)).ToArray();
        var classes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in checks.GroupBy(check => check.Project, StringComparer.OrdinalIgnoreCase))
        {
            var projects = group.Key is { Length: > 0 } project ? new[] { project } :
                GoalAcceptanceVerifier.DiscoverTrustedTestProjects(worktree);
            foreach (var projectPath in projects)
            foreach (var item in AcceptanceTestClassSourceScanner.ScanProject(worktree, projectPath))
                if (group.Any(check => Includes(check.Arguments, item.FullName))) classes.Add(item.FullName);
        }
        return classes.Order(StringComparer.Ordinal).ToArray();
    }

    private static bool Includes(IReadOnlyList<string> arguments, string name)
    {
        var included = new List<string>();
        var excluded = new List<string>();
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument is not ("--filter" or "--filter-class" or "--filter-not-class")) continue;
            if (++index == arguments.Count) throw new InvalidDataException("Incomplete gate class filter.");
            if (argument == "--filter")
            {
                var lane = new AcceptanceTestLane("evidence cost", arguments[index]);
                if (AcceptanceLaneMembership.LanesIncluding([lane], name).Count == 0) return false;
            }
            else (argument == "--filter-class" ? included : excluded).Add(arguments[index]);
        }
        return (included.Count == 0 || included.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, name))) &&
            !excluded.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, name));
    }
}
