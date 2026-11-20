using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: captured worker text and in-memory kernels; no dispatch processes or stores.
public sealed class ReviewerUnactionableNeedsWorkContractTests
{
    private const string ValidRequest =
        ""","evidence_request":{"selections":[{"test_project":"Core","test_class":"ExampleTests"}]}""";
    private const string EmptyRequest = ""","evidence_request":{"selections":[]}""";

    [Theory]
    [InlineData(false, 1, false)]
    [InlineData(true, 1, false)]
    [InlineData(false, 2, false)]
    [InlineData(false, 1, true)]
    public void RequestlessBlockingEvidenceReceivesSpecificRepair(
        bool explicitNull, int blockingCount, bool includeIrrelevantFindings)
    {
        var findings = Enumerable.Range(0, blockingCount)
            .Select(index => Finding("missing-" + index,
                request: explicitNull ? ""","evidence_request":null""" : "")).ToList();
        if (includeIrrelevantFindings)
        {
            findings.Add(Finding("closed-source", state: "resolved", category: "correctness"));
            findings.Add(Finding("advisory-source", severity: "advisory", category: "correctness"));
        }
        var output = Contract("needs-work", findings);
        var parsed = Parse(output);
        Assert.Equal(blockingCount, parsed.Findings.Count(finding =>
            finding.State == ReviewFindingState.Open && finding.Severity == FindingSeverity.Blocking));
        Assert.All(parsed.Findings.Where(finding => finding.Category == FindingCategory.TestEvidence),
            finding => Assert.Null(finding.EvidenceRequest));

        var assessment = WorkerResultBlockers.AssessTerminalVerificationOutput(AgentRole.Reviewer, output);

        Assert.Equal(VerificationTerminalContractStatus.Malformed, assessment.Status);
        Assert.Equal(ReviewerBlockingFindingActorRule.RepairMessage, assessment.Diagnostic);
        Assert.Contains(ReviewerBlockingFindingActorRule.RuleText, assessment.Diagnostic);
        Assert.Contains("every open blocking finding is a test-evidence finding without evidence_request",
            assessment.Diagnostic);
    }

    public static IEnumerable<object[]> AcceptedReviewRounds()
    {
        yield return ["source-defect", "needs-work", new[] { Finding("source", category: "correctness") }];
        yield return ["requested-evidence", "needs-work", new[] { Finding("requested", request: ValidRequest) }];
        yield return ["gate-owned-pass", "pass", Array.Empty<string>()];
        yield return ["mixed-source", "needs-work",
            new[] { Finding("missing"), Finding("source", category: "correctness") }];
        yield return ["mixed-request", "needs-work",
            new[] { Finding("missing"), Finding("requested", request: ValidRequest) }];
        yield return ["empty", "needs-work", Array.Empty<string>()];
        yield return ["supplied-empty-request", "needs-work", new[] { Finding("empty", request: EmptyRequest) }];
        yield return ["fail", "fail", new[] { Finding("missing") }];
        yield return ["blocked-at-cap", "blocked-at-cap", new[] { Finding("missing") }];
        yield return ["resolved-only", "needs-work", new[] { Finding("closed", state: "resolved") }];
        yield return ["advisory-only", "needs-work", new[] { Finding("advice", severity: "advisory") }];
    }

    [Theory]
    [MemberData(nameof(AcceptedReviewRounds))]
    public void ActionableAndOutOfScopeRoundsKeepTheirTerminalAcceptance(
        string scenario, string verdict, string[] findings)
    {
        var output = Contract(verdict, findings);
        var parsed = Parse(output);
        Assert.Equal(findings.Length, parsed.Findings.Count);
        if (scenario is "requested-evidence" or "supplied-empty-request")
            Assert.NotNull(Assert.Single(parsed.Findings).EvidenceRequest);
        if (scenario == "gate-owned-pass")
        {
            var record = Record(output);
            Assert.True(WorkerResultBlockers.TryFindCriteriaVerdicts(record, out var criteria, out _));
            Assert.Equal("not-verifiable", Assert.Single(criteria).Verdict);
        }

        var assessment = WorkerResultBlockers.AssessTerminalVerificationOutput(AgentRole.Reviewer, output);

        Assert.Equal(VerificationTerminalContractStatus.Valid, assessment.Status);
        Assert.Empty(assessment.Diagnostic);
    }

    [Fact]
    public void TesterRequestlessEvidenceKeepsExistingTerminalHandling()
    {
        var output = Contract("needs-work", [Finding("missing")]);

        var assessment = WorkerResultBlockers.AssessTerminalVerificationOutput(AgentRole.Tester, output);

        Assert.Equal(VerificationTerminalContractStatus.Valid, assessment.Status);
        Assert.Empty(assessment.Diagnostic);
    }

