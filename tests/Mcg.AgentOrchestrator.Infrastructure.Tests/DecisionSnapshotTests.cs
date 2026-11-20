using System.Globalization;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its state copy and clock.
public sealed class DecisionSnapshotTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private const string TaskKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task StoredLegacyReviewerRecord_CapturesCandidateParserFailureAndKeepsSibling()
    {
        using var fixture = new DecisionSnapshotTestDirectory();
        var state = Directory.CreateDirectory(Path.Combine(fixture.Path, "copy")).FullName;
        var db = Path.Combine(state, "state.db");
        var legacy = VerifiedGoal("00000000000000000000000000000001", "reviewed:1");
        var sibling = VerifiedGoal("00000000000000000000000000000002", "2");
        await SeedAsync(db, legacy, sibling);
        var adoptedPath = Path.Combine(fixture.Path, "adopted.json");
        var candidatePath = Path.Combine(fixture.Path, "candidate.json");
        var adoptedRecords = new List<string>();
        var candidateRecords = new List<string>();

        await DecisionSnapshotWriter.WriteAsync(state, adoptedPath, AsOf, restored =>
        {
            var stored = restored.Tasks.Single().LastVerification!.StandardOutput;
            adoptedRecords.Add(stored);
            _ = int.Parse(stored.StartsWith("reviewed:", StringComparison.Ordinal) ? stored[9..] : stored,
                CultureInfo.InvariantCulture);
            return Goal.FromSnapshot(restored);
        });
        await DecisionSnapshotWriter.WriteAsync(state, candidatePath, AsOf, restored =>
        {
            var stored = restored.Tasks.Single().LastVerification!.StandardOutput;
            candidateRecords.Add(stored);
            _ = int.Parse(stored, CultureInfo.InvariantCulture);
            return Goal.FromSnapshot(restored);
        });

        Assert.Equal(new[] { "reviewed:1", "2" }, adoptedRecords);
        Assert.Equal(adoptedRecords, candidateRecords);
        var adopted = DecisionSnapshot.Read(adoptedPath);
        var candidate = DecisionSnapshot.Read(candidatePath);
        Assert.Equal(adopted.EvaluationContext.StateCopyHash, candidate.EvaluationContext.StateCopyHash);
        Assert.Equal(new[] { legacy.Id, sibling.Id }, adopted.Goals.Select(goal => goal.GoalId));
        Assert.Equal(new[] { legacy.Id, sibling.Id }, candidate.Goals.Select(goal => goal.GoalId));
        Assert.Equal(DecisionSnapshotDecision.CandidateBuilt, adopted.Goals[0].Decision!.GateOutcome);
        Assert.True(adopted.Goals[0].Decision!.CountsAsPassed[TaskKey]);
        Assert.Null(candidate.Goals[0].Decision);
        Assert.Equal(typeof(FormatException).FullName, candidate.Goals[0].Exception!.Type);
        Assert.Contains("reviewed:1", candidate.Goals[0].Exception!.Message, StringComparison.Ordinal);
        Assert.Equal(DecisionSnapshotDecision.CandidateBuilt, candidate.Goals[1].Decision!.GateOutcome);

        var comparison = DecisionSnapshotComparer.Compare(adoptedPath, candidatePath, []);
        Assert.False(comparison.Passed);
        Assert.Null(comparison.InputError);
        var failure = Assert.Single(comparison.Differences);
        Assert.Equal(legacy.Id, failure.GoalId);
        Assert.Equal(DecisionSnapshotComparer.Exception, failure.DecisionKind);
        Assert.Equal(DecisionSnapshotDecision.CandidateBuilt, failure.Adopted!.Decision!.GateOutcome);
        Assert.Equal(candidate.Goals[0].Exception, failure.Candidate!.Exception);
    }

    [Fact]
    public async Task PersistedLease_IsEvaluatedAtFixedTimeWithoutRecovery()
    {
        using var fixture = new DecisionSnapshotTestDirectory();
        var state = Directory.CreateDirectory(Path.Combine(fixture.Path, "copy")).FullName;
        var db = Path.Combine(state, "state.db");
        var goal = VerifiedGoal("00000000000000000000000000000001", "1");
        await SeedAsync(db, goal);
        var leases = new ReconcileSweepRemediationStore(db);
        const string owner = "goal-evidence:fixture";
        Assert.True(leases.TryClaimAcceptanceLease(goal.Id, owner, TimeSpan.FromMinutes(30)));
        var recorded = Assert.IsType<ReconcileAcceptanceLeaseState>(
            leases.TryGetAcceptanceLease(goal.Id, TimeSpan.FromMinutes(30)));
        var snapshotPath = Path.Combine(fixture.Path, "decision.json");

        await DecisionSnapshotWriter.WriteAsync(state, snapshotPath, recorded.AcquiredAtUtc);
        var held = Assert.Single(DecisionSnapshot.Read(snapshotPath).Goals);
        Assert.Equal(typeof(ConductorDriver.EvidenceMutationLeaseUnavailableException).FullName, held.Exception!.Type);
        Assert.Contains(owner, held.Exception.Message, StringComparison.Ordinal);
        Assert.Equal(recorded, leases.TryGetAcceptanceLease(goal.Id, TimeSpan.FromMinutes(30)));

        await DecisionSnapshotWriter.WriteAsync(state, snapshotPath, recorded.ExpiresAtUtc);
        Assert.Equal(DecisionSnapshotDecision.CandidateBuilt,
            Assert.Single(DecisionSnapshot.Read(snapshotPath).Goals).Decision!.GateOutcome);
    }

    private static GoalSnapshot VerifiedGoal(string id, string output)
    {
        var verification = new TaskVerificationSnapshot("verify", ".", 0, output, "", AsOf);
        return new GoalSnapshot(id, "Stored review result", GoalStatus.Verified,
            [new TaskSnapshot(TaskKey, "Review", AgentRole.Reviewer, WorkTaskStatus.Completed, null,
                null, verification, [verification], null, null)],
            []);
    }

    private static async Task SeedAsync(string db, params GoalSnapshot[] goals)
    {
        StateDbMigrations.EnsureUpToDate(db);
        var repository = new SqliteOrchestratorStateRepository(db);
        await repository.SaveAsync(AgentOrchestratorKernel.FromSnapshot(new(goals, [])));
    }

}
