using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class NodeUnitCommandDeriver
{
    public static (ProjectUnit Unit, UnitCommands Commands)? Derive(ProjectFact<string> framework)
    {
        if (framework.Confidence != FactConfidence.High || framework.Value != "vitest")
            return null;

        // Node verification uses stack-level checks in NodeVerificationChecks, not per-unit runner checks.
        var unit = new ProjectUnit(".", High(".", framework.Source), High(".", framework.Source),
            new ProjectFact<bool?>(false, framework.Source, FactConfidence.High));
        return (unit, new UnitCommands(unit.Id, null, High("npx vitest run", framework.Source)));
    }

    private static ProjectFact<string> High(string value, FactSource source) => new(value, source, FactConfidence.High);
}
