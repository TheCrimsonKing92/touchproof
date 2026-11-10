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

    private static ExperimentReplayFault Evaluate(string shape, params ExperimentReplayExecution[] executions) =>
        ExperimentReplayFaultRunner.Evaluate("fault", new("run", "registration", "complete", null, Now, Now), Registration(),
            new("case-000", shape, "patch", ["Example.Defect"], "defect-patch"), [new("patch", "hash")], executions);
    private static ExperimentReplayExecution Control() => Execution(new(0, "case-000", "control", 0, "B", false));
    private static ExperimentReplayExecution Reference() => Execution(new(1, "case-000", "reference", 0, "B", false), state: "test-failed") with { FailingTests = ["Example.Defect"] };
    private static ExperimentReplayExecution Mutant(string arm) => Execution(new(2, "case-000", "mutant", 0, arm, false), state: "test-failed") with { FailingTests = ["Example.Defect"] };
}
