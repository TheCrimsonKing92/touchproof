using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each test owns its database and uses local logical work keys.
public sealed class SoloRepairReplayTests
{
    [Fact]
    public void Grouping_UsesExactBaselineScopeAndDataCase()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        var path = Path.Combine(fixture.Root, SoloBaselineRepairStore.FileName);
        var store = new SoloBaselineRepairStore(path);
        var first = Finding();
        var same = first with { Candidate = "other-candidate", Attempt = "other-attempt", Baseline = first.Baseline.ToUpperInvariant() };
        var revised = first with { Baseline = new string('9', 40), Main = new string('9', 40) };
        var otherScope = first with { Check = "other acceptance check" };
        var sibling = first with { Identity = SoloAttributedFailureRouteFixture.First + "(value: 2)" };
        Assert.Equal(first.GroupKey, same.GroupKey);
        Assert.Equal(4, new[] { first, same, revised, otherScope, sibling }.Select(row => row.GroupKey).Distinct().Count());

        store.Retain("goal-1", [first, revised, otherScope, sibling]);
        store.Retain("goal-2", [same]);
        foreach (var row in new[] { first, same, revised, otherScope, sibling })
            store.ReserveGroup(row.GroupKey, "canonical intake");

        store = new SoloBaselineRepairStore(path);
        Assert.Equal(4, Count(path, "solo_repair_group"));
        Assert.Equal(5, Count(path, "solo_repair_dependency"));
        Assert.Equal(first.GroupKey, Assert.Single(store.ReadFindings("goal-2")).GroupKey);
    }

    [Fact]
    public async Task AttributionClaim_DeduplicatesConcurrentAndRestartedRequests()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        var path = Path.Combine(fixture.Root, SoloBaselineRepairStore.FileName);
        var store = new SoloBaselineRepairStore(path);
        var unresolved = Finding() with { Inherited = false, Reason = "missing exact baseline result" };
        store.Retain("dependent", [unresolved]);
        var key = SoloBaselineRepairStore.AttributionKey("dependent", unresolved, unresolved.Candidate, unresolved.Main);
        var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => store.TryClaimCheck(key))));
        Assert.Single(claims.Where(claimed => claimed));

        var reopened = new SoloBaselineRepairStore(path);
        Assert.Null(reopened.ReadCheck(key));
        Assert.False(reopened.TryClaimCheck(key));
        reopened.Retain("dependent", [unresolved with { Attempt = "replayed-attempt", Proof = new("other", "digest") }]);
        var retained = Assert.Single(reopened.ReadFindings("dependent"));
        Assert.Equal(unresolved.Proof, retained.Proof);
        Assert.Equal(unresolved.Request, retained.Request);
        Assert.Equal(unresolved.Reason, retained.Reason);
        Assert.Equal(unresolved.CandidateArtifacts.ToArray(), retained.CandidateArtifacts.ToArray());
        reopened.CompleteCheck(key, new("unresolved", "no exact executed row", []));

        store = new SoloBaselineRepairStore(path);
        Assert.False(store.TryClaimCheck(key));
        Assert.Equal("no exact executed row", store.ReadCheck(key)?.Detail);
        Assert.Equal(1, Count(path, "solo_repair_check"));
        Assert.Null(store.ReadCheck(SoloBaselineRepairStore.AttributionKey("dependent", retained, "new-candidate", retained.Main)));
    }

    [Fact]
    public async Task MissingOwnership_EscalatesOnceAcrossCoordinatorRestart()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce(baselineFails: true);
        fixture.Record(fixture.Summary);
        var workspace = SoloRepairWorkflowTests.Workspace(fixture);
        var escalationMessages = new List<string>();
        for (var tick = 0; tick < 2; tick++)
        {
            var coordinator = Coordinator(fixture, workspace, fixture.Kernel, escalationMessages);
            var result = Advance(coordinator, fixture.Goal);
            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result?.Outcome);
            Assert.Equal(SoloHoldReasons.RepairEvidenceRequired, held.Decision?.DiscriminatingEvidence);
        }

        Assert.Contains("Designate one baseline-maintenance epic", Assert.Single(escalationMessages));
        Assert.Equal(1, Count(Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName), "solo_repair_check"));
    }

    [Theory]
    [InlineData("INSERT ON solo_repair_dependency", 0, 0, false)]
    [InlineData("INSERT ON solo_repair_group", 1, 0, false)]
    [InlineData("UPDATE OF repair_goal ON solo_repair_group", 1, 1, true)]
    public async Task InterruptedWrite_ReconcilesRepairAndLinkAfterRestart(
        string boundary, int retainedLinks, int reservedGroups, bool intakeCommitted)
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce(baselineFails: true, singleRow: true);
        fixture.Record(fixture.Summary);
        var finding = Assert.Single(SoloRepairEvidenceTests.Read(fixture).Findings);
        Assert.True(finding.Inherited, finding.Reason);
        var workspace = SoloRepairWorkflowTests.Workspace(fixture);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(fixture.Kernel);
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var maintenance = await portfolio.AddEpicAsync("baseline-maintenance");
        var escalations = new List<string>();
        var coordinator = Coordinator(fixture, workspace, fixture.Kernel, escalations);
        var storePath = Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName);
        var store = new SoloBaselineRepairStore(storePath);
        ExecuteSql(storePath, $"""
            CREATE TRIGGER stop_repair_write BEFORE {boundary}
            BEGIN SELECT RAISE(ABORT, 'controlled repair interruption'); END
            """);

        var error = Assert.Throws<SqliteException>(() => Advance(coordinator, fixture.Goal));
        Assert.Contains("controlled repair interruption", error.Message);
        Assert.Equal(retainedLinks, Count(storePath, "solo_repair_dependency"));
        Assert.Equal(reservedGroups, Count(storePath, "solo_repair_group"));
        var intake = new GoalIntakeRequestStore(workspace.SqliteStatePath);
        var request = "solo-baseline-repair-" + finding.GroupKey;
        var existing = intake.Get(request);
        if (intakeCommitted)
        {
            Assert.Equal(GoalIntakeRequestStates.Created, existing?.State);
            Assert.Equal(maintenance.Id, (await portfolio.GetGoalMembershipAsync(existing!.GoalId!))?.EpicId);
            Assert.Null(store.ReadGroup(finding.GroupKey)?.RepairGoal);
        }
        else
        {
            Assert.Null(existing);
        }

        ExecuteSql(storePath, "DROP TRIGGER stop_repair_write");
        var restarted = await repository.LoadAsync();
        var dependent = restarted.GetGoal(fixture.Goal.Id);
        coordinator = Coordinator(fixture, workspace, restarted, escalations);
        for (var tick = 0; tick < 2; tick++)
        {
            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(Advance(coordinator, dependent)?.Outcome);
            Assert.Equal(SoloHoldReasons.RepairPending, held.Decision?.DiscriminatingEvidence);
        }

        store = new SoloBaselineRepairStore(storePath);
        var repair = Assert.IsType<SoloRepairGroup>(store.ReadGroup(finding.GroupKey));
        Assert.Equal(intake.Get(request)?.GoalId, repair.RepairGoal);
        if (existing is not null) Assert.Equal(existing.GoalId, repair.RepairGoal);
        Assert.Equal(maintenance.Id, (await portfolio.GetGoalMembershipAsync(repair.RepairGoal!))?.EpicId);
        Assert.Equal(1, Count(storePath, "solo_repair_group"));
        Assert.Equal(1, Count(storePath, "solo_repair_dependency"));
        Assert.Equal(finding.Proof, Assert.Single(store.ReadFindings(dependent.Id.Value)).Proof);
        Assert.Empty(escalations);
    }

    private static SoloBaselineRepairCoordinator Coordinator(SoloAttributedFailureRouteFixture fixture,
        OrchestratorWorkspace workspace, AgentOrchestratorKernel kernel, List<string> escalations) =>
        new(workspace, kernel, new SoloRepairWorkflowTests.ControlledVerifier(fixture.Root),
            _ => new(fixture.Context.CandidateSha, fixture.Context.MainSha), _ => fixture.Context,
            () => throw new InvalidOperationException("The inherited rows must not route to a Developer."),
            _ => new(true, false, false, false, false, false),
            (_, _, message) =>
            {
                escalations.Add(message);
                return LandingEscalationWriteResult.CompletedWithoutExternalSinks;
            },
            (_, _) => throw new InvalidOperationException("A repair must land before validation."));

    private static ConductorAdvanceResult? Advance(SoloBaselineRepairCoordinator coordinator, Goal goal) =>
        coordinator.TryAdvance(goal, goal.Id.Value[..8], ConductorAutonomyPolicy.Permissive);

    internal static SoloRepairFinding Finding()
    {
        const string first = SoloAttributedFailureRouteFixture.First;
        var baseline = new string('a', 40);
        return new(new string('b', 40), baseline, baseline, "solo tests", first + "(value: 1)",
            SoloAttributedFailureRouteFixture.Project, $"Sample.Tests FullyQualifiedName~{first}",
            $"reviewer focused evidence: Sample.Tests FullyQualifiedName~{first}", true, "attempt",
            new("proof.json", "proof-digest"), [new("candidate.trx", "candidate-digest")],
            [new("baseline.trx", "baseline-digest")], null);
    }

    internal static long Count(string path, string table)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Assert.IsType<long>(command.ExecuteScalar());
    }

    private static void ExecuteSql(string path, string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
