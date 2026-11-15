using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static TestClassRenameEvidenceTests;
using static Mcg.AgentOrchestrator.App.Orchestration.GateTestObligationEvaluator;

// Parallel-safe: each test owns its repositories, attempt metadata and result files.
public sealed class GateTestObligationPrefixedNameFollowTests
{
    private const string Planned = "PlannedTests";
    private const string Committed = "FamilyPlannedTests";
    private const string AddedPath = "tests/Fixture/FamilyPlannedTests.cs";

    [Theory]
    [InlineData("PlannedTests")]
    [InlineData("PlannedTests.Check")]
    [InlineData("Fixture.PlannedTests.Check")]
    public void UniquePassingClass_BindsClassAndMethodSelectors(string selector)
    {
        using var repo = AddedRepository();
        using var fixture = new ProspectiveEvidenceGateFixture();
        var goal = Seed(fixture, [selector]);
        var candidate = Git(repo.Root, "rev-parse", "HEAD");
        var rowClass = selector.StartsWith("Fixture.", StringComparison.Ordinal) ? "Fixture." + Committed : Committed;
        var (id, _, _) = fixture.WriteAttempt([($"{rowClass}.Check", "Passed"), ($"{rowClass}.Second", "Passed")], candidateSha: candidate);
        Assert.Equal(Outcome.SelectorNotFound, Assert.Single(Evaluate(goal, fixture.OrchestratorDirectory, candidate)).Outcome);

        var result = Assert.Single(Evaluate(goal, fixture.OrchestratorDirectory, candidate, new(repo.Root, "main")));

        Assert.Equal(Outcome.Satisfied, result.Outcome);
        Assert.Equal(id, result.SourceId);
        Assert.Equal(new[] { selector }, result.Selectors);
        var binding = Assert.Single(result.RenameFollows!);
        Assert.Equal(Planned, binding.OldClass);
        Assert.Equal(Committed, binding.NewClass);
        Assert.Equal("none", binding.OldPath);
        Assert.Equal(AddedPath, binding.NewPath);
        Assert.Equal(0, binding.Similarity);
        Assert.Equal($"prefixed-name planned={Planned} committed={Committed} candidate={candidate}", binding.Evidence);
        Assert.Equal($"GATE_TEST_OBLIGATION_RENAME_FOLLOWED id={Assert.Single(goal.GateTestObligations).Id[..8]} " +
            $"old={Planned} new={Committed} old_path=none new_path={AddedPath} similarity=0 " +
            $"evidence=prefixed-name planned={Planned} committed={Committed} candidate={candidate} source={id}", result.RenameProvenance);
    }

