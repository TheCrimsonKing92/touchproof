using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: in-memory commands and deterministic fake execution only.
public sealed class UnitMeasurementPrerequisiteBuildFailureTests
{
    [Fact]
    public void Collect_PrerequisiteFails_SkipsTestAndNamesBuildFailure()
    {
        var unit = Commands("tests/Failed.csproj");
        var fake = new RecordingUnitCommandMeasurer
        {
            Result = (_, kind) => kind == UnitCommandKinds.Build
                ? UnitCommandMeasurementResult.Failed("fixture build failure",
                    UnitCommandMeasurementFailureCause.ExitedNonZero, "build diagnostic")
                : UnitCommandMeasurementResult.Succeeded(3.25)
        };
        var questions = new List<ProjectOwnerQuestion>();

        var measurements = UnitMeasurementCollector.Collect("fixture-root", [unit], fake,
            UnitCommandKinds.Test, questions);

        Assert.Equal(new[] {
            ("fixture-root", unit.UnitId, UnitCommandKinds.Build, unit.BuildCommand.Value)
        }, fake.Calls);
        Assert.Empty(measurements);
        var question = Assert.Single(questions);
        Assert.Equal($"commands/{unit.UnitId}/test/duration", question.FactKey);
        Assert.Equal(unit.BuildCommand.Source, question.Source);
        Assert.Contains("prerequisite build", question.Question);
        Assert.Contains("fixture build failure", question.Question);
    }

    [Fact]
    public void Collect_FirstPrerequisiteFails_ContinuesWithNextUnit()
    {
        var failed = Commands("tests/Failed.csproj");
        var next = Commands("tests/Next.csproj");
        var fake = new RecordingUnitCommandMeasurer
        {
            Result = (id, kind) => id == failed.UnitId && kind == UnitCommandKinds.Build
                ? UnitCommandMeasurementResult.Failed("first build failed",
                    UnitCommandMeasurementFailureCause.ExitedNonZero)
                : UnitCommandMeasurementResult.Succeeded(kind == UnitCommandKinds.Build ? 12.5 : 3.25)
        };
        var questions = new List<ProjectOwnerQuestion>();

        var measurements = UnitMeasurementCollector.Collect("fixture-root", [failed, next], fake,
            UnitCommandKinds.Test, questions);

        Assert.Equal(new[] {
            ("fixture-root", failed.UnitId, UnitCommandKinds.Build, failed.BuildCommand.Value),
            ("fixture-root", next.UnitId, UnitCommandKinds.Build, next.BuildCommand.Value),
            ("fixture-root", next.UnitId, UnitCommandKinds.Test, next.TestCommand!.Value)
        }, fake.Calls);
        var measurement = Assert.Single(measurements);
        Assert.Equal(next.UnitId, measurement.UnitId);
        Assert.Null(measurement.BuildSeconds);
        Assert.Equal(3.25, measurement.TestSeconds!.Value);
        Assert.Equal($"commands/{failed.UnitId}/test/duration", Assert.Single(questions).FactKey);
    }

    [Theory]
    [InlineData(FactConfidence.Medium)]
    [InlineData(FactConfidence.Low)]
    public void Collect_UnconfirmedPrerequisite_ExecutesNeitherCommand(FactConfidence confidence)
    {
        var unit = Commands("tests/Unconfirmed.csproj", buildConfidence: confidence);
        var fake = new RecordingUnitCommandMeasurer();
        var questions = new List<ProjectOwnerQuestion>();

        var measurements = UnitMeasurementCollector.Collect("fixture-root", [unit], fake,
            UnitCommandKinds.Test, questions);

        Assert.Empty(fake.Calls);
        Assert.Empty(measurements);
        var question = Assert.Single(questions);
        Assert.Equal($"commands/{unit.UnitId}/test/duration", question.FactKey);
        Assert.Equal(unit.BuildCommand.Source, question.Source);
        Assert.Contains("confirm the prerequisite build command", question.Question);
    }

    [Theory]
    [InlineData(FactConfidence.Medium)]
    [InlineData(FactConfidence.Low)]
    public void Collect_UnconfirmedTest_ExecutesNeitherCommand(FactConfidence confidence)
    {
        var unit = Commands("tests/Unconfirmed.csproj", testConfidence: confidence);
        var fake = new RecordingUnitCommandMeasurer();
        var questions = new List<ProjectOwnerQuestion>();

        var measurements = UnitMeasurementCollector.Collect("fixture-root", [unit], fake,
            UnitCommandKinds.Test, questions);

        Assert.Empty(fake.Calls);
        Assert.Empty(measurements);
        var question = Assert.Single(questions);
        Assert.Equal($"commands/{unit.UnitId}/test/duration", question.FactKey);
        Assert.Equal(unit.TestCommand!.Source, question.Source);
        Assert.Contains("confirm the command", question.Question);
    }

    private static UnitCommands Commands(string unitId,
        FactConfidence buildConfidence = FactConfidence.High,
        FactConfidence testConfidence = FactConfidence.High) => new(unitId,
        new ProjectFact<string>($"dotnet build {unitId}", new FactSource(path: unitId, line: 1), buildConfidence),
        new ProjectFact<string>($"dotnet test {unitId} --no-build", new FactSource(path: unitId, line: 2), testConfidence));
}
