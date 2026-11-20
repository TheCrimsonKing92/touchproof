using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class DependencyIndexCensusProbeTests
{
    [Fact]
    public void Collect_namespace_cycles_and_dependency_model_differences()
    {
        DependencyIndexMeasurementProbe.RequireCheckpointInput();
        var assemblies = new[]
        {
            typeof(AgentOrchestratorKernel).Assembly, typeof(GoalAcceptanceVerifier).Assembly,
            typeof(CohortAcceptanceStore).Assembly, typeof(ProviderSmokeTester).Assembly,
            typeof(SubscriptionPlanBuilder).Assembly
        };
        var index = DependencyIndexCache.Get(assemblies);
        var architecture = SharedArchitectureCache.Get(assemblies);
        var assemblyNames = assemblies.Select(assembly => assembly.GetName().Name!).ToHashSet(StringComparer.Ordinal);
        var legacyTypes = architecture.Types.Select(type => (type.Assembly.Name, Name: Normalize(type.FullName)))
            .ToHashSet();
        var indexTypes = index.Types.Select(type => (type.AssemblyName, Name: type.FullName)).ToHashSet();
        var legacyEdges = architecture.Types
            .SelectMany(type => type.Dependencies.Select(edge =>
                (SourceAssembly: type.Assembly.Name, Source: Normalize(type.FullName),
                    TargetAssembly: edge.Target.Assembly?.Name ?? "", Target: Normalize(edge.Target.FullName))))
            .Where(edge => assemblyNames.Contains(edge.TargetAssembly)).ToHashSet();
        var indexEdges = index.Types
            .SelectMany(type => type.References.Select(target =>
                (SourceAssembly: type.AssemblyName, Source: type.FullName,
                    TargetAssembly: target.AssemblyName, Target: target.FullName)))
            .Where(edge => assemblyNames.Contains(edge.TargetAssembly)).ToHashSet();

        var cycles = new[] { assemblies[4], assemblies[0], assemblies[1] }.Select(assembly =>
        {
            var name = assembly.GetName().Name!;
            var paths = index.NamespaceCycles(name);
            return new { Assembly = name, Count = paths.Length, Paths = paths };
        }).ToArray();

        DependencyIndexMeasurementProbe.Save("census", new
        {
            AppModuleVersionId = assemblies[4].ManifestModule.ModuleVersionId,
            Cycles = cycles,
            LegacyOnlyTypes = Summarize(legacyTypes.Except(indexTypes).Select(type => $"{type.Item1}: {type.Name}")),
            IndexOnlyTypes = Summarize(indexTypes.Except(legacyTypes).Select(type => $"{type.Item1}: {type.Name}")),
            LegacyOnlyEdges = Summarize(legacyEdges.Except(indexEdges).Select(Describe)),
            IndexOnlyEdges = Summarize(indexEdges.Except(legacyEdges).Select(Describe))
        });
    }

    private static string Normalize(string fullName) => fullName.Replace('/', '+');

    private static string Describe((string SourceAssembly, string Source, string TargetAssembly, string Target) edge) =>
        $"{edge.SourceAssembly}: {edge.Source} -> {edge.TargetAssembly}: {edge.Target}";

    private static object Summarize(IEnumerable<string> entries)
    {
        var ordered = entries.Order(StringComparer.Ordinal).ToArray();
        return new { Count = ordered.Length, Examples = ordered.Take(25).ToArray() };
    }
}
