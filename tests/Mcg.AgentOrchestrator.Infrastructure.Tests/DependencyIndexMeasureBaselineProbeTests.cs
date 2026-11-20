using ArchUnitNET.Fluent;
using ArchUnitNET.Fluent.Extensions;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

public sealed class DependencyIndexMeasureBaselineProbeTests
{
    [Fact]
    public void Collect_legacy_model_peak_and_rule_time() =>
        DependencyIndexMeasurementProbe.Measure("baseline", assembly =>
        {
            var architecture = SharedArchitectureCache.Get(assembly);
            var subjects = Types().That().FollowCustomPredicate(
                type => type.Namespace.FullName.Equals("Mcg.AgentOrchestrator.App.Application", StringComparison.Ordinal),
                "reside exactly in Mcg.AgentOrchestrator.App.Application");
            string[] forbiddenNamespaces = ["Mcg.AgentOrchestrator.App.Cli", "Mcg.AgentOrchestrator.App.Rendering"];
            var forbidden = Types().That().FollowCustomPredicate(
                type => forbiddenNamespaces.Contains(type.Namespace.FullName, StringComparer.Ordinal),
                $"reside exactly in {string.Join(", ", forbiddenNamespaces)}");
            return subjects.Should().NotDependOnAny(forbidden).Evaluate(architecture).Count(result => !result.Passed);
        });
}
