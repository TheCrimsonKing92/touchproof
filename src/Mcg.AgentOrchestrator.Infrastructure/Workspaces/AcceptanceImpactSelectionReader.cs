using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Candidate-local composition shared by replay and live acceptance. Replay owns its cache policy.
internal static class AcceptanceImpactSelectionReader
{
    internal static AcceptanceSelectedExecutionInput ReadLive(string checkout, IReadOnlyList<string> changedFiles,
        AcceptanceGateEngineSettings? engineSettings = null)
    {
        var settings = engineSettings ?? AcceptanceGateEngineSettings.Load(checkout);
        var classes = AcceptanceTestClassSourceScanner.Scan(checkout);
        var routing = new ImpactReplayChangedSourceRouting(checkout);
        var sources = new ImpactReplayTestSourceIndex(LaneTestSourceRootResolver.SourceFiles(checkout,
            LaneTestSourceRootResolver.Resolve(checkout)), routing.TestProjects, checkout);
        var guards = SourceReadingGuardTestClasses.Read(sources, classes);
        var impact = Read(checkout, changedFiles, routing, sources, classes, guards,
            ChangedFileTestClassPathMapper.TestProjectDirectories(settings), bypassCache: false);
        var selection = impact.ChangedTests.AbandonReason is { } reason
            ? ReverseDependencyTestSelection.Abandoned(ReverseDependencyDegradationKind.ChangedFileUnmapped, reason)
            : impact.Selection;
        return new(selection, classes, (guards?.Classes ?? []).Concat(impact.DeclaredGuards.Selections
            .Select(item => item.ClassName)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    internal static (ReverseDependencyTestSelection Selection, ImpactReplayDeclaredGuardReport DeclaredGuards,
        (IReadOnlyList<string> Classes, bool Gained, string? AbandonReason, IReadOnlyList<string> OtherFiles,
            IReadOnlyList<string> QualifiedClasses) ChangedTests) Read(
        string checkout, IReadOnlyList<string> changedFiles, ImpactReplayChangedSourceRouting routing,
        ImpactReplayTestSourceIndex sources, IReadOnlyList<AcceptanceTestClassDescriptor> classes,
        ImpactReplaySourceGuardReport? guards, IReadOnlyList<string> testProjectDirectories, bool bypassCache)
    {
        var changedSources = changedFiles.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var changedTests = changedSources.Where(routing.IsTestSource).ToArray();
        var productionSources = changedSources.Except(changedTests, StringComparer.OrdinalIgnoreCase).ToArray();
        var selection = productionSources.Length == 0 && changedTests.Length > 0
            ? ReverseDependencyTestSelection.Resolved([])
            : ReverseDependencyTestImpactReader.Read(checkout, productionSources, bypassCache: bypassCache);
        var augmented = ImpactReplayChangedTestClassSelection.Read(checkout, changedTests, selection.TestClassNames, routing);
        selection = ImpactReplaySelectionAugmentation.Apply(selection, changedFiles, sources, classes, guards,
            out var declaredGuards, augmented.QualifiedClasses, testProjectDirectories);
        return (selection, declaredGuards, augmented);
    }
}
