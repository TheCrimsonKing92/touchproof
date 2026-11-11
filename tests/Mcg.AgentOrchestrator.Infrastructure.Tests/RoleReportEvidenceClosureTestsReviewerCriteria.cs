using Mcg.AgentOrchestrator.Core;

// Parallel-safe: only fixture-owned in-memory goals, tasks, requests and a fixed clock.
public sealed class RoleReportEvidenceClosureTestsReviewerCriteria
{
    private const string Met = "[{\"criterion_index\":2,\"verdict\":\"met\",\"evidence\":\"Reviewed current source contract\"}]";
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 0, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("[{\"criterion_index\":2,\"verdict\":\"not-verifiable\",\"evidence\":\"Baseline=capture-only; matched comparison pending\"}]", "criterion-verdict-not-met:criterion_index=2 verdict=not-verifiable")]
    [InlineData("[{\"criterion_index\":2,\"verdict\":\"not-met\",\"evidence\":\"Missing proof\"}]", "criterion-verdict-not-met:criterion_index=2 verdict=not-met")]
    [InlineData("[{\"criterion_index\":0,\"verdict\":\"met\",\"evidence\":\"Unrelated source reviewed\"}]", "criterion-verdict-missing:criterion_index=2")]
    [InlineData("[]", "criterion-verdict-missing:criterion_index=2")]
    [InlineData(null, "criterion-verdict-invalid:")]
    [InlineData("{", "criterion-verdict-invalid:")]
    [InlineData("[null]", "criterion-verdict-invalid:")]
    [InlineData("[{\"criterion_index\":2,\"verdict\":\"unknown\",\"evidence\":\"Unknown\"}]", "criterion-verdict-invalid:")]
    [InlineData("[{\"criterion_index\":2,\"verdict\":\"met\"}]", "criterion-verdict-invalid:")]
    [InlineData("[{\"criterion_index\":2,\"verdict\":\"met\",\"evidence\":\"Source reviewed\"},{\"criterion_index\":2,\"verdict\":\"not-verifiable\",\"evidence\":\"Execution pending\"}]", "criterion-verdict-conflict:criterion_index=2 verdicts=met,not-verifiable")]
    public void OverallPass_WithoutUniqueMatchingMet_KeepsCriterionPending(
        string? verdicts, string reason)
    {
        var fixture = Create(verdicts);
        Assert.True(WorkerResultBlockers.TryFindPassVerdict(fixture.Reviewer.LastVerification));

        var skipped = fixture.Close();

        Assert.StartsWith(reason, Assert.Single(skipped).Value);
        Assert.Equal(fixture.Request.Id, Assert.Single(skipped).Key);
        Assert.False(fixture.Request.IsCompleted);
        Assert.Null(fixture.Request.Answer);
        Assert.Empty(fixture.Request.AnswerHistory);
        Assert.DoesNotContain(fixture.Goal.Timeline, item => item.Kind == ProgressKind.HumanInputReceived);
        Assert.Contains(reason, Assert.Single(fixture.Notes).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatchingMet_CurrentBriefMapping_ClosesWithCriterionEvidence(bool retainedSpec)
    {
        var fixture = Create(Met, retainedSpec: retainedSpec);

        Assert.Empty(fixture.Close());

        Assert.True(fixture.Request.IsCompleted);
        Assert.Contains("criterion=3 candidate=B landing_candidate=C", fixture.Request.Answer);
        Assert.Contains("criterion_index=2 evidence=Reviewed current source contract", fixture.Request.Answer);
        Assert.EndsWith("verdict=pass", fixture.Request.Answer);
        Assert.Contains($"task={fixture.Reviewer.Id.Value}", fixture.Request.Answer);
        Assert.Equal(HumanInputAnswerOrigin.Worker, fixture.Request.AuthoritativeAnswer!.Origin);
        Assert.Equal(fixture.Goal.AuthoritativeBrief.Version, fixture.Request.AuthoritativeAnswer.BriefVersion);
        Assert.Equal(1, fixture.Goal.AuthoritativeRefinedSpecVersion!.BriefVersion);
        Assert.Single(fixture.Goal.Timeline.Where(item => item.Kind == ProgressKind.HumanInputReceived));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatchingMet_DifferentCandidate_RequiresConductorOnlyRange(bool compatible)
    {
        var fixture = Create(Met, reviewed: "A");
        var queries = new List<(string, string)>();

        var skipped = fixture.Close((reviewed, landing) =>
        {
            queries.Add((reviewed, landing));
            return compatible;
        });

        Assert.Equal(new[] { ("A", "C") }, queries);
        Assert.Equal(compatible, fixture.Request.IsCompleted);
        if (compatible) Assert.Empty(skipped);
        else Assert.Equal("owner-candidate-differs", skipped[fixture.Request.Id]);
    }

    [Fact]
    public void ConductorOnlyRange_MissingCriterionVerdict_DoesNotCertifyIt()
    {
        var fixture = Create("[]", reviewed: "A");
        var queried = false;

        var skipped = fixture.Close((_, _) => queried = true);

        Assert.True(queried);
        Assert.Equal("criterion-verdict-missing:criterion_index=2", skipped[fixture.Request.Id]);
        Assert.False(fixture.Request.IsCompleted);
    }

    [Theory]
    [InlineData("request-brief", "brief-version-differs")]
    [InlineData("reviewer-brief", "owner-brief-version-differs")]
    [InlineData("unresolved-index", "criterion-unresolved")]
    [InlineData("identity-goal", "criterion-binding-differs")]
    [InlineData("identity-task", "criterion-binding-differs")]
    [InlineData("payload-index", "criterion-binding-differs")]
    [InlineData("payload-owner", "criterion-binding-differs")]
    [InlineData("payload-key", "criterion-binding-differs")]
    public void MatchingMet_IncompatibleBinding_KeepsWait(string mismatch, string reason)
    {
        var fixture = Create(Met, mismatch: mismatch);

        var skipped = fixture.Close();

        Assert.Equal(reason, skipped[fixture.Request.Id]);
        Assert.False(fixture.Request.IsCompleted);
        Assert.Empty(fixture.Request.AnswerHistory);
    }

    [Fact]
    public void RepeatedClosure_PendingThenMet_CompletesOnceWithoutChangingReceipt()
    {
        var fixture = Create("[]");
        Assert.Single(fixture.Close());
        Assert.Single(fixture.Close());
        Assert.Single(fixture.Notes);
        Assert.False(fixture.Request.IsCompleted);

        fixture.Reviewer.RecordVerification(fixture.Reviewer.LastVerification! with
        {
            StandardOutput = Output(Met), AuthoritativeStandardOutput = Output(Met)
        });
        fixture.Clock.UtcNow = Now.AddHours(1);
        Assert.Empty(fixture.Close());
        var answer = fixture.Request.AuthoritativeAnswer;
        Assert.NotNull(answer);
        Assert.Single(fixture.Request.AnswerHistory);
        var events = fixture.Goal.Timeline.Count;
        fixture.Clock.UtcNow = Now.AddHours(2);

        Assert.Empty(fixture.Close());

        Assert.Same(answer, fixture.Request.AuthoritativeAnswer);
        Assert.Single(fixture.Request.AnswerHistory);
        Assert.Equal(events, fixture.Goal.Timeline.Count);
    }

    [Fact]
    public void UntypedRequest_CriterionProse_PreservesLegacyEligibility()
    {
        var fixture = Create(Met, mismatch: "untyped");

        var skipped = fixture.Close();

        // Prose and a payload alone do not grant criterion-binding authority or change eligibility.
        Assert.Equal("brief-version-differs", skipped[fixture.Request.Id]);
        Assert.False(fixture.Request.IsCompleted);
    }

    [Fact]
    public void MissingIndex_MetVerdict_DoesNotDefaultToFirstCriterion()
    {
        var fixture = Create("[{\"verdict\":\"met\",\"evidence\":\"No criterion identity\"}]", criterionIndex: 1);

        var skipped = fixture.Close();

        Assert.StartsWith("criterion-verdict-invalid:", skipped[fixture.Request.Id]);
        Assert.False(fixture.Request.IsCompleted);
    }

    private static Fixture Create(string? verdicts, string reviewed = "B", bool retainedSpec = false,
        string? mismatch = null, int criterionIndex = 3)
    {
        var kernel = new AgentOrchestratorKernel();
        var planner = new TaskSpec(TaskId.New(), "Plan evidence", AgentRole.Planner);
        var developer = new TaskSpec(TaskId.New(), "Implement candidate", AgentRole.Developer);
        var reviewer = new TaskSpec(TaskId.New(), "Review candidate", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Preserve criterion evidence", [planner, developer, reviewer]);
        string[] criteria = ["Review first source requirement.", "Review second source requirement.",
            "Review the current criterion; Acceptance executes required experiments."];
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec("Review criteria", criteria,
            VerificationClass.TestVerifiable, [], []));
        if (retainedSpec)
            kernel.ReviseGoalBrief(goal.Id, "Revised wording\n\n## Acceptance criteria\n" +
                string.Join("\n", criteria.Select((item, index) => $"{index + 1}. {item}")), "Unchanged criteria");
        developer.RecordDispatch(new TaskDispatchRecord("fixture", "implement", "fixture", Now,
            ResultCommit: "B", BriefVersion: goal.AuthoritativeBrief.Version));
        developer.SetStatus(WorkTaskStatus.Completed);
        reviewer.RecordDispatch(new TaskDispatchRecord("fixture", "review", "fixture", Now.AddMinutes(1),
            BaseCommit: reviewed, BriefVersion: mismatch == "reviewer-brief" ? 0 : goal.AuthoritativeBrief.Version));
        reviewer.RecordVerification(new TaskVerificationRecord("review", "fixture", 0, Output(verdicts),
            string.Empty, Now.AddMinutes(2), WorkerResultPresent: true, ReviewedCommit: reviewed));
        reviewer.SetStatus(WorkTaskStatus.Completed);
        var identity = new PlannerEvidenceIdentity(mismatch == "identity-goal" ? "another-goal" : goal.Id.Value,
            mismatch == "identity-task" ? developer.Id.Value : planner.Id.Value,
            mismatch == "request-brief" ? 0 : goal.AuthoritativeBrief.Version,
            mismatch == "unresolved-index" ? 99 : criterionIndex, "review-proof");
        var request = new HumanInputRequest(HumanInputRequestId.New(), goal.Id, planner.Id,
            "Provide Reviewer evidence for criterion 3", Now, kind: HumanWaitKind.ProspectiveAcceptanceEvidence,
            evidenceOwner: "reviewer", plannerIdentity: mismatch == "untyped" ? null : identity,
            plannerEvidence: new PlannerEvidencePayload(mismatch == "payload-index" ? 1 : identity.CriterionIndex,
                mismatch == "payload-key" ? "different-key" : identity.EvidenceKey, "post-implementation",
                mismatch == "payload-owner" ? "acceptance" : "reviewer", "Reviewer proof", "Criterion evidence",
                null, null, null));
        return new Fixture(goal, reviewer, request);
    }

    private static string Output(string? verdicts) => string.Join("\n", new[]
    {
        "WORKER_RESULT:", "tests: pass - WorkerSpecificationComparisonTests; Baseline=capture-only",
        "blockers: none", "verdict: pass", verdicts is null ? null : $"criteria_verdicts: {verdicts}", "END_WORKER_RESULT"
    }.OfType<string>());

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = Now;
    }

    private sealed class Fixture(Goal goal, TaskSpec reviewer, HumanInputRequest request)
    {
        public Goal Goal { get; } = goal;
        public TaskSpec Reviewer { get; } = reviewer;
        public HumanInputRequest Request { get; } = request;
        public FixedClock Clock { get; } = new();
        public IEnumerable<ProgressEvent> Notes => Goal.Timeline.Where(item => item.Kind == ProgressKind.TaskNote);

        public IReadOnlyDictionary<HumanInputRequestId, string> Close(Func<string, string, bool>? range = null) =>
            new RoleReportEvidenceClosure(new object(), Clock,
                (g, task, kind, message) => g.Append(new ProgressEvent(g.Id, task, kind, message, Clock.UtcNow)))
                .Close(Goal, [Request], "C", _ => throw new Exception("Reviewer must not certify gate tests"),
                    conductorOnlyRange: range ?? ((_, _) => throw new Exception("Current candidate needs no range proof")));
    }
}
