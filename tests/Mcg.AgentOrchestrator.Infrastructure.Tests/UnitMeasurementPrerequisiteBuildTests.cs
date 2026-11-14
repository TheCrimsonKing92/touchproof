using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: in-memory commands and deterministic fake execution only.
public sealed class UnitMeasurementPrerequisiteBuildTests
{
    [Theory]
    [InlineData(UnitCommandKinds.Test)]
    [InlineData(UnitCommandKinds.All)]
    public void Collect_TestsRequested_BuildRunsOnceBeforeTest(UnitCommandKinds kinds)
    {
        const string unitId = "tests/Sample.csproj";
        var source = new FactSource(path: unitId, line: 1);
        var unit = new UnitCommands(unitId,
            new ProjectFact<string>("dotnet build tests/Sample.csproj", source, FactConfidence.High),
            new ProjectFact<string>("dotnet test tests/Sample.csproj --no-build", source, FactConfidence.High));
        var fake = new RecordingUnitCommandMeasurer();
        var questions = new List<ProjectOwnerQuestion>();

        var measurements = UnitMeasurementCollector.Collect("fixture-root", [unit], fake, kinds, questions);

        Assert.Equal(new[] {
            ("fixture-root", unitId, UnitCommandKinds.Build, unit.BuildCommand.Value),
            ("fixture-root", unitId, UnitCommandKinds.Test, unit.TestCommand!.Value)
        }, fake.Calls);
        var measurement = Assert.Single(measurements);
        Assert.Equal(unitId, measurement.UnitId);
        Assert.Equal(3.25, measurement.TestSeconds!.Value);
        Assert.Equal($"measurement:project-discover/{unitId}/test", measurement.TestSeconds.Source.MeasurementReference);
        if (kinds == UnitCommandKinds.Test)
            Assert.Null(measurement.BuildSeconds);
        else
            Assert.Equal(12.5, measurement.BuildSeconds!.Value);
        Assert.Empty(questions);
    }
}
