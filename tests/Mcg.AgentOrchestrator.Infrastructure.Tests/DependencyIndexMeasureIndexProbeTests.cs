public sealed class DependencyIndexMeasureIndexProbeTests
{
    [Fact]
    public void Collect_index_peak_and_rule_time() =>
        DependencyIndexMeasurementProbe.Measure("index", assembly =>
        {
            var index = DependencyIndexCache.Get(assembly);
            var subjects = ApplicationLayerArchitectureTests.Subjects(index, "Mcg.AgentOrchestrator.App.Application");
            return ApplicationLayerArchitectureTests.NoDependencyRule(index, subjects,
                "Mcg.AgentOrchestrator.App.Cli", "Mcg.AgentOrchestrator.App.Rendering").Length;
        });
}
