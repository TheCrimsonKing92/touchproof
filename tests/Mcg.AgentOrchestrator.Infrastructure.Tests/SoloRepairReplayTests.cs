using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using static SoloAttributedFailureRouteFixture;

// Parallel-safe: each test owns its database and uses local logical work keys.
public sealed class SoloRepairReplayTests
{
    [Fact]
    public void CompletedValidation_RequiresItsRetainedProofAndOriginalSelectionArtifacts()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        var workspace = SoloRepairWorkflowTests.Workspace(fixture);
        var store = new SoloBaselineRepairStore(Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName));
        var goal = fixture.Goal.Id.Value;
        var finding = Finding();
        store.Retain(goal, [finding]);
        store.ReserveGroup(finding.GroupKey, "{}");
        store.AcknowledgeRepair(finding.GroupKey, GoalId.New().Value);
        var result = SoloRepairWorkflowTests.RecordPassedValidation(store, finding, finding.Main);
        bool Ready() => store.IsEvidenceCurrent(goal, store.ReadFindings(goal), finding.Candidate, finding.Main);
        Assert.True(Ready());

        var artifact = Assert.Single(result.Artifacts);
        var original = File.ReadAllBytes(artifact.Path);
        File.AppendAllText(artifact.Path, " ");
        Assert.False(Ready());
        File.WriteAllBytes(artifact.Path, original);
        Assert.True(Ready());

        File.Delete(result.Proof!.Path);
        Assert.False(Ready());
        Assert.Equal("passed", store.ReadCheck(SoloBaselineRepairStore.ValidationKey(finding, finding.Main))?.Outcome);
    }

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
    [InlineData("busy")]
    [InlineData("claimed")]
    [InlineData("verifier-result")]
    [InlineData("flushed-result")]
    [InlineData("executed")]
    public async Task MissingCandidateTrx_RecoversFocusedEvidenceAcrossDeferralAndRestart(string boundary)
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce(baselineFails: true, singleRow: true);
        fixture.ReplaceProof(proof => proof with { Checks = proof.Checks.Select(item => item with { Artifacts = [] }).ToArray() });
        fixture.Record(fixture.Summary);
        var finding = Assert.Single(SoloRepairEvidenceTests.Read(fixture).Findings);
        Assert.Equal(First, finding.Identity);
        Assert.Equal($"{Project}: FullyQualifiedName~{First}", finding.Request);
        var checkName = $"reviewer focused evidence: {GoalAcceptanceVerifier.ProjectLabel(Project)} FullyQualifiedName~{First}";
        Assert.Equal(checkName, finding.BaselineCheck);
        Assert.Empty(finding.CandidateArtifacts);
        Assert.False(finding.Inherited);

        var workspace = SoloRepairWorkflowTests.Workspace(fixture);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(fixture.Kernel);
        await new PortfolioStore(workspace.PortfolioStorePath).AddEpicAsync("baseline-maintenance");
        var storePath = Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName);
        var store = new SoloBaselineRepairStore(storePath);
        var key = SoloBaselineRepairStore.AttributionKey(fixture.Goal.Id.Value, finding, finding.Candidate, finding.Main);
        var verifier = new SoloRepairWorkflowTests.ControlledVerifier(fixture.Root)
        {
            Finding = finding with { BaselineCheck = checkName },
            AttributionBaselinePass = false
        };
        var escalations = new List<string>();
        if (boundary == "busy")
        {
            var busy = Coordinator(fixture, workspace, fixture.Kernel, escalations, verifier,
                (_, _) => throw new DotnetBuildSlotsBusyException(new("fixture-stable-slot", [])));
            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(Advance(busy, fixture.Goal)?.Outcome);
            Assert.Equal(SoloHoldReasons.RepairPending, held.Decision?.DiscriminatingEvidence);
            Assert.Equal(0, Count(storePath, "solo_repair_check"));
        }
        else if (boundary == "claimed")
        {
            Assert.True(store.TryClaimCheck(key));
        }
        else
        {
            var write = boundary == "executed" ? "UPDATE OF result ON solo_repair_check" : "INSERT ON solo_repair_check_execution";
            ExecuteSql(storePath, $"""
                CREATE TRIGGER stop_check_ack BEFORE {write}
                BEGIN SELECT RAISE(ABORT, 'lost check write'); END
                """);
            var interrupted = Coordinator(fixture, workspace, fixture.Kernel, escalations, verifier);
            Assert.Contains("lost check write", Assert.Throws<SqliteException>(() => Advance(interrupted, fixture.Goal)).Message);
            Assert.Equal(boundary == "executed" ? "inherited" : null, store.ReadCheckExecution(key)?.Outcome);
            var resultPath = Path.Combine(store.ResultDirectory(key), "focused-result.json");
            Assert.True(File.Exists(resultPath));
            if (boundary == "flushed-result") File.Move(resultPath, resultPath + ".tmp");
            Assert.Null(store.ReadCheck(key));
            ExecuteSql(storePath, "DROP TRIGGER stop_check_ack");
        }
        Assert.Equal(boundary is "executed" or "verifier-result" or "flushed-result" ? 1 : 0, verifier.FocusedRuns.Count);

        var restarted = await repository.LoadAsync();
        var dependent = restarted.GetGoal(fixture.Goal.Id);
        var coordinator = Coordinator(fixture, workspace, restarted, escalations, verifier);
        for (var tick = 0; tick < 2; tick++)
        {
            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(Advance(coordinator, dependent)?.Outcome);
            Assert.Equal(SoloHoldReasons.RepairPending, held.Decision?.DiscriminatingEvidence);
        }

        Assert.Equal((finding.Request!, finding.Candidate), Assert.Single(verifier.FocusedRuns));
        var result = Assert.IsType<SoloRepairCheckReceipt>(store.ReadCheck(key));
        Assert.Equal("inherited", result.Outcome);
        Assert.NotEmpty(result.CandidateArtifacts!);
        Assert.NotEmpty(result.Artifacts);
        Assert.NotNull(result.Proof);
        Assert.Null(store.ReadCheckExecution(key));
        Assert.Equal(1, Count(storePath, "solo_repair_dependency"));
        Assert.Equal(1, Count(storePath, "solo_repair_check"));
        Assert.Empty(escalations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CandidateMovement_ReexecutesBothArmsOnUnchangedRedMain(bool candidatePasses)
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce(baselineFails: true, singleRow: true);
        fixture.ReplaceProof(proof => proof with { Checks = proof.Checks.Select(item => item with { Artifacts = [] }).ToArray() });
        fixture.Record(fixture.Summary);
        var original = Assert.Single(SoloRepairEvidenceTests.Read(fixture).Findings);
        var workspace = SoloRepairWorkflowTests.Workspace(fixture);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(fixture.Kernel);
        await new PortfolioStore(workspace.PortfolioStorePath).AddEpicAsync("baseline-maintenance");
        fixture.Git("commit", "--allow-empty", "-m", "Move only the candidate");
        var candidate = fixture.Git("rev-parse", "HEAD");
        fixture.Context = fixture.Context with { CandidateSha = candidate };
        var verifier = new SoloRepairWorkflowTests.ControlledVerifier(fixture.Root)
        {
            Finding = original,
            AttributionCandidatePass = candidatePasses,
            AttributionBaselinePass = false
        };
        var escalations = new List<string>();
        var coordinator = Coordinator(fixture, workspace, fixture.Kernel, escalations, verifier);

        _ = Advance(coordinator, fixture.Goal);
        _ = Advance(coordinator, fixture.Goal);

        Assert.Equal((original.Request!, candidate), Assert.Single(verifier.FocusedRuns));
        var storePath = Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName);
        var store = new SoloBaselineRepairStore(storePath);
        var key = SoloBaselineRepairStore.AttributionKey(fixture.Goal.Id.Value, original, candidate, original.Main);
        var receipt = Assert.IsType<SoloRepairCheckReceipt>(store.ReadCheck(key));
        Assert.Equal(candidatePasses ? "passed" : "inherited", receipt.Outcome);
        Assert.NotEmpty(receipt.CandidateArtifacts!);
        Assert.NotEmpty(receipt.Artifacts);
        Assert.Equal(candidatePasses ? 0 : 1, Count(storePath, "solo_repair_dependency"));
        Assert.Equal(!candidatePasses, coordinator.TryDeclineAdmission(fixture.Goal, candidate, original.Main, out _));
        if (!candidatePasses)
        {
            var current = Assert.Single(store.ReadFindings(fixture.Goal.Id.Value).Where(row => row.Inherited));
            Assert.Equal(candidate, current.Candidate);
            Assert.Equal(original.Main, current.Baseline);
            Assert.Equal(receipt.Proof, current.Proof);
        }
        Assert.Empty(escalations);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AttributionAcrossRestart_DeduplicatesOnlyEquivalentSelections(bool previousClaim, bool distinctSelection)
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce(baselineFails: true, singleRow: true);
        fixture.ReplaceProof(proof => proof with { Checks = proof.Checks.Select(item => item with { Artifacts = [] }).ToArray() });
        fixture.Record(fixture.Summary);
        var original = Assert.Single(SoloRepairEvidenceTests.Read(fixture).Findings);
        var workspace = SoloRepairWorkflowTests.Workspace(fixture);
        var path = Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName);
        var store = new SoloBaselineRepairStore(path);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(fixture.Kernel);
        fixture.Git("commit", "--allow-empty", "-m", "Move only the candidate");
        var candidate = fixture.Git("rev-parse", "HEAD");
        fixture.Context = fixture.Context with { CandidateSha = candidate };
        var current = original with { Candidate = candidate };
        if (distinctSelection)
            current = current with { Project = "Other.csproj", Request = "Other: FullyQualifiedName~" + original.Identity };
        store.Retain(fixture.Goal.Id.Value, [original, current]);
        Assert.Equal(2, store.ReadFindings(fixture.Goal.Id.Value).Count);
        if (previousClaim)
            Assert.True(store.TryClaimCheck(SoloBaselineRepairStore.PreviousAttributionKey(
                fixture.Goal.Id.Value, current, candidate, current.Main)));
        string[] expectedRequests = distinctSelection ? [original.Request!, current.Request!] : [original.Request!];
        var verifier = new SoloRepairWorkflowTests.ControlledVerifier(fixture.Root)
        {
            UnresolvedAttributionRequests = expectedRequests
        };
        var escalations = new List<string>();
        _ = Advance(Coordinator(fixture, workspace, fixture.Kernel, escalations, verifier), fixture.Goal);
        Assert.Equal(expectedRequests.Order(), verifier.FocusedRuns.Select(run => run.Request).Order());
        Assert.All(verifier.FocusedRuns, run => Assert.Equal(candidate, run.Sha));

        var restarted = await repository.LoadAsync();
        _ = Advance(Coordinator(fixture, workspace, restarted, escalations, verifier), restarted.GetGoal(fixture.Goal.Id));

        Assert.Equal(expectedRequests.Length, verifier.FocusedRuns.Count);
        var originalRequest = store.ResolveAttributionRequest(fixture.Goal.Id.Value, original, candidate, current.Main);
        var currentRequest = store.ResolveAttributionRequest(fixture.Goal.Id.Value, current, candidate, current.Main);
        Assert.Equal(!distinctSelection, originalRequest.Key == currentRequest.Key);
        Assert.Equal("unresolved", store.ReadCheck(originalRequest.Key)?.Outcome);
        Assert.Equal("unresolved", store.ReadCheck(currentRequest.Key)?.Outcome);
        Assert.Equal(!previousClaim || distinctSelection, currentRequest.AllowCandidatePass);
        var unboundLegacyClaim = previousClaim && distinctSelection ? 1 : 0;
        Assert.Equal(expectedRequests.Length + unboundLegacyClaim + 1, Count(path, "solo_repair_check")); // Includes the escalation.
        Assert.Single(escalations);
    }

    [Theory]
    [InlineData("no-proof", false, false)]
    [InlineData("missing-result", true, false)]
    [InlineData("missing-trx", false, false)]
    [InlineData("missing-trx", true, true)]
    public async Task CompletedLegacyAttribution_PreservesTheChargeWithUnavailableProof(
        string unavailable, bool allowCandidatePass, bool distinctSelection)
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce(baselineFails: true, singleRow: true);
        fixture.ReplaceProof(proof => proof with { Checks = proof.Checks.Select(item => item with { Artifacts = [] }).ToArray() });
        fixture.Record(fixture.Summary);
        var finding = Assert.Single(SoloRepairEvidenceTests.Read(fixture).Findings);
        var workspace = SoloRepairWorkflowTests.Workspace(fixture);
        var path = Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName);
        var store = new SoloBaselineRepairStore(path);
        var goal = fixture.Goal.Id.Value;
        var sibling = finding with { Project = "Other.csproj", Request = "Other: FullyQualifiedName~" + finding.Identity };
        store.Retain(goal, distinctSelection ? [finding, sibling] : [finding]);
        var legacy = SoloBaselineRepairStore.AttributionRequests(goal, finding, finding.Candidate, finding.Main)
            .Skip(1).Single(request => request.AllowCandidatePass == allowCandidatePass);
        var identity = SoloBaselineRepairStore.FocusedProofIdentity(
            legacy.Key, finding, finding.Candidate, finding.Main, true, legacy.AllowCandidatePass);
        var recorded = new SoloRepairCheckReceipt("unresolved", "The verifier produced no result.", []);
        if (unavailable != "no-proof")
        {
            var root = Directory.CreateDirectory(store.ResultDirectory(legacy.Key)).FullName;
            var resultPath = Path.Combine(root, "focused-result.json");
            var trx = Path.Combine(root, "baseline.trx");
            WriteTrx(trx, false, finding.Identity);
            var artifact = new SoloAcceptanceArtifact(trx, SoloBaselineRepairStore.Digest(File.ReadAllBytes(trx)));
            recorded = new("inherited", finding.BaselineCheck!, [artifact], [artifact]);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new SoloRepairVerifierResult(identity, recorded));
            File.WriteAllBytes(resultPath, bytes);
            recorded = recorded with { Proof = new(resultPath, SoloBaselineRepairStore.Digest(bytes)) };
            File.Delete(unavailable == "missing-result" ? resultPath : trx);
        }
        Assert.True(store.TryClaimCheck(legacy.Key));
        store.CompleteCheck(legacy.Key, recorded);

        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(fixture.Kernel);
        var restarted = await repository.LoadAsync();
        var verifier = new SoloRepairWorkflowTests.ControlledVerifier(fixture.Root)
        {
            UnresolvedAttributionRequests = [finding.Request!, sibling.Request!]
        };
        var escalations = new List<string>();
        var coordinator = Coordinator(fixture, workspace, restarted, escalations, verifier);
        for (var tick = 0; tick < 2; tick++)
        {
            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(Advance(coordinator, restarted.GetGoal(fixture.Goal.Id))?.Outcome);
            Assert.Equal(SoloHoldReasons.RepairEvidenceRequired, held.Decision?.DiscriminatingEvidence);
        }

        string[] expectedRuns = distinctSelection ? [sibling.Request!] : [];
        Assert.Equal(expectedRuns, verifier.FocusedRuns.Select(run => run.Request));
        Assert.Equal(legacy, store.ResolveAttributionRequest(goal, finding, finding.Candidate, finding.Main));
        Assert.Null(store.ReadCheck(SoloBaselineRepairStore.AttributionKey(goal, finding, finding.Candidate, finding.Main)));
        Assert.Equal("unresolved", store.ReadVerifiedCheck(legacy.Key, identity)?.Outcome);
        Assert.Equal(0, Count(path, "solo_repair_dependency"));
        Assert.Equal(2 + expectedRuns.Length, Count(path, "solo_repair_check")); // Includes the escalation.
        Assert.Single(escalations);
    }

    [Fact]
    public void CurrentPairProof_PreservesTheOtherSelectionWithTheSameTestLabel()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        var store = new SoloBaselineRepairStore(Path.Combine(fixture.Root, SoloBaselineRepairStore.FileName));
        var proof = Finding();
        var unresolved = proof with
        {
            Project = "Other.csproj", Request = "Other: FullyQualifiedName~" + proof.Identity,
            BaselineCheck = "other focused check", Inherited = false, Reason = "Requires attribution"
        };

        store.Retain("dependent", [unresolved]);
        store.Retain("dependent", [proof]);

        var findings = store.ReadFindings("dependent");
        Assert.Equal(2, findings.Count);
        Assert.True(Assert.Single(findings.Where(finding => finding.Request == proof.Request)).Inherited);
        Assert.Equal(unresolved.Reason, Assert.Single(findings.Where(finding => finding.Request == unresolved.Request)).Reason);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UnavailableRetainedProof_CannotRouteAfterRestart(bool inherited, bool removeEnvelope)
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce(baselineFails: inherited, singleRow: true);
        fixture.Record(fixture.Summary);
        var finding = Assert.Single(SoloRepairEvidenceTests.Read(fixture).Findings);
        Assert.Equal(inherited, finding.Inherited);
        Assert.Null(finding.Reason);
        var workspace = SoloRepairWorkflowTests.Workspace(fixture);
        var path = Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName);
        var store = new SoloBaselineRepairStore(path);
        store.Retain(fixture.Goal.Id.Value, [finding]);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(fixture.Kernel);
        await new PortfolioStore(workspace.PortfolioStorePath).AddEpicAsync("baseline-maintenance");

        if (removeEnvelope)
            File.Delete(finding.Proof.Path);
        else
            File.AppendAllText(Assert.Single(finding.CandidateArtifacts).Path, " ");
        var restarted = await repository.LoadAsync();
        var goal = restarted.GetGoal(fixture.Goal.Id);
        var verifier = new SoloRepairWorkflowTests.ControlledVerifier(fixture.Root)
        {
            UnresolvedAttributionRequests = [finding.Request!]
        };
        var escalations = new List<string>();
        var coordinator = Coordinator(fixture, workspace, restarted, escalations, verifier);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(Advance(coordinator, goal)?.Outcome);

        Assert.Equal(SoloHoldReasons.RepairEvidenceRequired, held.Decision?.DiscriminatingEvidence);
        Assert.True(coordinator.TryDeclineAdmission(goal, finding.Candidate, finding.Main, out _));
        Assert.Single(verifier.FocusedRuns);
        Assert.Equal(0, Count(path, "solo_repair_group"));
        Assert.Equal(inherited ? 1 : 0, Count(path, "solo_repair_dependency"));
        Assert.Equal(0, goal.AutomaticAcceptanceRetryCount);
        Assert.Contains(finding.Identity, Assert.Single(escalations));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OppositeSamePairProofs_AreBothRetainedAndCannotRoute(bool inheritedFirst)
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        var workspace = SoloRepairWorkflowTests.Workspace(fixture);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(fixture.Kernel);
        await new PortfolioStore(workspace.PortfolioStorePath).AddEpicAsync("baseline-maintenance");
        var escalations = new List<string>();
        var coordinator = Coordinator(fixture, workspace, fixture.Kernel, escalations);
        var path = Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName);
        var store = new SoloBaselineRepairStore(path);
        await fixture.Produce(baselineFails: inheritedFirst, singleRow: true, attemptId: "first");
        fixture.Record(fixture.Summary);
        var first = Assert.Single(SoloRepairEvidenceTests.Read(fixture).Findings);
        Assert.Equal(inheritedFirst, first.Inherited);
        Assert.Null(first.Reason);
        if (inheritedFirst)
        {
            var waiting = Assert.IsType<ConductorAdvanceOutcome.Held>(Advance(coordinator, fixture.Goal)?.Outcome);
            Assert.Equal(SoloHoldReasons.RepairPending, waiting.Decision?.DiscriminatingEvidence);
            Assert.Equal(1, Count(path, "solo_repair_dependency"));
        }
        else
        {
            // Resume from a retained introduced proof before the Developer route was delivered.
            store.Retain(fixture.Goal.Id.Value, [first]);
            Assert.Equal(first.Proof, Assert.Single(store.ReadFindings(fixture.Goal.Id.Value, introduced: true)).Proof);
        }
        await fixture.Produce(baselineFails: !inheritedFirst, singleRow: true, attemptId: "second");
        fixture.Record(fixture.Summary);
        var second = Assert.Single(SoloRepairEvidenceTests.Read(fixture).Findings);
        Assert.NotEqual(first.Inherited, second.Inherited);
        Assert.Equal(first.Key, second.Key);
        var hold = Assert.IsType<ConductorAdvanceOutcome.Held>(Advance(coordinator, fixture.Goal)?.Outcome);
        Assert.Equal(SoloHoldReasons.RepairEvidenceRequired, hold.Decision?.DiscriminatingEvidence);

        var reopened = Coordinator(fixture, workspace, fixture.Kernel, escalations);
        _ = Advance(reopened, fixture.Goal);

        Assert.Empty(store.ReadFindings(fixture.Goal.Id.Value, introduced: true));
        var finding = Assert.Single(store.ReadFindings(fixture.Goal.Id.Value));
        Assert.True(finding.HasConflictingProofs);
        Assert.Equal(first.Request, finding.Request);
        using var db = new SqliteConnection($"Data Source={path};Pooling=False");
        db.Open();
        using var query = db.CreateCommand();
        query.CommandText = "SELECT json_extract(proof, '$.Proof.Path') FROM solo_repair_conflicting_proof ORDER BY 1";
        using var proofs = query.ExecuteReader();
        Assert.True(proofs.Read());
        Assert.Equal(first.Proof.Path, proofs.GetString(0));
        Assert.True(proofs.Read());
        Assert.Equal(second.Proof.Path, proofs.GetString(0));
        Assert.False(proofs.Read());
        Assert.Equal(0, Count(path, "solo_repair_dependency"));
        Assert.Contains(First, Assert.Single(escalations));
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
        OrchestratorWorkspace workspace, AgentOrchestratorKernel kernel, List<string> escalations,
        SoloRepairWorkflowTests.ControlledVerifier? verifier = null,
        Func<string, CancellationToken, DotnetBuildEnvironmentLease>? acquireSlot = null) =>
        new(workspace, kernel, verifier ?? new SoloRepairWorkflowTests.ControlledVerifier(fixture.Root),
            _ => new(fixture.Context.CandidateSha, fixture.Context.MainSha), _ => fixture.Context,
            () => throw new InvalidOperationException("The inherited rows must not route to a Developer."),
            _ => new(true, false, false, false, false, false),
            (_, _, message, _) =>
            {
                escalations.Add(message);
                return LandingEscalationWriteResult.CompletedWithoutExternalSinks;
            },
            acquireSlot ?? ((_, _) => verifier is not null
                ? AcceptanceStableSlotTestSupport.CreateFakeStableSlotLease(workspace.OrchestratorDirectory)
                : throw new InvalidOperationException("A repair must land before validation.")));

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
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Assert.IsType<long>(command.ExecuteScalar());
    }

    internal static void ExecuteSql(string path, string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
