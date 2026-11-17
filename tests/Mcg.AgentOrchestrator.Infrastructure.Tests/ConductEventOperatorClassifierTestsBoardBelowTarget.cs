using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: pure classifier decision table.
public sealed class ConductEventOperatorClassifierTestsBoardBelowTarget
{
    [Theory]
    [InlineData("board-fill-draft", "BOARD_BELOW_TARGET count=3 target=9 reason=daily-cap-reached", "decision")]
    [InlineData("board-fill-draft", "BOARD_TARGET_RECOVERED count=9 target=9", "outcome")]
    [InlineData("board-fill-draft", "BOARD_BELOW_TARGET", "decision")]
    [InlineData("board-fill-draft", "BOARD_TARGET_RECOVERED", "outcome")]
    [InlineData("board-fill-draft", "BOARD_BELOW_TARGET_EXTRA", null)]
    [InlineData("board-fill-draft", "BOARD_TARGET_RECOVERED_EXTRA", null)]
    [InlineData("board-fill-draft", " BOARD_BELOW_TARGET count=3 target=9", null)]
    [InlineData("board-fill-draft", " BOARD_TARGET_RECOVERED count=9 target=9", null)]
    [InlineData("other", "BOARD_BELOW_TARGET count=3 target=9", null)]
    [InlineData("other", "BOARD_TARGET_RECOVERED count=9 target=9", null)]
    public void Exact_transition_and_kind_supply_positive_evidence(
        string kind, string detail, string? expected) =>
        Assert.Equal(expected, ConductEventOperatorClassifier.Classify(kind, detail));
}
