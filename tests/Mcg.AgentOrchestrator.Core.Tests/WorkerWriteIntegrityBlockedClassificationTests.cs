using Mcg.AgentOrchestrator.Core;
using System.Text.Json;
using Xunit;

public sealed class WorkerWriteIntegrityBlockedClassificationTests
{
    private static readonly TaskId DeveloperId = new("write-integrity-developer");
    private static readonly DateTimeOffset DispatchedAt = DateTimeOffset.Parse("2026-11-07T11:58:00Z");
    private static readonly DateTimeOffset StartedAt = DispatchedAt.AddSeconds(1);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(null)]
    public void MediumIsEnvironmentalWithoutAutomaticRetry(bool? scopeComplete)
    {
        var verification = Verification(new("src/Feature.cs", WorkerWriteIntegrityLabel.Medium, true))
            with { AssignedScopeComplete = scopeComplete };
        var outcome = DispatchFailureClassifier.Classify(Developer(), verification);

        Assert.Equal("worker-write-integrity-blocked", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        Assert.Equal(TaskOutcomeClass.Environmental, outcome.OutcomeClass);
        Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
        Assert.Contains("src/Feature.cs", outcome.EvidenceSummary);
        Assert.Contains("Medium", outcome.EvidenceSummary);
    }

    [Theory]
    [InlineData(WorkerWriteIntegrityLabel.Low, true)]
    [InlineData(WorkerWriteIntegrityLabel.Medium, false)]
    [InlineData(WorkerWriteIntegrityLabel.Other, true)]
    [InlineData(WorkerWriteIntegrityLabel.Unlabeled, true)]
    public void InsufficientLabelEvidencePreservesIncompleteScope(WorkerWriteIntegrityLabel label, bool success)
    {
        AssertIncomplete(Verification(new("src/Feature.cs", label, success)));
    }

    [Fact]
    public void AbsentObservationPreservesIncompleteScope() => AssertIncomplete(Verification(null));

    [Fact]
    public void NonDeveloperIgnoresObservation()
    {
        var task = new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer);
        var outcome = DispatchFailureClassifier.Classify(task,
            Verification(new("src/Feature.cs", WorkerWriteIntegrityLabel.Medium, true)));
        Assert.NotEqual("worker-write-integrity-blocked", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
    }

    [Fact]
    public void EarlierFailingTestsKeepPrecedence()
    {
        var output = Verification(null).StandardOutput.Replace(
            "tests: deferred - conductor", "tests: fail - focused regression failed", StringComparison.Ordinal);
        var verification = Verification(new("src/Feature.cs", WorkerWriteIntegrityLabel.Medium, true)) with
        {
            StandardOutput = output, AuthoritativeStandardOutput = output
        };
        var outcome = DispatchFailureClassifier.Classify(Developer(), verification);
        Assert.Equal("succeeded-worker-result-failing-tests", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        Assert.Equal(TaskOutcomeClass.RealFailure, outcome.OutcomeClass);
        Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
    }

    [Fact]
    public void SameRoundEnrichmentRetainsTransientObservation()
    {
        var plain = Verification(null);
        var observed = plain with { WriteIntegrityObservation = new("src/Feature.cs", WorkerWriteIntegrityLabel.Medium, true) };
        Assert.Equal(observed.WriteIntegrityObservation, plain.MergeSameRoundEnrichment(observed).WriteIntegrityObservation);
        Assert.Equal(observed.WriteIntegrityObservation, observed.MergeSameRoundEnrichment(plain).WriteIntegrityObservation);
    }

    [Fact]
    public void EveryTargetSurvivesSerializationAndClassifiesOnce()
    {
        var verification = MultipleVerification();
        var restored = JsonSerializer.Deserialize<TaskVerificationRecord>(JsonSerializer.Serialize(verification))!;
        Assert.Equal(2, restored.GetWriteIntegrityObservations().Count);
        Assert.Equal(JsonSerializer.Serialize(verification.WriteIntegrityEvidence), JsonSerializer.Serialize(restored.WriteIntegrityEvidence));
        var outcome = DispatchFailureClassifier.Classify(Developer(), restored);
        Assert.Equal(TaskOutcomeClass.Environmental, outcome.OutcomeClass);
        Assert.Equal("worker-write-integrity-blocked", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
        Assert.Contains("src/Feature.cs", outcome.EvidenceSummary);
        Assert.Contains("src/Other.cs", outcome.EvidenceSummary);
        Assert.NotEqual(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentHostProofWithoutSharedAttemptBindingClassifies(bool multiple)
    {
        var verification = (multiple ? MultipleVerification() : Verification(
            new("src/Feature.cs", WorkerWriteIntegrityLabel.Medium, true))) with { DispatchAttemptAt = null };
        var restored = JsonSerializer.Deserialize<TaskVerificationRecord>(JsonSerializer.Serialize(verification))!;

        Assert.Null(restored.DispatchAttemptAt);
        Assert.Equal(DispatchedAt, restored.WriteIntegrityEvidence!.DispatchedAt);
        Assert.Equal(multiple ? 2 : 1, restored.GetWriteIntegrityObservations().Count);
        var outcome = DispatchFailureClassifier.Classify(Developer(), restored);
        Assert.Equal(TaskOutcomeClass.Environmental, outcome.OutcomeClass);
        Assert.Equal("worker-write-integrity-blocked", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        Assert.NotEqual(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("WORKER_RESULT:\nblockers: none\nEND_WORKER_RESULT")]
    public void LocalProofWithoutAuthoritativeBlockerStaysUnproven(string? output)
    {
        var verification = MultipleVerification() with
        {
            DispatchAttemptAt = null,
            AuthoritativeStandardOutput = output
        };

        Assert.False(verification.WriteIntegrityEvidence!.EstablishesAllTargets(Developer(), verification));
        AssertIncomplete(verification);
    }

    [Theory]
    [InlineData("no-launch")]
    [InlineData("stale-launch")]
    [InlineData("other-task")]
    [InlineData("other-candidate")]
    [InlineData("other-dirty-state")]
    [InlineData("other-process")]
    [InlineData("other-completion-child")]
    [InlineData("invalid-completion-child")]
    [InlineData("medium-execution")]
    [InlineData("unknown-execution")]
    [InlineData("missing-process-identity")]
    [InlineData("stale-observation")]
    [InlineData("missing-attempt")]
    [InlineData("conflicting-attempt")]
    [InlineData("non-dispatch")]
    [InlineData("missing-candidate")]
    [InlineData("reported-blocker-mismatch")]
    [InlineData("low-member")]
    [InlineData("unknown-member")]
    [InlineData("unreadable-member")]
    [InlineData("duplicate-member")]
    [InlineData("conflicting-singular")]
    [InlineData("conflicting-enrichment")]
    public void IncompleteOrConflictingProofKeepsRealFailure(string kind)
    {
        var verification = MultipleVerification();
        var evidence = verification.WriteIntegrityEvidence!;
        var launch = evidence.LowLaunch!;
        evidence = kind switch
        {
            "no-launch" => evidence with { LowLaunch = null },
            "stale-launch" => evidence with { LowLaunch = launch with { DispatchedAt = DispatchedAt.AddDays(-1) } },
            "other-task" => evidence with { LowLaunch = launch with { TaskId = TaskId.New() } },
            "other-candidate" => evidence with { LowLaunch = launch with { WorktreeHeadSha = "other" } },
            "other-dirty-state" => evidence with { LowLaunch = launch with { DirtyStateHash = "other" } },
            "other-process" => evidence with { LowLaunch = launch with { ProcessId = 999 } },
            "other-completion-child" => evidence with { LowLaunch = launch with { CompletionChildProcessId = 999 } },
            "invalid-completion-child" => evidence with { LowLaunch = launch with { CompletionChildProcessId = 0 } },
            "medium-execution" => evidence with { LowLaunch = launch with { ObservedIntegrityRid = 8192 } },
            "unknown-execution" => evidence with { LowLaunch = launch with { ObservedIntegrityRid = 0 } },
            "missing-process-identity" => evidence with { LowLaunch = launch with { ImagePath = "" } },
            "stale-observation" => evidence with { DispatchedAt = DispatchedAt.AddDays(-1) },
            "missing-attempt" => evidence with { DispatchedAt = default },
            "missing-candidate" => evidence with { DirtyStateHash = null },
            "reported-blocker-mismatch" => evidence with { ReportedBlocker = "exact-blocker - unrelated failure" },
            "low-member" => evidence with { Observations = [evidence.Observations[0], evidence.Observations[1] with { ObservedLabel = WorkerWriteIntegrityLabel.Low }] },
            "unknown-member" => evidence with { Observations = [evidence.Observations[0], evidence.Observations[1] with { ObservedLabel = WorkerWriteIntegrityLabel.Other }] },
            "unreadable-member" => evidence with { Observations = [evidence.Observations[0], evidence.Observations[1] with { LabelQuerySucceeded = false }] },
            "duplicate-member" => evidence with { Observations = [evidence.Observations[0], evidence.Observations[0]] },
            _ => evidence
        };
        verification = verification with { WriteIntegrityEvidence = evidence };
        if (kind == "conflicting-attempt") verification = verification with { DispatchAttemptAt = DispatchedAt.AddDays(-1) };
        if (kind == "non-dispatch") verification = verification with { EvidenceSource = VerificationEvidenceSource.Unspecified };
        if (kind == "conflicting-singular") verification = verification with { WriteIntegrityObservation = evidence.Observations[0] };
        if (kind == "conflicting-enrichment")
            verification = verification.MergeSameRoundEnrichment(verification with
            {
                WriteIntegrityEvidence = evidence with { Observations = [evidence.Observations[0]] }
            });
        AssertIncomplete(verification);
    }

    [Fact]
    public void NativeWorkerProofRetainsItsAssociationWithDifferentCompletionChild()
    {
        var verification = MultipleVerification();
        var evidence = verification.WriteIntegrityEvidence!;
        verification = verification with
        {
            WriteIntegrityEvidence = evidence with
            {
                LowLaunch = evidence.LowLaunch! with { ProcessId = 999, CompletionChildProcessId = verification.ChildProcessId }
            }
        };
        var restored = JsonSerializer.Deserialize<TaskVerificationRecord>(JsonSerializer.Serialize(verification))!;
        Assert.Equal(999, restored.WriteIntegrityEvidence!.LowLaunch!.ProcessId);
        Assert.Equal(verification.ChildProcessId, restored.WriteIntegrityEvidence.LowLaunch.CompletionChildProcessId);
        Assert.Equal(TaskOutcomeClass.Environmental, DispatchFailureClassifier.Classify(Developer(), restored).OutcomeClass);
    }

    [Fact]
    public void LegacySingularIsReadableButCannotSupplyLaunchProof()
    {
        var legacy = Verification(new("src/Feature.cs", WorkerWriteIntegrityLabel.Medium, true)) with { WriteIntegrityEvidence = null };
        var restored = JsonSerializer.Deserialize<TaskVerificationRecord>(JsonSerializer.Serialize(legacy))!;
        Assert.Equal(legacy.WriteIntegrityObservation, Assert.Single(restored.GetWriteIntegrityObservations()));
        AssertIncomplete(restored);
    }

    [Fact]
    public void RequestedLowProfileDoesNotProveExecution()
    {
        var verification = Verification(new("src/Feature.cs", WorkerWriteIntegrityLabel.Medium, true));
        Assert.True(Developer().LastDispatch!.SandboxLowIntegrity);
        AssertIncomplete(verification with { WriteIntegrityEvidence = verification.WriteIntegrityEvidence! with { LowLaunch = null } });
    }

    [Theory]
    [InlineData("failed-tests")]
    [InlineData("host-build")]
    [InlineData("host-policy")]
    [InlineData("provider-fault")]
    public void IndependentFailuresKeepTheirExistingDisposition(string kind)
    {
        var verification = MultipleVerification() with { ExitCode = 1 };
        if (kind == "failed-tests")
        {
            var output = verification.StandardOutput.Replace("tests: deferred - conductor", "tests: fail - source regression failed", StringComparison.Ordinal);
            verification = verification with { StandardOutput = output, AuthoritativeStandardOutput = output };
        }
        if (kind == "host-build") verification = verification with
        {
            StandardError = DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed)
        };
        if (kind == "host-policy") verification = verification with { OrchestratorFailureReason = "Host policy rejected independent source change" };
        if (kind == "provider-fault") verification = verification with { ProviderFailureKind = ProviderFailureKind.Sandbox1312 };
        var baseline = DispatchFailureClassifier.Classify(Developer(), verification with { WriteIntegrityEvidence = null });
        var observed = DispatchFailureClassifier.Classify(Developer(), verification);
        Assert.Equal(TaskOutcomeClass.RealFailure, observed.OutcomeClass);
        Assert.Equal(baseline.ClassifierReceipt, observed.ClassifierReceipt);
    }

    [Fact]
    public void StaleBuildDiagnostic_WithWriteEvidence_PreservesHostFailure()
    {
        var task = Developer();
        var verification = MultipleVerification();
        Assert.Equal(TaskOutcomeClass.Environmental, DispatchFailureClassifier.Classify(task, verification).OutcomeClass);
        const string diagnostic = "receipt does not cover project src/Feature.csproj";
        var completedOutput = verification.StandardOutput.Replace(
            "assigned_scope_complete: false", "assigned_scope_complete: true", StringComparison.Ordinal);
        verification = verification with
        {
            ExitCode = 1,
            AssignedScopeComplete = true,
            StandardOutput = completedOutput,
            AuthoritativeStandardOutput = completedOutput,
            StandardError = DispatchFailureDiagnosticMarker.FormatWorkerBuildReceiptStale(
                false, [], ["src/Feature.csproj"]),
            FullStandardError = "",
            AuthoritativeStandardError = ""
        };

        var baseline = DispatchFailureClassifier.Classify(task, verification with { WriteIntegrityEvidence = null });
        var observed = DispatchFailureClassifier.Classify(task, verification);

        Assert.Equal("worker-build-receipt-stale", TaskOutcomeClassifier.TryExtractRule(observed.ClassifierReceipt));
        Assert.Equal(TaskOutcomeClass.UnknownEra, observed.OutcomeClass);
        Assert.Equal("Worker build receipt does not match the final tree. " + diagnostic, observed.EvidenceSummary);
        Assert.Equal(baseline.ClassifierReceipt, observed.ClassifierReceipt);
    }

    private static TaskSpec Developer()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Classify", [new TaskSpec(DeveloperId, "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, [new AgentDefinition(new AgentId("worker"), "Worker", AgentRole.Developer,
            new ModelProfile("OpenAI", "fixture-model", ModelCapability.Text, SubscriptionMode.ApiKey))]);
        kernel.RecordTaskDispatch(goal.Id, DeveloperId, new TaskDispatchRecord("worker", "worker", "C:\\repo", DispatchedAt,
            SandboxLowIntegrity: true, WorktreeHeadSha: "head", DirtyStateHash: "dirty"));
        return goal.Tasks.Single();
    }

    private static WorkerWriteIntegrityEvidence Evidence(string blocker, IReadOnlyList<WorkerWriteIntegrityObservation> observations) =>
        new(DeveloperId, DispatchedAt, StartedAt, "head", "dirty", blocker, observations,
            new(DeveloperId, DispatchedAt, StartedAt, "head", "dirty", 123, StartedAt.AddSeconds(1),
                "C:\\tools\\worker.exe", 4096, "fixture native token query: S-1-16-4096"));

    private static TaskVerificationRecord MultipleVerification()
    {
        var baseline = Verification(null);
        var blocker = AgentOutputDirectives.WorkerWriteDenialExample["blockers: ".Length..];
        var output = baseline.StandardOutput.Replace("exact-blocker - Cannot write src/Feature.cs; integrity mismatch claims Medium", blocker, StringComparison.Ordinal);
        return baseline with
        {
            StandardOutput = output, AuthoritativeStandardOutput = output,
            WriteIntegrityEvidence = Evidence(blocker,
                [new("src/Feature.cs", WorkerWriteIntegrityLabel.Medium, true) { OsDiagnostic = "Access is denied. (os error 5)" },
                 new("src/Other.cs", WorkerWriteIntegrityLabel.Medium, true) { OsDiagnostic = "Access is denied. (os error 5)" }])
        };
    }

    private static void AssertIncomplete(TaskVerificationRecord verification)
    {
        var outcome = DispatchFailureClassifier.Classify(Developer(), verification);
        Assert.Equal("incomplete-scope-declaration", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        Assert.Equal(TaskOutcomeClass.RealFailure, outcome.OutcomeClass);
        Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
    }

    private static TaskVerificationRecord Verification(WorkerWriteIntegrityObservation? observation) => new(
        "worker", "C:\\repo", 0, """
        WORKER_RESULT:
        files: none
        commands: none
        tests: deferred - conductor
        commit: none
        blockers: exact-blocker - Cannot write src/Feature.cs; integrity mismatch claims Medium
        assigned_scope_complete: false
        model_fit: fixture/model - adequate - classification
        skills: none
        confidence: high
        END_WORKER_RESULT
        """, "", DateTimeOffset.Parse("2026-11-07T12:00:00Z"), WorkerResultPresent: true, AssignedScopeComplete: false,
        DispatchAttemptAt: DispatchedAt, DispatchStartedAt: StartedAt, ChildProcessId: 123, EvidenceSource: VerificationEvidenceSource.Dispatch)
    {
        WriteIntegrityObservation = observation,
        WriteIntegrityEvidence = observation is null ? null : Evidence(
            "exact-blocker - Cannot write src/Feature.cs; integrity mismatch claims Medium", [observation])
    };
}
