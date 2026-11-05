using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

public sealed class PlannerBriefAmendmentRequestTests
{
    private const string Prefix = "PLANNER_BRIEF_AMENDMENT: ";
    private const string Evidence = """
        PLANNER_EVIDENCE_REQUEST: {"criterion_index":1,"evidence_key":"parser-tests","availability":"post-implementation","owner":"acceptance-gate","needed":"passing parser tests","reason":"implementation must exist first","tests":["PlannerBriefAmendmentRequestTests"]}
        """;

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Parse_valid_amendment_returns_only_one_spec_clarification(bool withEvidence)
    {
        var output = Amendment();
        if (withEvidence)
            output += "\n" + Evidence + "\n" + Evidence.Replace("parser-tests", "replay-tests");

        var result = AgentOutputDirectives.ParseHumanInputRequest(output, AgentRole.Planner);

        Xunit.Assert.False(result.IsMalformed, result.Diagnostic);
        var directive = Xunit.Assert.Single(result.Directives);
        Xunit.Assert.Same(directive, result.Directive);
        Xunit.Assert.Equal(HumanWaitKind.SpecClarification, directive.Kind);
        Xunit.Assert.Null(directive.PlannerEvidence);
        Xunit.Assert.Equal(0, result.EvidenceLineCount);
        Xunit.Assert.Contains("criterion 1", directive.Question);
        Xunit.Assert.Contains("Defect: Existing regression contradicts the criterion.", directive.Question);
        Xunit.Assert.Contains("Evidence: tests/ExistingTests.cs:12", directive.Question);
        Xunit.Assert.Contains("Proposed text: Preserve the existing behavior.", directive.Question);
        Xunit.Assert.Contains("Fallback: Narrow the criterion.", directive.Question);
        Xunit.Assert.False(string.IsNullOrWhiteSpace(directive.BlockerFingerprint));
    }

    public static IEnumerable<object?[]> InvalidFields()
    {
        foreach (var field in new[] { "criterion_index", "defect", "evidence", "proposed_text" })
            foreach (var value in new[] { null, "null", "\"\"", "\"   \"", "true", "{}", "[]" })
                yield return [field, value];
        foreach (var value in new[] { "0", "-1", "\"1\"", "1.5", "2147483648" })
            yield return ["criterion_index", value];
        foreach (var field in new[] { "defect", "evidence", "proposed_text", "fallback_text" })
            yield return [field, "42"];
        foreach (var value in new[] { "\"File.cs\"", "\"File.cs:0\"", "\"File.cs:-1\"", "\":12\"", "\" :12\"", "\"File.cs:line\"" })
            yield return ["evidence", value];
    }

    [Xunit.Theory]
    [Xunit.MemberData(nameof(InvalidFields))]
    public void Parse_invalid_field_returns_a_named_malformed_diagnostic(string field, string? json)
    {
        var result = AgentOutputDirectives.ParseHumanInputRequest(Amendment(field, json), AgentRole.Planner);

        Xunit.Assert.True(result.IsMalformed);
        Xunit.Assert.StartsWith("Malformed PLANNER_BRIEF_AMENDMENT:", result.Diagnostic);
        Xunit.Assert.Contains(field, result.Diagnostic);
        Xunit.Assert.Null(result.Directive);
        Xunit.Assert.Empty(result.Directives);
    }

    [Xunit.Theory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData("null")]
    [Xunit.InlineData("\"\"")]
    [Xunit.InlineData("\"   \"")]
    public void Parse_optional_fallback_accepts_absence_and_reports_it(string? json)
    {
        var result = AgentOutputDirectives.ParseHumanInputRequest(Amendment("fallback_text", json), AgentRole.Planner);

        Xunit.Assert.False(result.IsMalformed, result.Diagnostic);
        Xunit.Assert.Contains("Fallback: No fallback supplied.", Xunit.Assert.Single(result.Directives).Question);
    }

    [Xunit.Fact]
    public void Parse_windows_path_and_unknown_properties_preserves_only_named_fields()
    {
        var payload = ValidPayload();
        payload["evidence"] = @"C:\repo\File.cs:12";
        payload["goal_id"] = "untrusted-goal";
        payload["task_id"] = "untrusted-task";
        payload["brief_version"] = 99;
        var result = AgentOutputDirectives.ParseHumanInputRequest(Prefix + JsonSerializer.Serialize(payload), AgentRole.Planner);

        Xunit.Assert.False(result.IsMalformed, result.Diagnostic);
        var directive = Xunit.Assert.Single(result.Directives);
        Xunit.Assert.Contains(@"Evidence: C:\repo\File.cs:12", directive.Question);
        Xunit.Assert.DoesNotContain("untrusted", directive.Question);
        Xunit.Assert.Equal(ParseDirective(Amendment()).BlockerFingerprint, directive.BlockerFingerprint);
    }