    [Fact]
    public void MalformedSuppliedRequestKeepsExistingParsingDiagnostic()
    {
        var output = Contract("needs-work", [Finding("malformed", request: ""","evidence_request":42""")]);
        Assert.False(WorkerResultBlockers.TryFindReviewFindingRound(Record(output), out _, out var reason));

        var assessment = WorkerResultBlockers.AssessTerminalVerificationOutput(AgentRole.Reviewer, output);

        Assert.Equal(VerificationTerminalContractStatus.Malformed, assessment.Status);
        Assert.Equal("Reviewer field findings is malformed: " + reason, assessment.Diagnostic);
    }

    [Fact]
    public void RejectionUsesExistingRepairRetryAndCarriesRuleIntoFeedback()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Require actionable review",
            [new TaskSpec(TaskId.New(), "Review candidate", AgentRole.Reviewer)]);
        kernel.ActivateGoal(goal.Id, [new AgentDefinition(AgentId.New(), "Reviewer", AgentRole.Reviewer,
            new ModelProfile("Test", "fixture", ModelCapability.Text, SubscriptionMode.ApiKey))]);
        var task = Assert.Single(goal.Tasks);
        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("grok-cli", "grok review", @"C:\repo", clock.UtcNow,
                WorkerProviderKind: ProviderKind.XaiGrokCli));
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "Fixture attempt started.");
        var record = new TaskVerificationRecord(task.LastDispatch!.Command, task.LastDispatch.WorkingDirectory, 0,
            Contract("needs-work", [Finding("missing")]), "", clock.UtcNow.AddSeconds(1),
            WorkerResultPresent: true, DispatchStartedAt: clock.UtcNow,
            EvidenceSource: VerificationEvidenceSource.Dispatch, DispatchAttemptAt: clock.UtcNow);
        var outcome = DispatchFailureClassifier.Classify(task, record);
        Assert.Equal(DispatchOutcomeKind.VerificationIncomplete, outcome.Kind);
        Assert.Equal("terminal_contract=Malformed; " + ReviewerBlockingFindingActorRule.RepairMessage,
            outcome.EvidenceSummary);

        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, record);

        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        var retry = Assert.Single(goal.Timeline.Where(evt => evt.Kind == ProgressKind.TaskRetried));
        Assert.Equal("Reviewer terminal contract repair 1/2; same-role retry: terminal_contract=Malformed; " +
            ReviewerBlockingFindingActorRule.RepairMessage, retry.Message);
        Assert.Null(task.LastVerification);
        Assert.Null(task.PendingReviewFindingRepairCheckpoint);
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.Message.Contains("ERR_REVIEWER_TEST_EVIDENCE_REQUEST_MISSING", StringComparison.Ordinal));
        Assert.Contains(ReviewerBlockingFindingActorRule.RuleText,
            FeedbackBlock(kernel.BuildTaskBrief(goal.Id, task.Id).Content));
    }

    private static ReviewFindingRound Parse(string output)
    {
        Assert.True(WorkerResultBlockers.TryFindReviewFindingRound(Record(output), out var round, out var diagnostic),
            diagnostic);
        return round;
    }

    private static TaskVerificationRecord Record(string output) =>
        new("contract", "", 0, output, "", DateTimeOffset.MinValue);

    private static string Finding(string id, string state = "open", string category = "test-evidence",
        string severity = "blocking", string request = "") =>
        $$"""{"stable_id":"{{id}}","state":"{{state}}","severity":"{{severity}}","category":"{{category}}","location":{"file":"src/Example.cs","region":"{{id}}"},"description":"Inspect the candidate evidence."{{request}}}""";

    private static string Contract(string verdict, IEnumerable<string> findings) =>
        "WORKER_RESULT:\nfiles: none\ncommands: inspected candidate\ntests: deferred - ExampleTests\n" +
        "commit: none\nblockers: " + (verdict == "pass" ? "none" : "review evidence unavailable") +
        "\nmodel_fit: fixture\nskills: none\nconfidence: high\n" +
        "findings: [" + string.Join(",", findings) + "]\ntouched_anchors: []\n" +
        "criteria_verdicts: [{\"criterion_index\":0,\"verdict\":\"not-verifiable\"," +
        "\"evidence\":\"ACCEPTANCE-GATE-OWNED execution is deferred to the gate.\"}]\nverdict: " +
        verdict + "\nEND_WORKER_RESULT";

    private static string FeedbackBlock(string brief)
    {
        const string startMarker = "<!-- ACCUMULATED_RETRY_FEEDBACK_START -->";
        const string endMarker = "<!-- ACCUMULATED_RETRY_FEEDBACK_END -->";
        var start = brief.IndexOf(startMarker, StringComparison.Ordinal);
        var end = brief.IndexOf(endMarker, start >= 0 ? start : 0, StringComparison.Ordinal);
        Assert.True(start >= 0, "Accumulated retry feedback start marker was absent.");
        Assert.True(end > start, "Accumulated retry feedback end marker was absent.");
        return brief[start..(end + endMarker.Length)];
    }
}
