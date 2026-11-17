using Mcg.AgentOrchestrator.Core;

public sealed class DeveloperCriteriaSelfCheckParserTests
{
    [Fact]
    public void ParsesEveryStatusWithOriginalIndexesAndEvidence()
    {
        var result = DeveloperCriteriaSelfCheck.Parse("""
            [{"criterion_index":0,"status":"proven","evidence":"Tests.Outcome fails without change"},{"criterion_index":3,"status":"not-owned","evidence":"Reviewer"},{"criterion_index":5,"status":"unmet","evidence":"Missing assertion"}]
            """);
        Assert.Equal(DeveloperSelfCheckFieldState.Present, result.State);
        Assert.Null(result.Reason);
        Assert.Equal(new[]
        {
            new DeveloperCriterionSelfCheck(0, DeveloperCriterionStatus.Proven, "Tests.Outcome fails without change"),
            new DeveloperCriterionSelfCheck(3, DeveloperCriterionStatus.NotOwned, "Reviewer"),
            new DeveloperCriterionSelfCheck(5, DeveloperCriterionStatus.Unmet, "Missing assertion")
        }, result.Entries);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void BlankFieldIsMissing(string? value)
    {
        var result = DeveloperCriteriaSelfCheck.Parse(value);
        Assert.Equal(DeveloperSelfCheckFieldState.Missing, result.State);
        Assert.Empty(result.Entries);
    }

    [Theory]
    [InlineData("[{", "JSON")]
    [InlineData("{}", "JSON")]
    [InlineData("null", "JSON")]
    [InlineData("[null]", "criterion_index")]
    [InlineData("[{\"status\":\"proven\",\"evidence\":\"test\"}]", "criterion_index")]
    [InlineData("[{\"criterion_index\":-1,\"status\":\"proven\",\"evidence\":\"test\"}]", "criterion_index")]
    [InlineData("[{\"criterion_index\":0,\"status\":\"done\",\"evidence\":\"test\"}]", "status")]
    [InlineData("[{\"criterion_index\":0,\"status\":\"proven\",\"evidence\":\"  \"}]", "evidence")]
    [InlineData("[\n]", "one line")]
    public void MalformedFieldReturnsSpecificReasonWithoutThrowing(string value, string reason)
    {
        var result = DeveloperCriteriaSelfCheck.Parse(value);
        Assert.Equal(DeveloperSelfCheckFieldState.Malformed, result.State);
        Assert.Empty(result.Entries);
        Assert.Contains(reason, result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyArrayAndOverlongEvidenceRemainPresent()
    {
        var empty = DeveloperCriteriaSelfCheck.Parse("[]");
        Assert.Equal(DeveloperSelfCheckFieldState.Present, empty.State);
        Assert.Empty(empty.Entries);
        var evidence = new string('x', 600) + "TAIL";
        var result = DeveloperCriteriaSelfCheck.Parse(
            $"[{{\"criterion_index\":0,\"status\":\"proven\",\"evidence\":\"{evidence}\"}}]");
        Assert.Equal(evidence, Assert.Single(result.Entries).Evidence);
    }

    [Fact]
    public void LatestBlockAndLastFieldWinWithoutOlderFallback()
    {
        var earlier = "WORKER_RESULT:\ncriteria_self_check: []\nEND_WORKER_RESULT\n";
        var missing = DeveloperCriteriaSelfCheck.ParseWorkerOutput(earlier + "WORKER_RESULT:\nfiles: none\nEND_WORKER_RESULT");
        Assert.Equal(DeveloperSelfCheckFieldState.Missing, missing.State);
        var latest = DeveloperCriteriaSelfCheck.ParseWorkerOutput(earlier +
            "WORKER_RESULT:\ncriteria_self_check: broken\n- **criteria_self_check**: []\nEND_WORKER_RESULT\ncriteria_self_check: broken");
        Assert.Equal(DeveloperSelfCheckFieldState.Present, latest.State);
        Assert.Empty(latest.Entries);
        Assert.Equal(DeveloperSelfCheckFieldState.Missing, DeveloperCriteriaSelfCheck.ParseWorkerOutput(null).State);
    }

    [Theory]
    [InlineData("criteria_self_check:", true)]
    [InlineData("**criteria_self_check:**", true)]
    [InlineData("**criteria_self_check**:", true)]
    [InlineData("criteria_self_check:", false)]
    [InlineData("**criteria_self_check:**", false)]
    [InlineData("**criteria_self_check**:", false)]
    public void LabelForms_WorkerOutput_ReturnSameEntries(string label, bool inBlock)
    {
        const string value = """[{"criterion_index":0,"status":"proven","evidence":"Tests.Outcome fails without change"},{"criterion_index":3,"status":"not-owned","evidence":"Reviewer"},{"criterion_index":5,"status":"unmet","evidence":"Missing assertion"}]""";
        var output = label + " " + value;
        if (inBlock) output = "WORKER_RESULT:\n" + output + "\nEND_WORKER_RESULT";

        var result = DeveloperCriteriaSelfCheck.ParseWorkerOutput(output);

        Assert.Equal(DeveloperSelfCheckFieldState.Present, result.State);
        Assert.Null(result.Reason);
        Assert.Equal(new[]
        {
            new DeveloperCriterionSelfCheck(0, DeveloperCriterionStatus.Proven, "Tests.Outcome fails without change"),
            new DeveloperCriterionSelfCheck(3, DeveloperCriterionStatus.NotOwned, "Reviewer"),
            new DeveloperCriterionSelfCheck(5, DeveloperCriterionStatus.Unmet, "Missing assertion")
        }, result.Entries);
    }

    [Theory]
    [InlineData("criteria_self_check:", true)]
    [InlineData("**criteria_self_check:**", true)]
    [InlineData("**criteria_self_check**:", true)]
    [InlineData("criteria_self_check:", false)]
    [InlineData("**criteria_self_check:**", false)]
    [InlineData("**criteria_self_check**:", false)]
    public void LabelForms_InvalidPayload_ReturnExactMalformedReason(string label, bool inBlock)
    {
        (string Value, string Reason)[] cases =
        [
            ("[{", "Invalid JSON array or entry shape."),
            ("""[{"criterion_index":0,"status":"done","evidence":"test"}]""",
                "status must be proven, not-owned or unmet."),
            ("""[{"criterion_index":0,"status":"proven","evidence":"  "}]""",
                "evidence must be non-empty."),
            ("[] []", "Invalid JSON array or entry shape.")
        ];
        foreach (var (value, reason) in cases)
        {
            var output = label + " " + value;
            if (inBlock) output = "WORKER_RESULT:\n" + output + "\nEND_WORKER_RESULT";

            var result = DeveloperCriteriaSelfCheck.ParseWorkerOutput(output);

            Assert.Equal(DeveloperSelfCheckFieldState.Malformed, result.State);
            Assert.Empty(result.Entries);
            Assert.Equal(reason, result.Reason);
        }
    }

    [Theory]
    [InlineData("criteria_self_check:", true)]
    [InlineData("**criteria_self_check:**", true)]
    [InlineData("**criteria_self_check**:", true)]
    [InlineData("criteria_self_check:", false)]
    [InlineData("**criteria_self_check:**", false)]
    [InlineData("**criteria_self_check**:", false)]
    public void JsonValue_SplitterAndWorkerOutput_PreserveInteriorContent(string label, bool inBlock)
    {
        const string value = """[ {"criterion_index":0, "status":"unmet", "evidence":"see **note** _under_score_: C:\\work:42 \"quoted\"\nnext"} ]""";
        var line = label + " \t" + value + "\t ";
        Assert.True(WorkerResultMarkdownEmphasis.TrySplitField(line, out var parsedLabel, out var parsedValue));
        Assert.Equal("criteria_self_check", parsedLabel);
        Assert.Equal(value, parsedValue);
        var output = inBlock ? "WORKER_RESULT:\n" + line + "\nEND_WORKER_RESULT" : line;

        var result = DeveloperCriteriaSelfCheck.ParseWorkerOutput(output);

        Assert.Equal(DeveloperSelfCheckFieldState.Present, result.State);
        Assert.Null(result.Reason);
        Assert.Equal(new DeveloperCriterionSelfCheck(0, DeveloperCriterionStatus.Unmet,
            "see **note** _under_score_: C:\\work:42 \"quoted\"\nnext"), Assert.Single(result.Entries));
    }

    [Theory]
    [InlineData("**criteria_self_check:", true, null)]
    [InlineData("criteria_self_check:**", false, "Invalid JSON array or entry shape.")]
    [InlineData("*criteria_self_check:*", false, "Invalid JSON array or entry shape.")]
    public void UnbalancedDecoration_WorkerOutput_KeepsNormalizerOutcome(
        string label, bool accepted, string? reason)
    {
        const string value = """[{"criterion_index":0,"status":"proven","evidence":"test"}]""";
        var result = DeveloperCriteriaSelfCheck.ParseWorkerOutput(
            "WORKER_RESULT:\n" + label + " " + value + "\nEND_WORKER_RESULT");

        Assert.Equal(accepted ? DeveloperSelfCheckFieldState.Present : DeveloperSelfCheckFieldState.Malformed,
            result.State);
        Assert.Equal(reason, result.Reason);
        if (accepted)
            Assert.Equal(new DeveloperCriterionSelfCheck(0, DeveloperCriterionStatus.Proven, "test"),
                Assert.Single(result.Entries));
        else
            Assert.Empty(result.Entries);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConflictingFields_LastFieldWins_EvenWhenMalformed(bool validLast)
    {
        const string valid = """**criteria_self_check:** [{"criterion_index":0,"status":"unmet","evidence":"Missing assertion"}]""";
        const string invalid = """**criteria_self_check:** [{"criterion_index":0,"status":"done","evidence":"test"}]""";
        var lines = validLast ? invalid + "\n" + valid : valid + "\n" + invalid;

        var result = DeveloperCriteriaSelfCheck.ParseWorkerOutput("WORKER_RESULT:\n" + lines + "\nEND_WORKER_RESULT");

        Assert.Equal(validLast ? DeveloperSelfCheckFieldState.Present : DeveloperSelfCheckFieldState.Malformed,
            result.State);
        if (validLast)
        {
            Assert.Null(result.Reason);
            Assert.Equal(new DeveloperCriterionSelfCheck(0, DeveloperCriterionStatus.Unmet, "Missing assertion"),
                Assert.Single(result.Entries));
        }
        else
        {
            Assert.Equal("status must be proven, not-owned or unmet.", result.Reason);
            Assert.Empty(result.Entries);
        }
    }

    [Theory]
    [InlineData("no field here", false, "", "")]
    [InlineData("**criteria_self_check**", false, "", "")]
    [InlineData(": value:tail", true, "", "value:tail")]
    [InlineData("label:", true, "label", "")]
    [InlineData(" label : value:tail ", true, "label", "value:tail")]
    public void FieldLine_ColonBoundary_ReturnsTrimmedLabelAndValue(
        string line, bool expected, string expectedLabel, string expectedValue)
    {
        Assert.Equal(expected, WorkerResultMarkdownEmphasis.TrySplitField(line, out var label, out var value));
        Assert.Equal(expectedLabel, label);
        Assert.Equal(expectedValue, value);
    }
}
