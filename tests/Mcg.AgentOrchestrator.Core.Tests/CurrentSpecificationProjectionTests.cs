using System.Text;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;

public sealed class CurrentSpecificationProjectionTests
{
    [Xunit.Fact]
    public void RepeatedBodiesRoundTripEveryRecordAndKeepDistinctRulings()
    {
        var body = string.Concat(Enumerable.Repeat("Preserve exact owner ruling ✓ and unique requirement. ", 150));
        var spec = new RefinedSpec("Contract", ["Exact criterion\nwith another line"], VerificationClass.TestVerifiable,
            [new("same topic", body, "decision provenance"), new("same topic", body + " DIFFERENT", "distinct ruling")],
            [new("q1", "same topic", "contract", "Answered", body, "scope", "normalized", "criterion", "blast"),
             new("q2", "still unresolved", "contract", "Open")])
        {
            ClarificationAnswerHistory = [new("answer-1", body, DateTimeOffset.UnixEpoch, BriefVersion: 2)]
        };
        var source = CurrentSpecificationProjection.SourceBytes(spec);
        var projected = CurrentSpecificationProjection.Project(spec);
        Assert.True(projected.Length < source.Length);
        var view = JsonNode.Parse(projected)!.AsObject();
        Assert.Single(view["SharedBodies"]!.AsObject());
        var bodies = view["SharedBodies"]!.DeepClone().AsObject();
        foreach (var (collection, property) in new[] { ("Decisions", "Choice"), ("OpenQuestions", "Answer"), ("ClarificationAnswerHistory", "Text") })
            foreach (var record in view[collection]!.AsArray().OfType<JsonObject>())
                if (record[property] is JsonObject reference)
                    record[property] = bodies[reference["$body"]!.GetValue<string>()]!.DeepClone();
        view.Remove("SharedBodies");
        view.Remove("BodyReferenceContract");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(source), view), "Every source field must survive expansion, including differing same-topic answers.");
        Assert.Equal(projected, CurrentSpecificationProjection.Project(spec));
    }

    [Xunit.Fact]
    public void OnlyExactCriteriaUnderRecognizedHeadingAreReplaced()
    {
        var criterion = new string('x', 240);
        var spec = new RefinedSpec("contract", [criterion], VerificationClass.TestVerifiable, [], []);
        var objective = $"Unique brief addition\r\n1. {criterion}\r\n## Acceptance criteria\r\n1. {criterion}\r\n2. unique addition\r\n## Operations\r\n1. {criterion}\r\n";
        var expected = $"Unique brief addition\r\n1. {criterion}\r\n## Acceptance criteria\r\n- See current specification, acceptance criterion 1.\r\n2. unique addition\r\n## Operations\r\n1. {criterion}\r\n";
        Assert.Equal(expected, CurrentSpecificationProjection.ProjectObjective(objective, spec));
        Assert.Equal(objective, CurrentSpecificationProjection.ProjectObjective(objective, null));
        Assert.Equal(objective, CurrentSpecificationProjection.ProjectObjective(objective, spec with { AcceptanceCriteria = [criterion + " revised"] }));
    }

    [Xunit.Fact]
    public void CompactUniqueContentIsByteIdentical()
    {
        var spec = new RefinedSpec("compact ✓", ["criterion"], VerificationClass.TestVerifiable,
            [new("question", "unique answer", "reason")], []);
        Assert.Equal(CurrentSpecificationProjection.SourceBytes(spec), CurrentSpecificationProjection.Project(spec));
        Assert.Equal("compact brief", CurrentSpecificationProjection.ProjectObjective("compact brief", spec));
    }

    [Xunit.Fact]
    public void InlineOnlyKernelKeepsLargeUniqueBriefAndCompleteQuestionMetadata()
    {
        var addition = "unique-middle-" + new string('z', 5000) + "-unique-tail";
        var answer = "answer-middle-" + new string('a', 6000) + "-answer-tail";
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
        var goal = kernel.CreateGoal(addition, [task]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec("contract", ["exact revised criterion"], VerificationClass.TestVerifiable,
            [new("question", answer, "reason")], [new("q1", "question", "contract", "Answered", answer, "unique-scope")])
        {
            ClarificationAnswerHistory = [new("owner-answer", answer, DateTimeOffset.UnixEpoch, BriefVersion: 1)]
        });
        var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;
        Assert.Contains(addition, brief, StringComparison.Ordinal);
        Assert.Contains(answer, brief, StringComparison.Ordinal);
        Assert.Contains("unique-scope", brief, StringComparison.Ordinal);
        Assert.Contains("owner-answer", brief, StringComparison.Ordinal);
        Assert.Contains("exact revised criterion", brief, StringComparison.Ordinal);
        Assert.Equal(1, brief.Split(answer, StringSplitOptions.None).Length - 1);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void InlineOnlyPlannerEvidenceIsCompleteOrFailsExplicitly(bool available)
    {
        var plan = "Complete durable Planner plan\n" + new string('p', 6000) + "\nunique-plan-tail";
        var planner = new TaskSpec(TaskId.New(), "Plan", AgentRole.Planner);
        planner.RecordVerification(new TaskVerificationRecord("planner", "unused", 0, "preview", "", DateTimeOffset.UnixEpoch,
            FullStandardOutput: available ? plan : null, FullStandardOutputUnavailableReason: available ? null : "capture-lost",
            StandardOutputIsAuthoritative: false));
        planner.SetStatus(WorkTaskStatus.Completed);
        var intervening = Enumerable.Range(0, 3).Select(index =>
        {
            var completed = new TaskSpec(TaskId.New(), $"Intermediate {index}", AgentRole.Developer);
            completed.RecordVerification(new TaskVerificationRecord("worker", "unused", 0, "completed intermediate", "", DateTimeOffset.UnixEpoch));
            completed.SetStatus(WorkTaskStatus.Completed);
            return completed;
        }).ToArray();
        var developer = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Preserve complete Planner evidence", [planner, .. intervening, developer]);
        if (available)
            Assert.Contains(plan, kernel.BuildTaskBrief(goal.Id, developer.Id).Content, StringComparison.Ordinal);
        else
            Assert.Contains("Complete inline Planner evidence unavailable", Assert.Throws<InvalidOperationException>(() =>
                kernel.BuildTaskBrief(goal.Id, developer.Id)).Message, StringComparison.Ordinal);
    }
}