    [Theory]
    [InlineData("two-matches")]
    [InlineData("no-match")]
    [InlineData("failed")]
    [InlineData("skipped")]
    [InlineData("partial-selector-list")]
    [InlineData("two-segments")]
    [InlineData("case-mismatch")]
    [InlineData("empty-segment")]
    [InlineData("theory-row")]
    [InlineData("planned-failed")]
    [InlineData("planned-skipped")]
    [InlineData("planned-ambiguous")]
    public void UnprovenBinding_PreservesBaselineGrading(string scenario)
    {
        using var repo = AddedRepository(scenario == "no-match" ? ["OtherTests"] :
            scenario == "two-matches" ? [Committed, "OtherPlannedTests"] : [Committed]);
        using var fixture = new ProspectiveEvidenceGateFixture();
        string[] selectors = scenario switch
        {
            "partial-selector-list" => [Planned, "MissingTests"],
            "two-segments" => ["PlannedTests.PlannedTests.Check"],
            "case-mismatch" => ["plannedTests"],
            "empty-segment" => [""],
            "theory-row" => ["PlannedTests.Check(value: 1)"],
            _ => [Planned]
        };
        var goal = Seed(fixture, selectors);
        var candidate = Git(repo.Root, "rev-parse", "HEAD");
        var outcome = scenario is "failed" or "planned-failed" ? "Failed" :
            scenario is "skipped" or "planned-skipped" ? "NotExecuted" : "Passed";
        var rowClass = scenario.StartsWith("planned-", StringComparison.Ordinal) ? Planned : Committed;
        fixture.WriteAttempt(scenario == "planned-ambiguous"
                ? [("A.PlannedTests.Check", "Passed"), ("B.PlannedTests.Check", "Passed"), ($"{Committed}.Check", "Passed")]
                : [($"{rowClass}.Check", outcome)], candidateSha: candidate);
        var baseline = Assert.Single(Evaluate(goal, fixture.OrchestratorDirectory, candidate));

        var result = Assert.Single(Evaluate(goal, fixture.OrchestratorDirectory, candidate, new(repo.Root, "main")));

        Assert.NotEqual(Outcome.Satisfied, baseline.Outcome);
        AssertSameGrading(baseline, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlannedClassExists_CannotBindUnrunClass(bool onMain)
    {
        using var repo = new Repository(oldOnMain: true);
        if (onMain) Git(repo.Root, "checkout", "main");
        Write(repo.Root, "planned.cs", Source(Planned));
        Commit(repo.Root, "Declare planned class", "planned.cs");
        if (onMain)
        {
            Git(repo.Root, "checkout", "goal");
            Git(repo.Root, "merge", "main", "--no-edit");
        }
        Write(repo.Root, AddedPath, Source(Committed));
        Commit(repo.Root, "Declare prefixed class", AddedPath);
        var candidate = Git(repo.Root, "rev-parse", "HEAD");
        using var fixture = new ProspectiveEvidenceGateFixture();
        var goal = Seed(fixture, [Planned]);
        fixture.WriteAttempt([($"{Committed}.Check", "Passed")], candidateSha: candidate);
        var baseline = Assert.Single(Evaluate(goal, fixture.OrchestratorDirectory, candidate));

        var result = Assert.Single(Evaluate(goal, fixture.OrchestratorDirectory, candidate, new(repo.Root, "main")));

        Assert.Equal(Outcome.SelectorNotFound, baseline.Outcome);
        AssertSameGrading(baseline, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MultipleSelectors_RequireOneCompleteSource(bool completeOlderSource)
    {
        using var repo = AddedRepository();
        using var fixture = new ProspectiveEvidenceGateFixture();
        var goal = Seed(fixture, [Planned, "ExistingTests"]);
        var candidate = Git(repo.Root, "rev-parse", "HEAD");
        var (older, _, _) = fixture.WriteAttempt(completeOlderSource
                ? [($"{Committed}.Check", "Passed"), ("ExistingTests.Check", "Passed")]
                : [($"{Committed}.Check", "Passed")],
            candidateSha: candidate, completedAt: DateTimeOffset.UnixEpoch.AddMinutes(1));
        fixture.WriteAttempt([("ExistingTests.Check", "Passed")], candidateSha: candidate,
            completedAt: DateTimeOffset.UnixEpoch.AddMinutes(2));
        var baseline = Assert.Single(Evaluate(goal, fixture.OrchestratorDirectory, candidate));
        Assert.Equal(Outcome.SelectorNotFound, baseline.Outcome);

        var result = Assert.Single(Evaluate(goal, fixture.OrchestratorDirectory, candidate, new(repo.Root, "main")));

        if (completeOlderSource)
        {
            Assert.Equal(Outcome.Satisfied, result.Outcome);
            Assert.Equal(older, result.SourceId);
            Assert.Single(result.RenameFollows!);
        }
        else AssertSameGrading(baseline, result);
    }

    [Fact]
    public void PartialDeclarationsAndRepeatedUse_RecordOneBinding()
    {
        using var repo = new Repository(oldOnMain: true);
        Write(repo.Root, "z.cs", $"partial class {Committed} {{ }}");
        Write(repo.Root, "a.cs", $"partial class {Committed} {{ }}");
        Commit(repo.Root, "Add partials", "a.cs", "z.cs");
        using var fixture = new ProspectiveEvidenceGateFixture();
        var goal = Seed(fixture, [Planned + ".Check", Planned + ".Second"]);
        var candidate = Git(repo.Root, "rev-parse", "HEAD");
        fixture.WriteAttempt([($"{Committed}.Check", "Passed"), ($"{Committed}.Second", "Passed")], candidateSha: candidate);
        Assert.Equal(Outcome.SelectorNotFound, Assert.Single(Evaluate(goal, fixture.OrchestratorDirectory, candidate)).Outcome);

        var result = Assert.Single(Evaluate(goal, fixture.OrchestratorDirectory, candidate, new(repo.Root, "main")));

        Assert.Equal(Outcome.Satisfied, result.Outcome);
        Assert.Equal("a.cs", Assert.Single(result.RenameFollows!).NewPath);
    }

    [Fact]
    public void RenameAlreadySatisfied_KeepsRenameCommitEvidence()
    {
        using var repo = new Repository();
        Rename(repo.Root, OldPath, NewPath, "FamilyOldNameTests");
        var candidate = Git(repo.Root, "rev-parse", "HEAD");
        using var fixture = new ProspectiveEvidenceGateFixture();
        var goal = Seed(fixture, ["OldNameTests"]);
        fixture.WriteAttempt([("FamilyOldNameTests.Check", "Passed")], candidateSha: candidate);
        Assert.Equal(Outcome.SelectorNotFound, Assert.Single(Evaluate(goal, fixture.OrchestratorDirectory, candidate)).Outcome);

        var result = Assert.Single(Evaluate(goal, fixture.OrchestratorDirectory, candidate, new(repo.Root, "main")));

        Assert.Equal(Outcome.Satisfied, result.Outcome);
        Assert.Equal("commit " + candidate, Assert.Single(result.RenameFollows!).Evidence);
    }

    [Fact]
    public void SatisfiedInput_DoesNotReadCandidateEvidence()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var goal = Seed(fixture, [Planned]);
        fixture.WriteAttempt([($"{Planned}.Check", "Passed")]);
        var original = Assert.Single(Evaluate(goal, fixture.OrchestratorDirectory, fixture.CandidateSha));
        Assert.Equal(Outcome.Satisfied, original.Outcome);

        var result = GateTestObligationPrefixedNameFollow.Follow(original, [], fixture.OrchestratorDirectory, fixture.CandidateSha,
            () => throw new InvalidOperationException("Added classes must not be read"),
            _ => throw new InvalidOperationException("Absence must not be checked"));

        Assert.Same(original, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(2)]
    public void PlannerIdentity_RequiresMatchingSourceBriefVersion(int? sourceVersion)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var goal = Seed(fixture, [Planned]);
        var (_, trx, _) = fixture.WriteAttempt([($"{Committed}.Check", "Passed")]);
        var obligation = Assert.Single(goal.GateTestObligations) with
        {
            PlannerIdentity = new(goal.Id.Value, "planner", 2, 0, "binding")
        };
        var original = new Result(obligation, Outcome.SelectorNotFound, [Planned], "baseline");
        Source[] sources = [new("selected", DateTimeOffset.UnixEpoch, [trx], sourceVersion)];

        var result = GateTestObligationPrefixedNameFollow.Follow(original, sources, fixture.OrchestratorDirectory,
            fixture.CandidateSha, () => [new(Committed, AddedPath)], _ => true);

        if (sourceVersion == 2)
        {
            Assert.Equal(Outcome.Satisfied, result.Outcome);
            Assert.Equal("selected", result.SourceId);
        }
        else Assert.Same(original, result);
    }

    [Theory]
    [InlineData("passed")]
    [InlineData("failed")]
    [InlineData("pending")]
    public void CommandSelector_PreservesCheckOutcomeSemantics(string checkOutcome)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var goal = Seed(fixture, [Planned, "BuildCheck"]);
        // A misleading TRX row must not override a failed or pending command check.
        var (_, trx, _) = fixture.WriteAttempt(checkOutcome == "passed"
            ? [($"{Committed}.Check", "Passed")]
            : [($"{Committed}.Check", "Passed"), ("BuildCheck", "Passed")]);
        var original = new Result(Assert.Single(goal.GateTestObligations), Outcome.SelectorNotFound, [Planned], "baseline");
        var check = new GoalAcceptanceVerifier.AcceptanceManifestCheck { Name = "BuildCheck", Type = "command", Command = "build" };
        var explicitChecks = new Dictionary<string, bool>();
        if (checkOutcome != "pending") explicitChecks[check.Name] = checkOutcome == "passed";
        Source[] sources = [new("source", DateTimeOffset.UnixEpoch, [trx], null,
            Checks: new([check], explicitChecks, false))];

        var result = GateTestObligationPrefixedNameFollow.Follow(original, sources, fixture.OrchestratorDirectory,
            fixture.CandidateSha, () => [new(Committed, AddedPath)], _ => true);

        if (checkOutcome == "passed") Assert.Equal(Outcome.Satisfied, result.Outcome);
        else Assert.Same(original, result);
    }

    [Fact]
    public void DeclaredRows_RegradeThroughDeclaredSelectorResolver()
    {
        using var fixture = new DeclaredTestReplayFixture();
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(fixture.Path))!;
        document["results"]!["tests"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
        {
            ["testId"] = Committed + "::Check", ["name"] = "Check",
            ["suite"] = new System.Text.Json.Nodes.JsonArray(Committed), ["status"] = "passed"
        });
        File.WriteAllText(fixture.Path, document.ToJsonString());
        var original = fixture.Evaluate(Planned + ".Check");
        Assert.Equal(Outcome.SelectorNotFound, original.Outcome);
        Assert.False(Assert.Single(GroupedGateReceiptTrxEvidence.Evaluate([Committed + ".Check"], [fixture.Path], fixture.Gate.Root)).Satisfied);
        var attempt = Assert.Single(ProspectiveEvidenceGateResolver.ReadAttempts(fixture.Gate.OrchestratorDirectory,
            fixture.Gate.Goal.Id, fixture.Gate.CandidateSha));
        Source[] sources = [new(attempt.Id, attempt.CompletedAt, attempt.Paths, attempt.BriefVersion,
            Checks: attempt.Checks, WorktreeRoot: attempt.WorktreeRoot)];

        var result = GateTestObligationPrefixedNameFollow.Follow(original, sources, fixture.Gate.OrchestratorDirectory,
            fixture.Gate.CandidateSha, () => [new(Committed, AddedPath)], _ => true);

        Assert.Equal(Outcome.Satisfied, result.Outcome);
        Assert.Equal(attempt.Id, result.SourceId);
        Assert.Single(result.RenameFollows!);
    }

    private static Goal Seed(ProspectiveEvidenceGateFixture fixture, IReadOnlyList<string> selectors) =>
        GateTestObligationIntakeTests.Seed(fixture.Kernel, fixture.Goal, selectors).GetGoal(fixture.Goal.Id);

    private static Repository AddedRepository(params string[] names)
    {
        var repo = new Repository(oldOnMain: true);
        Write(repo.Root, AddedPath, string.Join('\n', (names.Length == 0 ? [Committed] : names).Select(Source)));
        Commit(repo.Root, "Add prefixed tests", AddedPath);
        return repo;
    }

    private static void AssertSameGrading(Result baseline, Result actual)
    {
        Assert.Equal(baseline.Obligation, actual.Obligation);
        Assert.Equal(baseline.Outcome, actual.Outcome);
        Assert.Equal(baseline.Selectors, actual.Selectors);
        Assert.Equal(baseline.SourceId, actual.SourceId);
        Assert.Empty(actual.RenameFollows ?? []);
        Assert.Equal(baseline.RenameProvenance, actual.RenameProvenance);
    }
}
