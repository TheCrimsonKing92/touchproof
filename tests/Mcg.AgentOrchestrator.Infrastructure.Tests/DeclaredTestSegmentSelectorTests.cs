using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: only immutable in-memory identities and rows are used.
public sealed class DeclaredTestSegmentSelectorTests
{
    [Theory]
    [InlineData("pkg::filter::tests::x", "pkg::filter::tests::x", true)]
    [InlineData("pkg::filter", "pkg::filter::tests::x", true)]
    [InlineData("filter::tests", "pkg::filter::tests::x", true)]
    [InlineData("tests::x", "pkg::filter::tests::x", true)]
    [InlineData("x", "pkg::filter::tests::x", true)]
    [InlineData("filter::tests", "pkg::filter::filter::tests::x", true)]
    [InlineData("filter::tests", "pkg::filter::other::tests::x", false)]
    [InlineData("filter::tests::x::extra", "pkg::filter::tests::x", false)]
    [InlineData("pkg::filter::tests::x::extra", "pkg::filter::tests::x", false)]
    [InlineData("ilter::tests", "pkg::filter::tests::x", false)]
    [InlineData("filter::test", "pkg::filter::tests::x", false)]
    [InlineData("follow_pattern", "pkg::follow_patterns::x", false)]
    [InlineData("Filter::tests", "pkg::filter::tests::x", false)]
    [InlineData("filter::tests ", "pkg::filter::tests::x", false)]
    [InlineData(" filter::tests", "pkg::filter::tests::x", false)]
    [InlineData("filter::tests", "pkg:: filter::tests::x", false)]
    [InlineData("", "pkg::tests::x", false)]
    [InlineData("", "pkg::::x", false)]
    [InlineData("", "", false)]
    [InlineData("tests", "", false)]
    [InlineData("filter::::tests", "pkg::filter::::tests::x", true)]
    [InlineData("filter::tests", "pkg::filter::::tests::x", false)]
    public void Matches_ContiguousWholeSegments_UsesOrdinalEquality(
        string selector, string identity, bool expected)
    {
        Assert.Equal(expected, DeclaredTestSegmentSelector.Matches(selector, identity));
    }

    [Theory]
    [InlineData("process-inspector::tree_cli.pid_json_lists_spawned_child_under_test_process_with_same_integrity",
        "process-inspector::tree_cli::pid_json_lists_spawned_child_under_test_process_with_same_integrity", true)]
    [InlineData("process-inspector.tree::tests::groups_children_sorted_and_marks_gone_parents",
        "process-inspector::tree::tests::groups_children_sorted_and_marks_gone_parents", true)]
    [InlineData("process-inspector.tree_cli.pid_json_x", "process-inspector::tree_cli::pid_json_x", true)]
    [InlineData("tree_cli.pid_json_x", "process-inspector::tree_cli::pid_json_x", true)]
    [InlineData("tree_cli.pid_json_y", "process-inspector::tree_cli::pid_json_x", false)]
    [InlineData("tree_gui.pid_json_x", "process-inspector::tree_cli::pid_json_x", false)]
    [InlineData("Tree_cli.pid_json_x", "process-inspector::tree_cli::pid_json_x", false)]
    [InlineData("tree_cl.pid_json_x", "process-inspector::tree_cli::pid_json_x", false)]
    [InlineData("tree_cli.pid_json", "process-inspector::tree_cli::pid_json_x", false)]
    [InlineData("process-inspector.pid_json_x", "process-inspector::tree_cli::pid_json_x", false)]
    [InlineData("file.Describe", "file::Describe.It", false)]
    public void Matches_DottedSelector_RequiresWholeOrdinalSegments(
        string selector, string identity, bool expected)
    {
        Assert.Equal(expected, DeclaredTestSegmentSelector.Matches(selector, identity));
    }

    [Theory]
    [InlineData("Passed", 1, 0, true)]
    [InlineData("Failed", 0, 1, false)]
    public void Evaluate_DottedSelector_GradesSeparatorSpelledRow(
        string outcome, int passed, int failed, bool satisfied)
    {
        var result = DeclaredTestSelectorResolver.Evaluate("process-inspector::tree_cli.pid_json_x", [
            new("process-inspector::tree_cli::pid_json_x", "pid_json_x", [], outcome, "report")]);

        Assert.Equal(passed, result.Passed);
        Assert.Equal(failed, result.Failed);
        Assert.Equal(0, result.Skipped + result.Superseded);
        Assert.Equal(satisfied, result.Satisfied);
    }

    [Fact]
    public void Evaluate_RepeatedSegmentRun_CountsRowOnce()
    {
        var result = DeclaredTestSelectorResolver.Evaluate("tests", [
            new("pkg::tests::tests::x", "x", ["pkg", "tests", "tests"], "Passed", "report")]);

        Assert.Equal(1, result.Passed);
        Assert.Equal(0, result.Failed + result.Skipped + result.Superseded);
        Assert.True(result.Satisfied);
    }

    [Theory]
    [InlineData("Passed", 2, 0, true)]
    [InlineData("Failed", 1, 1, false)]
    public void Evaluate_DifferentSegmentPositions_RequiresEveryMatchedRowToPass(
        string outcome, int passed, int failed, bool satisfied)
    {
        var result = DeclaredTestSelectorResolver.Evaluate("tests", [
            new("pkg::tests::a", "a", ["pkg", "tests"], "Passed", "report"),
            new("pkg::args::tests::b", "b", ["pkg", "args", "tests"], outcome, "report")]);

        Assert.Equal(passed, result.Passed);
        Assert.Equal(failed, result.Failed);
        Assert.Equal(0, result.Skipped + result.Superseded);
        Assert.Equal(satisfied, result.Satisfied);
    }
}
