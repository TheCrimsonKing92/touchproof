using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerResultParserEvidenceTests
{
    [Xunit.Fact(DisplayName = "WorkerResultParser_parses_canonical_inconclusive_tests_status")]
    public void WorkerResultParserParsesCanonicalInconclusiveTestsStatus()
    {
        var text = WorkerResultBlock(
            "dotnet test --no-build --filter Focused",
            "inconclusive - process killed; no TRX");

        Assert.True(WorkerResultParser.TryParseResult(text, out var result, out var diagnostic), diagnostic);
        Assert.Equal(WorkerResultParser.TestsStatus.Inconclusive, result.TestsStatus);
        Assert.Equal("inconclusive - process killed; no TRX", result.Fields["tests"]);
        Assert.False(WorkerResultParser.TestsReportFailure(result, out _));
        Assert.False(WorkerResultParser.TryParseSuccessfulResult(text, out _, out _));
    }

    [Xunit.Theory(DisplayName = "WorkerResultParser_requires_current_round_evidence_for_inconclusive")]
    [Xunit.InlineData("inconclusive", false)]
    [Xunit.InlineData("inconclusive: process killed", false)]
    [Xunit.InlineData("inconclusive process killed", false)]
    [Xunit.InlineData("inconclusive - ", false)]
    [Xunit.InlineData("inconclusive - process killed; no TRX", true)]
    public void WorkerResultParserRequiresCurrentRoundEvidenceForInconclusive(
        string tests,
        bool expectedInconclusive)
    {
        var text = WorkerResultBlock("dotnet test --no-build --filter Focused", tests);

        var parsed = WorkerResultParser.TryParseResult(text, out var result, out var diagnostic);

        Assert.Equal(expectedInconclusive, parsed);
        Assert.Equal(
            expectedInconclusive ? WorkerResultParser.TestsStatus.Inconclusive : WorkerResultParser.TestsStatus.Unknown,
            result.TestsStatus);
        if (!expectedInconclusive)
        {
            Assert.Contains("requires", diagnostic, StringComparison.Ordinal);
        }
    }

    [Xunit.Theory(DisplayName = "WorkerResultParser_read_only_success_policy_accepts_only_known_non_failing_statuses")]
    [Xunit.InlineData("pass - source inspection complete", true)]
    [Xunit.InlineData("not-run - read-only task", true)]
    [Xunit.InlineData("deferred - implementation verification belongs to Developer", true)]
    [Xunit.InlineData("inconclusive - external artifact was unavailable", true)]
    [Xunit.InlineData("fail - source contract violated", false)]
    [Xunit.InlineData("inspection complete", false)]
    public void WorkerResultParserReadOnlySuccessPolicyAcceptsOnlyKnownNonFailingStatuses(
        string tests,
        bool expected)
    {
        var text = WorkerResultBlock("source inspection", tests);

        var parsed = WorkerResultParser.TryParseSuccessfulResult(
            text,
            out _,
            out _,
            allowNoChangedFiles: true,
            requireNoBlockers: true,
            allowReadOnlyTestStatuses: true);

        Assert.Equal(expected, parsed);
    }

    [Xunit.Theory(DisplayName = "WorkerResultParser_rejects_noncanonical_premise_invalid")]
    [Xunit.InlineData("premise-invalid")]
    [Xunit.InlineData("premise-invalid: required API does not exist")]
    [Xunit.InlineData("premise-invalid required API does not exist")]
    [Xunit.InlineData("premise-invalid - ")]
    public void WorkerResultParserRejectsNoncanonicalPremiseInvalid(string blockers)
    {
        var text = WorkerResultBlock(
            "inspected source",
            "not-run - read-only task",
            blockers);

        Assert.False(WorkerResultParser.TryParseResult(text, out _, out var diagnostic));
        Assert.Contains("premise-invalid requires", diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "WorkerResultParser_uses_latest_WORKER_RESULT_block")]
    public void WorkerResultParserUsesLatestWorkerResultBlock()
    {
        var text =
            WorkerResultBlock("stale command", "pass - stale result") +
            Environment.NewLine +
            WorkerResultBlock("latest command", "inconclusive - latest command timed out; no TRX");

        Assert.True(WorkerResultParser.TryParseResult(text, out var result, out var diagnostic), diagnostic);
        Assert.Equal(WorkerResultParser.TestsStatus.Inconclusive, result.TestsStatus);
        Assert.Equal("latest command", result.Fields["commands"]);
        Assert.Equal("inconclusive - latest command timed out; no TRX", result.Fields["tests"]);
    }

    [Xunit.Fact(DisplayName = "WorkerResultParser_rejects_unstructured_failing_tests_with_status_guidance")]
    public void WorkerResultParserRejectsUnstructuredFailingTestsWithStatusGuidance()
    {
        var text = WorkerResultBlock("focused verification", "3 failed, see log");

        Assert.True(WorkerResultParser.TryParseResult(text, out var result, out var diagnostic), diagnostic);
        Assert.Equal(WorkerResultParser.TestsStatus.Unknown, result.TestsStatus);
        Assert.True(WorkerResultParser.TestsReportFailure(result, out _));

        Assert.False(WorkerResultParser.TryParseSuccessfulResult(text, out _, out diagnostic, allowNoChangedFiles: true));
        Assert.Contains("tests field is unstructured", diagnostic, StringComparison.Ordinal);
        Assert.Contains("Restate tests with a leading status word", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("tests reported failure", diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "WorkerResultParser_accepts_leading_pass_with_failure_words_in_narrative")]
    public void WorkerResultParserAcceptsLeadingPassWithFailureWordsInNarrative()
    {
        var text = WorkerResultBlock("focused verification", "pass - verified retry path that previously failed");

        var parsed = WorkerResultParser.TryParseSuccessfulResult(text, out _, out var diagnostic, allowNoChangedFiles: true);

        Assert.True(parsed, diagnostic);
    }

    [Xunit.Fact(DisplayName = "WorkerResultParser_preserves_structured_fail_diagnostic")]
    public void WorkerResultParserPreservesStructuredFailDiagnostic()
    {
        var text = WorkerResultBlock("focused verification", "fail - 2 assertions failed");

        Assert.False(WorkerResultParser.TryParseSuccessfulResult(text, out _, out var diagnostic, allowNoChangedFiles: true));
        Assert.Equal("WORKER_RESULT tests reported failure: fail - 2 assertions failed.", diagnostic);
    }

    [Xunit.Theory]
    [Xunit.InlineData("blockers: none", true)]
    [Xunit.InlineData("**blockers:** none", true)]
    [Xunit.InlineData("**blockers**: none", true)]
    [Xunit.InlineData("blockers: none", false)]
    [Xunit.InlineData("**blockers:** none", false)]
    [Xunit.InlineData("**blockers**: none", false)]
    public void LabelForms_BlockAndScan_ReturnUndecoratedValues(string line, bool inBlock)
    {
        var text = WorkerResultBlock("source inspection", "pass - inspected")
            .Replace("blockers: none", line, StringComparison.Ordinal);
        if (!inBlock)
            text = text.Replace("WORKER_RESULT:", string.Empty, StringComparison.Ordinal)
                .Replace("END_WORKER_RESULT", string.Empty, StringComparison.Ordinal);

        Assert.True(WorkerResultParser.TryParseFields(text, out var fields, out var diagnostic), diagnostic);
        Assert.Equal("none", fields["blockers"]);
    }

    [Xunit.Fact]
    public void CapturedHaikuShape_CriteriaVerdicts_StartWithJsonArray()
    {
        // Captured label shape from c0bb706d55fb78bb1213bc2f64c6dc2507b6ffba;
        // the abbreviated payload is filled with representative arithmetic evidence.
        const string fixture = """
            **criteria_verdicts:** [{"criterion_index":0,"verdict":"met","evidence":"Arithmetic result is 4."}]
            """;
        var text = WorkerResultBlock("source inspection", "pass - inspected")
            .Replace("END_WORKER_RESULT", fixture + "\nEND_WORKER_RESULT", StringComparison.Ordinal);

        Assert.True(WorkerResultParser.TryParseFields(text, out var fields, out var diagnostic), diagnostic);
        var value = fields["criteria_verdicts"];
        Assert.StartsWith("[", value, StringComparison.Ordinal);
        Assert.DoesNotContain("**", value, StringComparison.Ordinal);
        using var json = System.Text.Json.JsonDocument.Parse(value);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, json.RootElement.ValueKind);
        Assert.Equal(0, Assert.Single(json.RootElement.EnumerateArray()).GetProperty("criterion_index").GetInt32());
    }

    [Xunit.Theory]
    [Xunit.InlineData("commands:", true)]
    [Xunit.InlineData("**commands:**", true)]
    [Xunit.InlineData("**commands**:", true)]
    [Xunit.InlineData("commands:", false)]
    [Xunit.InlineData("**commands:**", false)]
    [Xunit.InlineData("**commands**:", false)]
    public void JsonValue_BlockAndScan_PreserveInteriorBytes(string label, bool inBlock)
    {
        const string value = """[ {"evidence":"see **note** _under_score_: C:\\work:42 \"quoted\"\nnext"} ]""";
        var text = WorkerResultBlock("source inspection", "pass - inspected")
            .Replace("commands: source inspection", label + " \t" + value + "\t ", StringComparison.Ordinal);
        if (!inBlock)
            text = text.Replace("WORKER_RESULT:", string.Empty, StringComparison.Ordinal)
                .Replace("END_WORKER_RESULT", string.Empty, StringComparison.Ordinal);

        Assert.True(WorkerResultParser.TryParseFields(text, out var fields, out var diagnostic), diagnostic);
        Assert.Equal(value, fields["commands"]);
    }

    [Xunit.Theory]
    [Xunit.InlineData("**blockers: none", "none", true)]
    [Xunit.InlineData("blockers:** none", "** none", true)]
    [Xunit.InlineData("*blockers:* none", "* none", true)]
    [Xunit.InlineData("**blockers: none", "none", false)]
    [Xunit.InlineData("blockers:** none", "** none", false)]
    [Xunit.InlineData("*blockers:* none", "* none", false)]
    public void UnbalancedDecoration_BlockAndScan_KeepNormalizerOutcome(
        string line, string expected, bool inBlock)
    {
        var text = WorkerResultBlock("source inspection", "pass - inspected")
            .Replace("blockers: none", line, StringComparison.Ordinal);
        if (!inBlock)
            text = text.Replace("WORKER_RESULT:", string.Empty, StringComparison.Ordinal)
                .Replace("END_WORKER_RESULT", string.Empty, StringComparison.Ordinal);

        Assert.True(WorkerResultParser.TryParseResult(text, out var result, out var diagnostic), diagnostic);
        Assert.Equal(expected, result.Fields["blockers"]);
        Assert.Equal(expected == "none" ? WorkerResultParser.BlockersStatus.None : WorkerResultParser.BlockersStatus.Unknown,
            result.BlockersStatus);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void ConflictingFields_BlockLastAndScanFirst_PreserveBlocker(bool inBlock)
    {
        const string blocker = "exact-blocker - fixture cannot build";
        var duplicateLines = inBlock
            ? "blockers: none\n**blockers:** " + blocker
            : "**blockers:** " + blocker + "\nblockers: none";
        var text = WorkerResultBlock("source inspection", "pass - inspected")
            .Replace("blockers: none", duplicateLines, StringComparison.Ordinal);
        if (!inBlock)
            text = text.Replace("WORKER_RESULT:", string.Empty, StringComparison.Ordinal)
                .Replace("END_WORKER_RESULT", string.Empty, StringComparison.Ordinal);

        Assert.True(WorkerResultParser.TryParseResult(text, out var result, out var diagnostic), diagnostic);
        Assert.Equal(blocker, result.Fields["blockers"]);
        Assert.Equal(WorkerResultParser.BlockersStatus.Present, result.BlockersStatus);
        Assert.False(WorkerResultParser.TryParseSuccessfulResult(text, out _, out diagnostic,
            allowNoChangedFiles: true, requireNoBlockers: true));
        Assert.Equal("WORKER_RESULT reports a blocker.", diagnostic);
    }

    [Xunit.Fact]
    public void DecoratedReviewFields_NotMetAndBlockingFinding_PassThrough()
    {
        const string verdicts = """[{"criterion_index":0,"verdict":"not-met","evidence":"Missing **assertion**: _outcome_."}]""";
        const string findings = """[{"stable_id":"missing-assertion","state":"open","severity":"blocking","category":"test-coverage","location":{"file":"Parser.cs","region":"Parse"},"description":"Missing **assertion**: _outcome_."}]""";
        var text = WorkerResultBlock("source inspection", "pass - inspected")
            .Replace("END_WORKER_RESULT",
                $"**criteria_verdicts:** {verdicts}\n**findings:** {findings}\nEND_WORKER_RESULT",
                StringComparison.Ordinal);

        Assert.True(WorkerResultParser.TryParseFields(text, out var fields, out var diagnostic), diagnostic);
        Assert.Equal(verdicts, fields["criteria_verdicts"]);
        Assert.Equal(findings, fields["findings"]);
    }

    private static string WorkerResultBlock(
        string commands,
        string tests,
        string blockers = "none")
    {
        return $"""
            WORKER_RESULT:
            files: none
            commands: {commands}
            tests: {tests}
            commit: none
            blockers: {blockers}
            model_fit: OpenAI/test - adequate - parser verification - sufficient
            skills: none
            confidence: high
            END_WORKER_RESULT
            """;
    }
}
