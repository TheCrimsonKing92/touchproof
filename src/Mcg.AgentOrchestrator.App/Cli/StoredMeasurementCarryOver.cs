using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

/// <summary>Retains observations only for surviving units and unchanged command values.</summary>
internal static class StoredMeasurementCarryOver
{
    public static ProjectModel Apply(ProjectModel fresh, ProjectModel stored)
    {
        var unitIds = fresh.Units.Select(unit => unit.Id).ToHashSet(StringComparer.Ordinal);
        var freshCommands = fresh.Commands.ToDictionary(command => command.UnitId, StringComparer.Ordinal);
        var storedCommands = stored.Commands.Where(command => command is not null)
            .DistinctBy(command => command.UnitId, StringComparer.Ordinal)
            .ToDictionary(command => command.UnitId, StringComparer.Ordinal);
        var measurements = new List<UnitMeasurement>();
        foreach (var measurement in stored.Measurements)
        {
            if (measurement is null || !unitIds.Contains(measurement.UnitId) ||
                !freshCommands.TryGetValue(measurement.UnitId, out var current) ||
                !storedCommands.TryGetValue(measurement.UnitId, out var previous))
                continue;

            var build = string.Equals(current.BuildCommand.Value, previous.BuildCommand.Value, StringComparison.Ordinal)
                ? measurement.BuildSeconds : null;
            var test = string.Equals(current.TestCommand?.Value, previous.TestCommand?.Value, StringComparison.Ordinal)
                ? measurement.TestSeconds : null;
            if (build is null && test is null)
                continue;
            measurements.Add(build == measurement.BuildSeconds && test == measurement.TestSeconds
                ? measurement : new UnitMeasurement(measurement.UnitId, build, test));
        }
        return fresh with { Measurements = measurements.AsReadOnly() };
    }

    public static string SummaryLine(ProjectModel model)
    {
        var kept = model.Measurements.Select(measurement => measurement.UnitId)
            .Distinct(StringComparer.Ordinal).Count();
        var measuredTests = model.Measurements.Where(measurement => measurement.TestSeconds is not null)
            .Select(measurement => measurement.UnitId).ToHashSet(StringComparer.Ordinal);
        var missing = model.Commands.Where(command => command.TestCommand is not null)
            .Select(command => command.UnitId).Distinct(StringComparer.Ordinal)
            .Count(id => !measuredTests.Contains(id));
        return $"Kept {kept} unit measurements; {missing} test units have no measured test duration";
    }
}
