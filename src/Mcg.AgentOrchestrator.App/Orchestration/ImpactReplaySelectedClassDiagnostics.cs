using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Preserve short-name observations independently of qualified selection and support-file abandonment.
internal static class ImpactReplaySelectedClassDiagnostics
{
    internal const string UnresolvedCollection = "unresolved-collection";

    internal static IReadOnlyList<ImpactReplayUnmappedTestClasses.Occurrence> Read(string attemptId,
        string checkout, IReadOnlyList<string> changedTests, IReadOnlyList<string> selectedClasses,
        IReadOnlyList<AcceptanceTestClassDescriptor> classes, IReadOnlyList<AcceptanceTestLane> lanes,
        IReadOnlyList<ImpactReplayBalancedClass> pool)
    {
        var present = changedTests.Where(path => File.Exists(Path.Combine(checkout, path)));
        var declarations = RepositoryTestImpactPlanner.ReadChangedInfrastructureTestClasses(present, checkout);
        var occurrences = new List<ImpactReplayUnmappedTestClasses.Occurrence>();
        if (changedTests.Count > 0)
        {
            var (_, excluded) = ImpactReplayChangedTestSupportSelection.RunnableClasses(
                checkout, new ImpactReplayChangedSourceRouting(checkout));
            // First occurrence wins during aggregation, preserving the specific exclusion reason.
            foreach (var name in excluded)
                occurrences.Add(new(attemptId, name.Split('.', '+').Last(), UnresolvedCollection, [name]));
        }
        foreach (var simpleName in selectedClasses.Concat(declarations.TestClasses).Distinct(StringComparer.Ordinal))
        {
            var matches = classes.Where(item => item.FullName.Split('.', '+').Last() == simpleName).ToArray();
            var hasLane = matches.Length == 1 &&
                (AcceptanceLaneMembership.LanesIncluding(lanes, matches[0].FullName).Count > 0 ||
                 pool.Any(item => item.ClassName == matches[0].FullName));
            var diagnostic = ImpactReplayUnmappedTestClasses.Classify(attemptId, simpleName,
                matches.Select(item => item.FullName).ToArray(), hasLane);
            if (diagnostic is not null) occurrences.Add(diagnostic);
        }
        return occurrences;
    }
}
