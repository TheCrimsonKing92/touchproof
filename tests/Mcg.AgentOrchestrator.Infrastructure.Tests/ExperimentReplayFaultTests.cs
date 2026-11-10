using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using static ExperimentReplayTestData;

// Parallel-safe: oracle evaluation uses private immutable execution receipts.
public sealed class ExperimentReplayFaultTests
{
    [Theory]
    [InlineData("reflection-signature")]
    [InlineData("renamed-fixture")]
    public void ReferenceDiscriminates_PositiveRequiresControlAndExactFailure(string shape)
    {
        var result = Evaluate(shape, Control(), Reference(), Mutant("A"));
        Assert.Equal("positive", result.State);
        Assert.Equal(new[] { "Example.Defect" }, result.ConfirmedDefects);
        Assert.Equal(new[] { "Example.Defect" }, result.ACatches);
        Assert.Empty(result.BCatches);
        Assert.Equal("unsupported/nonpositive", Evaluate(shape, Control(), Reference() with { FailingTests = ["Unrelated.Test"] }).State);
    }

    [Fact]
    public void DeletedAssertion_WithoutDefectControl_IsUnsupported()
    {
        Assert.Equal("unsupported/nonpositive", Evaluate("deleted-assertion-plus-defect", Control(), Reference()).State);
        var original = Reference() with { Slot = Reference().Slot with { Stage = "defect-control" } };
        Assert.Equal("positive", Evaluate("deleted-assertion-plus-defect", Control(), Reference(), original).State);
        Assert.Equal("unsupported/nonpositive", Evaluate("deleted-assertion-plus-defect", Control(), original).State);
    }

    [Fact]
    public void ParameterizedRows_UnrelatedRowCannotEstablishReferenceOrCatch()
    {
        using var fixture = new ExperimentReplayTestData();
        var trx = Path.Combine(fixture.Root, "rows.trx");
        File.WriteAllText(trx, """
            <TestRun><Results>
              <UnitTestResult testId="2" testName="Parses_case(value: 2)" outcome="Failed"/>
              <UnitTestResult testId="3" testName="Parses_case(value: 3)" outcome="Failed"/>
            </Results><TestDefinitions>
              <UnitTest id="2"><TestMethod className="Sample.Tests.ParserTests" name="Parses_case"/></UnitTest>
              <UnitTest id="3"><TestMethod className="Sample.Tests.ParserTests" name="Parses_case"/></UnitTest>
            </TestDefinitions></TestRun>
            """);
        var rows = ExperimentReplayEvidence.Tests(trx);
        Assert.Equal("Sample.Tests.ParserTests.Parses_case(value: 2)", rows[0].Identity);
        Assert.Equal("Sample.Tests.ParserTests.Parses_case(value: 3)", rows[1].Identity);
        var run = new ExperimentReplayRun("run", "registration", "complete", null, Now, Now);
        var definition = new ExperimentReplayFaultRunner.Definition("case-000", "reflection-signature", "patch", [rows[0].Identity]);
        var wrongReference = Reference() with { FailingTests = [rows[1].Identity] };
        var unsupported = ExperimentReplayFaultRunner.Evaluate("fault", run, Registration(), definition,
            [new("patch", "hash")], [Control(), wrongReference]);
        Assert.Equal("unsupported/nonpositive", unsupported.State);
        var rightReference = Reference() with { FailingTests = [rows[0].Identity] };
        var unrelatedMutant = Mutant("A") with { FailingTests = [rows[1].Identity] };
        var positive = ExperimentReplayFaultRunner.Evaluate("fault", run, Registration(), definition,
            [new("patch", "hash")], [Control(), rightReference, unrelatedMutant]);
        Assert.Equal("positive", positive.State);
        Assert.Empty(positive.ACatches);

        var registration = Registration();
        registration = registration with { Cases = registration.Cases.Select(c => c.Id != "case-000" ? c : c with {
            Gates = [new("gate", Now, "test-failed", [rows[0].Identity], [])] }).ToArray() };
        var mismatched = ExperimentReplayReportWriter.Create(registration, run, [wrongReference]);
        Assert.Equal(0, mismatched.ConfirmedCatchDenominator);
        var measured = unrelatedMutant with { Slot = unrelatedMutant.Slot with { Stage = "AB" } };
        var matched = ExperimentReplayReportWriter.Create(registration, run, [rightReference, measured]);
        Assert.Equal(1, matched.ConfirmedCatchDenominator);
        Assert.Equal(0, matched.ACaught);
    }

    private static ExperimentReplayFault Evaluate(string shape, params ExperimentReplayExecution[] executions) =>
        ExperimentReplayFaultRunner.Evaluate("fault", new("run", "registration", "complete", null, Now, Now), Registration(),
            new("case-000", shape, "patch", ["Example.Defect"], "defect-patch"), [new("patch", "hash")], executions);
    private static ExperimentReplayExecution Control() => Execution(new(0, "case-000", "control", 0, "B", false));
    private static ExperimentReplayExecution Reference() => Execution(new(1, "case-000", "reference", 0, "B", false), state: "test-failed") with { FailingTests = ["Example.Defect"] };
    private static ExperimentReplayExecution Mutant(string arm) => Execution(new(2, "case-000", "mutant", 0, arm, false), state: "test-failed") with { FailingTests = ["Example.Defect"] };
}