    [Xunit.Theory]
    [Xunit.InlineData("[]")]
    [Xunit.InlineData("null")]
    [Xunit.InlineData("true")]
    [Xunit.InlineData("not-json")]
    [Xunit.InlineData("{} trailing text")]
    public void Parse_invalid_payload_is_malformed(string payload)
    {
        var result = AgentOutputDirectives.ParseHumanInputRequest(Prefix + payload, AgentRole.Planner);

        Xunit.Assert.True(result.IsMalformed);
        Xunit.Assert.StartsWith("Malformed PLANNER_BRIEF_AMENDMENT:", result.Diagnostic);
    }

    [Xunit.Fact]
    public void Parse_duplicate_amendments_and_conflicting_questions_are_malformed()
    {
        var multiple = AgentOutputDirectives.ParseHumanInputRequest(Amendment() + "\n" + Amendment(), AgentRole.Planner);
        var conflict = AgentOutputDirectives.ParseHumanInputRequest(Amendment() + "\nHUMAN_INPUT: Another question?", AgentRole.Planner);
        var ambiguity = AgentOutputDirectives.ParseHumanInputRequest(
            Amendment() + "\nHUMAN_INPUT: Another question?\n" + Evidence, AgentRole.Planner);

        Xunit.Assert.True(multiple.IsMalformed);
        Xunit.Assert.Contains("criterion_index 1", multiple.Diagnostic);
        Xunit.Assert.Empty(multiple.Directives);
        Xunit.Assert.True(conflict.IsMalformed);
        Xunit.Assert.Contains("conflicts", conflict.Diagnostic);
        Xunit.Assert.Equal("Malformed PLANNER_EVIDENCE_REQUEST: substantive HUMAN_INPUT and Planner evidence are ambiguous; use evidence lines for every requirement.", ambiguity.Diagnostic);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Parse_distinct_amendments_preserves_source_order(bool withEvidence)
    {
        var output = Amendment() + "\n" + Amendment("criterion_index", "2");
        if (withEvidence) output += "\n" + Evidence;
        var result = AgentOutputDirectives.ParseHumanInputRequest(output, AgentRole.Planner);

        Xunit.Assert.False(result.IsMalformed, result.Diagnostic);
        Xunit.Assert.Null(result.Directive);
        Xunit.Assert.Equal(2, result.Directives.Count);
        Xunit.Assert.Contains("criterion 1", result.Directives[0].Question);
        Xunit.Assert.Contains("criterion 2", result.Directives[1].Question);
        Xunit.Assert.NotEqual(result.Directives[0].QuestionFingerprint, result.Directives[1].QuestionFingerprint);
        Xunit.Assert.Equal(0, result.EvidenceLineCount);
        var reversed = AgentOutputDirectives.ParseHumanInputRequest(
            Amendment("criterion_index", "2") + "\n" + Amendment(), AgentRole.Planner);
        Xunit.Assert.Equal(result.Directives.Reverse(), reversed.Directives);
    }

    [Xunit.Fact]
    public void Parse_nonadjacent_duplicates_names_first_repeated_index()
    {
        var result = AgentOutputDirectives.ParseHumanInputRequest(string.Join("\n",
            Amendment("criterion_index", "2"), Amendment(),
            Amendment("criterion_index", "2"), Amendment()), AgentRole.Planner);

        Xunit.Assert.True(result.IsMalformed);
        Xunit.Assert.Contains("criterion_index 2", result.Diagnostic);
        Xunit.Assert.Null(result.Directive);
        Xunit.Assert.Empty(result.Directives);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Parse_invalid_line_rejects_entire_batch(bool invalidFirst)
    {
        var invalid = Amendment("criterion_index", "0");
        var result = AgentOutputDirectives.ParseHumanInputRequest(invalidFirst
            ? invalid + "\n" + Amendment() : Amendment() + "\n" + invalid, AgentRole.Planner);

        Xunit.Assert.True(result.IsMalformed);
        Xunit.Assert.Equal("Malformed PLANNER_BRIEF_AMENDMENT: criterion_index must be a positive 1-based integer.", result.Diagnostic);
        Xunit.Assert.Empty(result.Directives);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Parse_plural_amendments_rejects_substantive_human_input(bool withEvidence)
    {
        var output = Amendment() + "\n" + Amendment("criterion_index", "2") +
            "\nHUMAN_INPUT: Another question?";
        if (withEvidence) output += "\n" + Evidence;

        var result = AgentOutputDirectives.ParseHumanInputRequest(output, AgentRole.Planner);

        Xunit.Assert.True(result.IsMalformed);
        Xunit.Assert.Empty(result.Directives);
        Xunit.Assert.Contains(withEvidence ? "ambiguous" : "conflicts", result.Diagnostic);
    }

    [Xunit.Fact]
    public void Parse_single_amendment_preserves_fixed_question_and_fingerprint()
    {
        var result = AgentOutputDirectives.ParseHumanInputRequest(Amendment(), AgentRole.Planner);
        var directive = Xunit.Assert.Single(result.Directives);

        Xunit.Assert.Equal(string.Join(Environment.NewLine,
            "Planner brief amendment requested for criterion 1.",
            "Defect: Existing regression contradicts the criterion.",
            "Evidence: tests/ExistingTests.cs:12",
            "Proposed text: Preserve the existing behavior.",
            "Fallback: Narrow the criterion.",
            "What is the ruling on this criterion amendment?"), directive.Question);
        const string fingerprint = "1497d45de3bb529ccacf7fa5fbff408f5b76b455cfe4f0a53bef162c1946df5d";
        Xunit.Assert.Equal(fingerprint, directive.QuestionFingerprint);
        Xunit.Assert.Equal(fingerprint, directive.BlockerFingerprint);
        Xunit.Assert.Same(directive, result.Directive);
        Xunit.Assert.Equal(HumanWaitKind.SpecClarification, directive.Kind);
        Xunit.Assert.Equal(0, result.EvidenceLineCount);
        Xunit.Assert.False(result.IsMalformed, result.Diagnostic);
    }

    [Xunit.Fact]
    public async Task Api_round_records_each_amendment_as_an_independent_question()
    {
        var kernel = new AgentOrchestratorKernel();
        var planner = new TaskSpec(TaskId.New(), "Plan the implementation", AgentRole.Planner);
        var goal = kernel.CreateGoal("Amend independent criteria", [planner]);
        var agents = DefaultAgents();
        kernel.ActivateGoal(goal.Id, agents);
        var provider = new AmendmentProvider(Amendment() + "\n" + Amendment("criterion_index", "2"));
        var runner = new AgentTaskRunner(kernel, agents, new InMemoryModelProviderRegistry([provider]));

        await runner.RunAsync(goal.Id, planner.Id, TestContext.Current.CancellationToken);

        var pending = kernel.GetPendingBlockingHumanInput(goal.Id);
        Xunit.Assert.Equal(2, pending.Count);
        Xunit.Assert.Equal(2, pending.Select(item => item.QuestionFingerprint).Distinct().Count());
        Xunit.Assert.All(pending, item => Xunit.Assert.Equal(HumanWaitKind.SpecClarification, item.Kind));
        Xunit.Assert.Equal(WorkTaskStatus.WaitingForHuman, planner.Status);
        Xunit.Assert.NotEqual(GoalStatus.Failed, goal.Status);
    }

    private sealed class AmendmentProvider(string output) : IModelProvider
    {
        public string ProviderName => "OpenAI";
        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ModelResponse(output, new ModelUsage(1, 1), "stop"));
    }

    [Xunit.Theory]
    [Xunit.InlineData("PLANNER_EVIDENCE_REQUEST: not-json")]
    [Xunit.InlineData("PLANNER_EVIDENCE_REQUEST: none\n" + Evidence)]
    public void Parse_amendment_does_not_bypass_invalid_evidence(string evidence)
    {
        var result = AgentOutputDirectives.ParseHumanInputRequest(Amendment() + "\n" + evidence, AgentRole.Planner);

        Xunit.Assert.True(result.IsMalformed);
        Xunit.Assert.StartsWith("Malformed PLANNER_EVIDENCE_REQUEST:", result.Diagnostic);
    }

    [Xunit.Fact]
    public void Parse_none_sentinels_wrapper_and_receipt_boundary_keep_existing_behavior()
    {
        var expected = ParseDirective(Amendment());
        var none = AgentOutputDirectives.ParseHumanInputRequest(
            "HUMAN_INPUT: none\n" + Amendment() + "\nPLANNER_EVIDENCE_REQUEST: none", AgentRole.Planner);
        var receipt = AgentOutputDirectives.ParseHumanInputRequest(
            "<!-- MCG_DURABLE_PLANNER_PLAN:BEGIN -->\n" + Amendment(), AgentRole.Planner);

        Xunit.Assert.Equal(expected, none.Directive);
        Xunit.Assert.Equal(expected.Question, AgentOutputDirectives.TryParseHumanInputRequest(Amendment()));
        Xunit.Assert.Equal(expected.Question, AgentOutputDirectives.TryParseHumanInputRequest(
            Amendment() + "\n" + Evidence + "\n" + Evidence.Replace("parser-tests", "replay-tests")));
        Xunit.Assert.Null(AgentOutputDirectives.TryParseHumanInputRequest(
            Evidence + "\n" + Evidence.Replace("parser-tests", "replay-tests")));
        Xunit.Assert.Null(receipt.Directive);
        Xunit.Assert.False(receipt.IsMalformed);
    }

    [Xunit.Fact]
    public void Fingerprint_uses_only_normalized_criterion_and_defect()
    {
        var original = ParseDirective(Amendment());
        var expected = original.BlockerFingerprint;
        Xunit.Assert.Equal(expected, original.QuestionFingerprint);
        foreach (var output in new[]
        {
            Amendment("evidence", "\"other.cs:99\""),
            Amendment("proposed_text", "\"Different proposal.\""),
            Amendment("fallback_text", null),
            Amendment("defect", "\" EXISTING   regression contradicts the CRITERION. \"")
        })
            Xunit.Assert.Equal(expected, ParseDirective(output).QuestionFingerprint);
        Xunit.Assert.Equal(expected, ParseDirective(Amendment("evidence", "\"other.cs:99\"")).BlockerFingerprint);
        Xunit.Assert.Equal(expected, ParseDirective(Amendment("proposed_text", "\"Different proposal.\"")).BlockerFingerprint);
        Xunit.Assert.Equal(expected, ParseDirective(Amendment("fallback_text", null)).BlockerFingerprint);
        Xunit.Assert.Equal(expected, ParseDirective(Amendment("defect", "\" EXISTING   regression contradicts the CRITERION. \"")).BlockerFingerprint);
        Xunit.Assert.NotEqual(expected, ParseDirective(Amendment("defect", "\"Different defect.\"")).BlockerFingerprint);
        Xunit.Assert.NotEqual(expected, ParseDirective(Amendment("criterion_index", "2")).BlockerFingerprint);
    }

    [Xunit.Fact]
    public void Planner_template_names_amendment_contract_and_ruling_lifecycle()
    {
        var template = AgentOutputDirectives.WorkerResultTemplateLinesForRole(AgentRole.Planner)[0];
        foreach (var fragment in new[] { "PLANNER_BRIEF_AMENDMENT:", "criterion_index (1-based)", "defect", "evidence (path:line)",
                     "proposed_text", "optional fallback_text", "report the amendment in blockers", "re-emit evidence lines after the ruling", "only the owner question",
                     "line per criterion that needs amending", "at most one per criterion" })
            Xunit.Assert.Contains(fragment, template);
    }

    private static HumanInputDirective ParseDirective(string output)
    {
        var result = AgentOutputDirectives.ParseHumanInputRequest(output, AgentRole.Planner);
        Xunit.Assert.False(result.IsMalformed, result.Diagnostic);
        return Xunit.Assert.Single(result.Directives);
    }

    private static Dictionary<string, object?> ValidPayload() => new()
    {
        ["criterion_index"] = 1,
        ["defect"] = "Existing regression contradicts the criterion.",
        ["evidence"] = "tests/ExistingTests.cs:12",
        ["proposed_text"] = "Preserve the existing behavior.",
        ["fallback_text"] = "Narrow the criterion."
    };

    private static string Amendment(string? field = null, string? json = null)
    {
        var payload = ValidPayload();
        if (field is not null)
        {
            if (json is null) payload.Remove(field);
            else payload[field] = JsonSerializer.Deserialize<JsonElement>(json);
        }
        return Prefix + JsonSerializer.Serialize(payload);
    }
}
