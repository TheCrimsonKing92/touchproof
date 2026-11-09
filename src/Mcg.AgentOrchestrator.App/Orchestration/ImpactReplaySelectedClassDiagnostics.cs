using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Preserve short-name observations independently of qualified selection and support-file abandonment.
internal static class ImpactReplaySelectedClassDiagnostics
{
    internal static IReadOnlyList<ImpactReplayUnmappedTestClasses.Occurrence> Read(string attemptId,
        string checkout, IReadOnlyList<string> changedTests, IReadOnlyList<string> selectedClasses,
        IReadOnlyList<AcceptanceTestClassDescriptor> classes, IReadOnlyList<AcceptanceTestLane> lanes,
        IReadOnlyList<ImpactReplayBalancedClass> pool)
    {
        var present = changedTests.Where(path => File.Exists(Path.Combine(checkout, path)));
        var declarations = RepositoryTestImpactPlanner.ReadChangedInfrastructureTestClasses(present, checkout);
        var occurrences = new List<ImpactReplayUnmappedTestClasses.Occurrence>();
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
