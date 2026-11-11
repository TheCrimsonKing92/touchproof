using System.Reflection;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each fixture owns its kernel and gate artifact directory.
public sealed class ProspectiveEvidenceGateResolverSelectorlessTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CurrentFullGateClosesNullAndEmptySelectorsAndAnswersPersist(bool typed)
    {
        using var f = new ProspectiveEvidenceGateFixture();
        var waits = new[]
        {
            CreateWait(f.Kernel, f.Goal, typed: typed),
            CreateWait(f.Kernel, f.Goal, names: [], owner: "acceptance", typed: typed)
        };
        var attempt = WriteCurrentAttempt(f);

        var result = f.Resolve();

        Assert.Equal(waits.Select(wait => wait.Id), result.ResolvedRequestIds);
        Assert.Empty(result.UnresolvedWaits);
        foreach (var wait in waits)
        {
            Assert.True(wait.IsCompleted);
            Assert.Contains(attempt.Id, wait.Answer, StringComparison.Ordinal);
            Assert.Contains(f.CandidateSha, wait.Answer, StringComparison.Ordinal);
            Assert.Contains(attempt.TrxPath, wait.Answer, StringComparison.Ordinal);
            Assert.Equal(HumanInputAnswerOrigin.Worker, wait.AuthoritativeAnswer!.Origin);
            Assert.Single(wait.AnswerHistory);
        }
        Assert.Empty(f.Kernel.GetPendingHumanInput(f.Goal.Id));
        var answers = waits.Select(wait => wait.Answer).ToArray();
        var nextCandidate = new string('b', 40);
        WriteCurrentAttempt(f, candidateSha: nextCandidate, outcome: ConductorParallelAcceptanceAttemptOutcome.Failed);

        var later = ProspectiveEvidenceGateResolver.Resolve(f.Kernel, f.Goal, f.OrchestratorDirectory, nextCandidate);

        Assert.Empty(later.ResolvedRequestIds);
        Assert.Empty(later.UnresolvedWaits);
        Assert.Equal(answers, waits.Select(wait => wait.Answer));
        Assert.All(waits, wait => { Assert.True(wait.IsCompleted); Assert.Single(wait.AnswerHistory); });
    }

    [Fact]
    public void DifferentCandidateLeavesWaitOpen()
    {
        using var f = new ProspectiveEvidenceGateFixture();
        var wait = CreateWait(f.Kernel, f.Goal);
        WriteCurrentAttempt(f, candidateSha: new string('b', 40));

        AssertUntouched(f, wait);
    }

    [Fact]
    public void FailedAttemptLeavesWaitOpen()
    {
        using var f = new ProspectiveEvidenceGateFixture();
        var wait = CreateWait(f.Kernel, f.Goal);
        WriteCurrentAttempt(f, outcome: ConductorParallelAcceptanceAttemptOutcome.Failed);

        AssertUntouched(f, wait);
    }

    [Fact]
    public void OperatorOwnerLeavesWaitOpenDespitePassingAttempt()
    {
        using var f = new ProspectiveEvidenceGateFixture();
        var wait = CreateWait(f.Kernel, f.Goal, owner: "operator");
        WriteCurrentAttempt(f);

        AssertUntouched(f, wait);
    }

    [Fact]
    public void PostLandingAvailabilityLeavesWaitOpenDespitePassingAttempt()
    {
        using var f = new ProspectiveEvidenceGateFixture();
        var wait = CreateWait(f.Kernel, f.Goal, availability: "post-landing");
        WriteCurrentAttempt(f);

        AssertUntouched(f, wait);
        Assert.Empty(PostLandingOperatorCheck.HoldingLanding([wait]));
    }

    [Fact]
    public void OtherRoleOwnerLeavesWaitOpenDespitePassingAttempt()
    {
        using var f = new ProspectiveEvidenceGateFixture();
        var wait = CreateWait(f.Kernel, f.Goal, owner: "reviewer");
        WriteCurrentAttempt(f);

        AssertUntouched(f, wait);
    }

    [Fact]
    public void AttemptWithoutTrxPathsLeavesWaitOpen()
    {
        using var f = new ProspectiveEvidenceGateFixture();
        var wait = CreateWait(f.Kernel, f.Goal);
        var attempt = WriteCurrentAttempt(f);
        UpdateMetadata(attempt.MetadataPath, "testResultPaths", new JsonArray());

        AssertUntouched(f, wait);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(1)]
    public void MissingOrDifferentBriefVersionLeavesWaitOpen(int? offset)
    {
        using var f = new ProspectiveEvidenceGateFixture();
        var wait = CreateWait(f.Kernel, f.Goal);
        var attempt = WriteCurrentAttempt(f);
        UpdateMetadata(attempt.MetadataPath, "briefVersion",
            offset is null ? null : JsonValue.Create(f.Goal.AuthoritativeBrief.Version + offset));

        AssertUntouched(f, wait);
    }

    [Fact]
    public void MissingTrxRowsStillUseFullGateReceipt()
    {
        using var f = new ProspectiveEvidenceGateFixture();
        var wait = CreateWait(f.Kernel, f.Goal);
        var attempt = WriteCurrentAttempt(f);
        File.Delete(attempt.TrxPath);

        var result = f.Resolve();

        Assert.Equal([wait.Id], result.ResolvedRequestIds);
        Assert.True(wait.IsCompleted);
        Assert.Contains(attempt.Id, wait.Answer, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("acceptance-gate", "post-implementation", false)]
    [InlineData("operator", "post-implementation", false)]
    [InlineData("acceptance-gate", "post-landing", false)]
    [InlineData("acceptance-gate", "post-implementation", true)]
    public void OrdinaryWorkerAnswerCannotBypassTypedPlannerGuard(
        string owner, string availability, bool named)
    {
        using var f = new ProspectiveEvidenceGateFixture();
        var wait = CreateWait(f.Kernel, f.Goal, owner: owner, availability: availability,
            names: named ? ["GateTests.Check"] : null);
        var attempt = WriteCurrentAttempt(f);

        Assert.Throws<UnauthorizedAccessException>(() => f.Kernel.SubmitHumanInput(wait.Id,
            $"Satisfied by gate attempt {attempt.Id} for candidate {f.CandidateSha}",
            origin: HumanInputAnswerOrigin.Worker, supersedeSiblings: false));

        Assert.False(wait.IsCompleted);
        Assert.Null(wait.Answer);
        Assert.Empty(wait.AnswerHistory);
    }

    [Theory]
    [InlineData("operator", "post-implementation", false, HumanWaitKind.ProspectiveAcceptanceEvidence)]
    [InlineData("reviewer", "post-implementation", false, HumanWaitKind.ProspectiveAcceptanceEvidence)]
    [InlineData("acceptance-gate", "post-landing", false, HumanWaitKind.ProspectiveAcceptanceEvidence)]
    [InlineData("acceptance-gate", "post-implementation", true, HumanWaitKind.ProspectiveAcceptanceEvidence)]
    [InlineData("acceptance-gate", "post-implementation", false, HumanWaitKind.SpecClarification)]
    public void HostReceiptCannotAuthorizeOtherTypedPlannerWaits(
        string owner, string availability, bool named, HumanWaitKind kind)
    {
        using var f = new ProspectiveEvidenceGateFixture();
        var wait = CreateWait(f.Kernel, f.Goal, owner: owner, availability: availability,
            names: named ? ["GateTests.Check"] : null, kind: kind);
        // Exercise the host-only overload with a valid receipt result: the shape guard
        // must still reject these waits independently of the receipt validator.
        Assert.Throws<UnauthorizedAccessException>(() =>
            PlannerHumanInputAnswerAuthorization.SubmitHumanInput(
                f.Kernel, wait.Id, "Host receipt", _ => true));
        Assert.False(wait.IsCompleted);
        Assert.Null(wait.Answer);
        Assert.Empty(wait.AnswerHistory);
    }

    internal static HumanInputRequest CreateWait(AgentOrchestratorKernel kernel, Goal goal,
        IReadOnlyList<string>? names = null, string owner = "acceptance-gate",
        string availability = "post-implementation", bool typed = true,
        HumanWaitKind kind = HumanWaitKind.ProspectiveAcceptanceEvidence)
    {
        var task = goal.Tasks.First();
        var key = Guid.NewGuid().ToString("N");
        return kernel.RequestHumanInputDeduplicated(goal.Id, task.Id, $"Provide {key}",
            kind: kind, evidenceOwner: owner, testNames: names,
            plannerIdentity: typed ? new PlannerEvidenceIdentity(goal.Id.Value, task.Id.Value,
                goal.AuthoritativeBrief.Version, 1, key) : null,
            plannerEvidence: new PlannerEvidencePayload(1, key, availability, owner,
                "Gate receipts", "Required evidence", null, null, names)).Request;
    }

    internal static void UpdateMetadata(string path, string field, JsonNode? value)
    {
        var metadata = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        metadata[field] = value;
        File.WriteAllText(path, metadata.ToJsonString());
    }

    private static (string Id, string TrxPath, string MetadataPath) WriteCurrentAttempt(
        ProspectiveEvidenceGateFixture f, string? candidateSha = null,
        ConductorParallelAcceptanceAttemptOutcome outcome = ConductorParallelAcceptanceAttemptOutcome.Passed)
    {
        var attempt = f.WriteAttempt([], candidateSha, outcome);
        UpdateMetadata(attempt.MetadataPath, "briefVersion", JsonValue.Create(f.Goal.AuthoritativeBrief.Version));
        return attempt;
    }

    private static void AssertUntouched(ProspectiveEvidenceGateFixture f, HumanInputRequest wait)
    {
        var result = f.Resolve();
        Assert.Empty(result.ResolvedRequestIds);
        Assert.Empty(result.UnresolvedWaits);
        Assert.False(wait.IsCompleted);
        Assert.Null(wait.Answer);
        Assert.Empty(wait.AnswerHistory);
        Assert.Equal(wait.Id, Assert.Single(f.Kernel.GetPendingHumanInput(f.Goal.Id)).Id);
    }
}
