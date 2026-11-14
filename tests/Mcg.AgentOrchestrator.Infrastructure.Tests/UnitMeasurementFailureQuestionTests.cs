using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: failures are supplied by a fake; no processes, clocks, or shared state.
public sealed class UnitMeasurementFailureQuestionTests
{
    [Theory]
    [InlineData(false, UnitCommandMeasurementFailureCause.ExitedNonZero, "ExitedNonZero")]
    [InlineData(false, UnitCommandMeasurementFailureCause.TimedOut, "TimedOut")]
    [InlineData(true, UnitCommandMeasurementFailureCause.ExitedNonZero, "ExitedNonZero")]
    [InlineData(true, UnitCommandMeasurementFailureCause.TimedOut, "TimedOut")]
    public void Collect_CommandFails_ReportsRoleCauseAndOutput(bool prerequisite,
        UnitCommandMeasurementFailureCause cause, string causeName)
    {
        const string unitId = "tests/Sample.csproj";
        const string failure = "fixture command failure";
        const string output = "stdout: compiling\nstderr: missing fixture dependency";
        var tail = cause == UnitCommandMeasurementFailureCause.ExitedNonZero ? output : string.Empty;
        var unit = new UnitCommands(unitId,
            new ProjectFact<string>($"dotnet build {unitId}", new FactSource(path: unitId, line: 1), FactConfidence.High),
            new ProjectFact<string>($"dotnet test {unitId} --no-build", new FactSource(path: unitId, line: 2), FactConfidence.High));
        var fake = new RecordingUnitCommandMeasurer
        {
            Result = (_, kind) => kind == (prerequisite ? UnitCommandKinds.Build : UnitCommandKinds.Test)
                ? UnitCommandMeasurementResult.Failed(failure, cause, tail)
                : UnitCommandMeasurementResult.Succeeded(12.5)
        };
        var questions = new List<ProjectOwnerQuestion>();

        var measurements = UnitMeasurementCollector.Collect("fixture-root", [unit], fake,
            UnitCommandKinds.Test, questions);

        var expectedCalls = new List<(string, string, UnitCommandKinds, string)>
        {
            ("fixture-root", unitId, UnitCommandKinds.Build, unit.BuildCommand.Value)
        };
        if (!prerequisite)
            expectedCalls.Add(("fixture-root", unitId, UnitCommandKinds.Test, unit.TestCommand!.Value));
        Assert.Equal(expectedCalls, fake.Calls);
        Assert.Empty(measurements);
        var question = Assert.Single(questions);
        Assert.Equal($"commands/{unitId}/test/duration", question.FactKey);
        Assert.Equal(prerequisite ? unit.BuildCommand.Source : unit.TestCommand!.Source, question.Source);
        Assert.Contains(unitId, question.Question);
        Assert.Contains(prerequisite ? "prerequisite build command failed" : "the test command failed", question.Question);
        Assert.DoesNotContain(prerequisite ? "the test command failed" : "prerequisite build command failed", question.Question);
        Assert.Contains(causeName, question.Question);
        Assert.DoesNotContain(cause == UnitCommandMeasurementFailureCause.TimedOut ? "ExitedNonZero" : "TimedOut",
            question.Question);
        Assert.Contains(failure, question.Question);
        Assert.EndsWith(tail.Length == 0 ? "Output tail: (no output captured)" : $"Output tail:\n{output}",
            question.Question);
    }
}
