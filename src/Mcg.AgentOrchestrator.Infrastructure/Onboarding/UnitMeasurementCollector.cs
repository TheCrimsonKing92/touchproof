using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class UnitMeasurementCollector
{
    public static IReadOnlyList<UnitMeasurement> Collect(string root, List<UnitCommands> commands,
        IUnitCommandMeasurer? measurer, UnitCommandKinds kinds, List<ProjectOwnerQuestion> questions)
    {
        if ((kinds & ~UnitCommandKinds.All) != 0)
            throw new ArgumentOutOfRangeException(nameof(kinds));
        var measurements = new List<UnitMeasurement>();
        if (measurer is null || kinds == UnitCommandKinds.None)
            return measurements;
        foreach (var unit in commands)
        {
            var build = Measure(unit, UnitCommandKinds.Build, unit.BuildCommand);
            ProjectFact<double>? test = null;
            if (kinds.HasFlag(UnitCommandKinds.Test) && unit.TestCommand is not null)
            {
                if (kinds.HasFlag(UnitCommandKinds.Build) && build is null)
                    Skip(unit, "test", unit.TestCommand, "Test measurement skipped because the requested build did not succeed.");
                else if (kinds.HasFlag(UnitCommandKinds.Build) || PrerequisiteBuildSucceeded(unit))
                    test = Measure(unit, UnitCommandKinds.Test, unit.TestCommand);
            }
            if (build is not null || test is not null)
                measurements.Add(new UnitMeasurement(unit.UnitId, build, test));
        }
        return measurements;

        ProjectFact<double>? Measure(UnitCommands unit, UnitCommandKinds kind, ProjectFact<string> command)
        {
            if (!kinds.HasFlag(kind)) return null;
            var label = kind == UnitCommandKinds.Build ? "build" : "test";
            if (command.Confidence != FactConfidence.High)
            {
                Skip(unit, label, command, "Measurement skipped; confirm the command before executing it.");
                return null;
            }
            var result = measurer.Measure(root, unit.UnitId, kind, command.Value);
            if (result.Seconds is { } seconds)
                return new ProjectFact<double>(seconds, new FactSource(measurementReference:
                    $"measurement:project-discover/{unit.UnitId}/{label}"), FactConfidence.High);
            Skip(unit, label, command, $"Measurement failed: the {label} command failed with cause {FailureDetails(result)}");
            return null;
        }

        bool PrerequisiteBuildSucceeded(UnitCommands unit)
        {
            if (unit.TestCommand!.Confidence != FactConfidence.High)
            {
                Skip(unit, "test", unit.TestCommand, "Measurement skipped; confirm the command before executing it.");
                return false;
            }
            if (unit.BuildCommand.Confidence != FactConfidence.High)
            {
                Skip(unit, "test", unit.BuildCommand,
                    "Test measurement skipped; confirm the prerequisite build command before executing it.");
                return false;
            }
            var result = measurer.Measure(root, unit.UnitId, UnitCommandKinds.Build, unit.BuildCommand.Value);
            if (result.Seconds is not null) return true;
            Skip(unit, "test", unit.BuildCommand,
                $"Test measurement skipped: the prerequisite build command failed with cause {FailureDetails(result)}");
            return false;
        }

        static string FailureDetails(UnitCommandMeasurementResult result) =>
            $"{result.Cause}. {result.Failure} Output tail:" +
            (string.IsNullOrEmpty(result.OutputTail) ? " (no output captured)" : $"\n{result.OutputTail}");

        void Skip(UnitCommands unit, string label, ProjectFact<string> command, string reason) =>
            ProjectFactHelpers.Ask($"commands/{unit.UnitId}/{label}/duration", reason, command.Source, questions);
    }
}
