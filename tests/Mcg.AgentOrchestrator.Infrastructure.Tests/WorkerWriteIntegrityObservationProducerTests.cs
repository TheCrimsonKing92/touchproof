using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;
using System.Diagnostics;
using Xunit;

// Every case owns its directory tree and fake labeler; no shared process or native label state.
public sealed class WorkerWriteIntegrityObservationProducerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void QueryResultControlsClassificationDespiteBlockerClaim(bool medium)
    {
        using var fixture = new Fixture();
        var labeler = new FakeLabeler(new(true, Low: !medium, Inheritable: false, Medium: medium));
        var output = Output("Cannot write `src/Feature.cs`; integrity mismatch claims Medium");
        Assert.True(WorkerWriteIntegrityObservationProducer.TryObserve(labeler, fixture.Worktree, output, out var observation));
        Assert.NotNull(observation);
        Assert.Equal(Path.Combine("src", "Feature.cs"), observation.WorktreeRelativePath);
        Assert.Equal(medium ? WorkerWriteIntegrityLabel.Medium : WorkerWriteIntegrityLabel.Low, observation.ObservedLabel);
        Assert.True(observation.LabelQuerySucceeded);
        Assert.Equal(Path.Combine(fixture.Worktree, "src", "Feature.cs"), Assert.Single(labeler.Queries));
        var verification = new TaskVerificationRecord("worker", fixture.Worktree, 0, output, "",
            DateTimeOffset.Parse("2026-11-07T12:00:00Z"), WorkerResultPresent: true, AssignedScopeComplete: false)
        { WriteIntegrityObservation = observation };
        var outcome = DispatchFailureClassifier.Classify(new(TaskId.New(), "Implement", AgentRole.Developer), verification);
        // Native labels alone never establish actual Low execution.
        Assert.Equal("incomplete-scope-declaration",
            TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        Assert.Equal(TaskOutcomeClass.RealFailure, outcome.OutcomeClass);
    }

    [Theory]
    [InlineData("CANNOT-WRITE src/Feature.cs")]
    [InlineData("access   denied 'src/Feature.cs'")]
    [InlineData("integrity-mismatch prevents editing \"src/Feature.cs\"")]
    [InlineData("cannot write src/../src/Feature.cs")]
    [InlineData("src/Feature.cs: access-denied")]
    public void EquivalentWordingSelectsOneTarget(string blocker)
    {
        using var fixture = new Fixture();
        var labeler = MediumLabeler();
        Assert.True(WorkerWriteIntegrityObservationProducer.TryObserve(labeler, fixture.Worktree, Output(blocker), out _));
        Assert.Single(labeler.Queries);
    }

    [Fact]
    public void QuotedAbsolutePathWithSpacesIsWorktreeRelative()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Worktree, "source folder", "Feature.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "source");
        var labeler = MediumLabeler();
        Assert.True(WorkerWriteIntegrityObservationProducer.TryObserve(labeler, fixture.Worktree,
            Output($"Cannot write '{path}'"), out var observation));
        Assert.Equal(Path.Combine("source folder", "Feature.cs"), observation!.WorktreeRelativePath);
        Assert.Equal(path, Assert.Single(labeler.Queries));
    }

    [Theory]
    [InlineData("../outside/Feature.cs")]
    [InlineData("sibling")]
    [InlineData("absolute")]
    public void OutsideTargetsAreRejectedBeforeQuery(string kind)
    {
        using var fixture = new Fixture();
        var path = kind switch
        {
            "sibling" => fixture.Worktree + "-sibling" + Path.DirectorySeparatorChar + "Feature.cs",
            "absolute" => Path.Combine(fixture.Root, "outside", "Feature.cs"),
            _ => kind
        };
        var labeler = MediumLabeler();
        Assert.False(WorkerWriteIntegrityObservationProducer.TryObserve(labeler, fixture.Worktree,
            Output($"Cannot write '{path}'"), out var observation));
        Assert.Null(observation);
        Assert.Empty(labeler.Queries);
    }

    [Theory]
    [InlineData("Cannot write src/Feature.cs or src/Other.cs", true)]
    [InlineData("Cannot write the target", true)]
    [InlineData("Cannot write the target. Details in logs/trace.txt", true)]
    [InlineData("Reading src/Feature.cs failed", true)]
    [InlineData("Cannot write src/Feature.cs", false)]
    public void MissingAmbiguousOrNonExactBlockerDoesNotQuery(string blocker, bool exact)
    {
        using var fixture = new Fixture();
        var labeler = MediumLabeler();
        Assert.False(WorkerWriteIntegrityObservationProducer.TryObserve(labeler, fixture.Worktree,
            Output(blocker, exact), out var observation));
        Assert.Null(observation);
        Assert.Empty(labeler.Queries);
    }

    [Theory]
    [InlineData("native-error")]
    [InlineData("missing")]
    [InlineData("throws")]
    [InlineData("contradictory")]
    [InlineData("unlabeled-medium")]
    [InlineData("unlabeled")]
    [InlineData("other")]
    public void UnusableNativeStateNeverEstablishesAboveLow(string kind)
    {
        using var fixture = new Fixture();
        var state = kind switch
        {
            "native-error" => new IntegrityLabelState(true, false, false, Medium: true, NativeQueryError: 5),
            "missing" => new IntegrityLabelState(false, false, false, Medium: true),
            "contradictory" => new IntegrityLabelState(true, true, false, Medium: true),
            "unlabeled-medium" => new IntegrityLabelState(true, false, false, Medium: true, Unlabeled: true),
            "unlabeled" => new IntegrityLabelState(true, false, false, Unlabeled: true),
            _ => new IntegrityLabelState(true, false, false)
        };
        var labeler = new FakeLabeler(state) { ThrowOnQuery = kind == "throws" };
        Assert.True(WorkerWriteIntegrityObservationProducer.TryObserve(labeler, fixture.Worktree,
            Output("Cannot write src/Feature.cs"), out var observation));
        Assert.NotNull(observation);
        Assert.False(observation.EstablishesAboveLow);
        if (kind is "native-error" or "missing" or "throws")
            Assert.False(observation.LabelQuerySucceeded);
        Assert.Single(labeler.Queries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinksOutsideWorktreeAreRejected(bool fileLink)
    {
        using var fixture = new Fixture();
        var outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "Feature.cs"), "external");
        var link = Path.Combine(fixture.Worktree, fileLink ? "Feature.cs" : "linked");
        try
        {
            if (fileLink) File.CreateSymbolicLink(link, Path.Combine(outside, "Feature.cs"));
            else Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Symbolic links unavailable: {ex.Message}");
        }
        var labeler = MediumLabeler();
        Assert.False(WorkerWriteIntegrityObservationProducer.TryObserve(labeler, fixture.Worktree,
            Output($"Cannot write '{(fileLink ? link : Path.Combine(link, "Feature.cs"))}'"), out var observation));
        Assert.Null(observation);
        Assert.Empty(labeler.Queries);
        Assert.False(WorkerWriteIntegrityObservationProducer.TryObserveAll(labeler, fixture.Worktree,
            Output(Collection("src/Other.cs", fileLink ? link : Path.Combine(link, "Feature.cs"))), out _, out _));
        Assert.Empty(labeler.Queries);
    }

    [Theory]
    [InlineData(AgentRole.Developer, true)]
    [InlineData(AgentRole.Reviewer, false)]
    public void CompletionCarriesObservationOnlyForDeveloper(AgentRole role, bool expected)
    {
        using var fixture = new Fixture();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Observe denied write", [new TaskSpec(TaskId.New(), "Work", role)]);
        kernel.ActivateGoal(goal.Id, [new AgentDefinition(new AgentId("worker"), "Worker", role,
            new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey))]);
        var task = goal.Tasks.Single();
        var started = DateTimeOffset.Parse("2026-11-07T12:00:00Z");
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "worker", fixture.Worktree, started));
        var stdout = Path.Combine(fixture.Root, "worker.out.log");
        var stderr = Path.Combine(fixture.Root, "worker.err.log");
        var exit = Path.Combine(fixture.Root, "worker.exit.txt");
        File.WriteAllText(stdout, Output("Cannot write src/Feature.cs"));
        File.WriteAllText(stderr, "");
        File.WriteAllText(exit, "1");
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(4242, "worker", fixture.Worktree,
            stdout, stderr, exit, started, null, null, OwnedProcessIds: [4242]));
        var labeler = MediumLabeler();
        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false, findBuildDaemons: _ => [])
        { WriteIntegrityLabeler = labeler };
        var result = runner.ReconcileLatestProcess(kernel, goal.Id, task.Id);
        Assert.NotNull(result.Verification);
        Assert.Equal(expected, result.Verification.WriteIntegrityObservation is not null);
        Assert.Equal(expected ? 1 : 0, labeler.Queries.Count);
        if (expected) Assert.Equal(WorkerWriteIntegrityLabel.Medium, result.Verification.WriteIntegrityObservation!.ObservedLabel);
        if (expected) Assert.Null(result.Verification.DispatchAttemptAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GuidanceReportRetainsEveryObservationAndRequiresHostLaunchProof(bool fileAccess)
    {
        using var fixture = new Fixture();
        string guidance;
        if (fileAccess)
        {
            var contextDirectory = Path.Combine(fixture.Root, "context");
            WorkerStandingRules.WriteContextArtifact(AgentRole.Developer, contextDirectory);
            Assert.DoesNotContain(AgentOutputDirectives.WorkerWriteDenialGuidance,
                AgentOutputDirectives.WorkerResultTemplateLinesForRole(AgentRole.Developer, contextDirectory));
            guidance = File.ReadAllText(Path.Combine(contextDirectory, WorkerStandingRules.ContextFileName));
        }
        else
        {
            guidance = string.Join(Environment.NewLine,
                AgentOutputDirectives.WorkerResultTemplateLinesForRole(AgentRole.Developer));
        }
        // Consume the example from the delivered instructions, including the file-access artifact.
        const string prefix = "blockers: exact-blocker - ";
        var exampleStart = guidance.IndexOf(prefix, StringComparison.Ordinal);
        Assert.True(exampleStart >= 0, "Delivered guidance must contain the canonical blocker example.");
        var exampleEnd = guidance.IndexOf("}]", exampleStart, StringComparison.Ordinal);
        Assert.True(exampleEnd > exampleStart, "The example must contain a complete target collection.");
        var output = Output(guidance[(exampleStart + prefix.Length)..(exampleEnd + 2)]);
        var (task, verification, launch) = CapturedDispatch(fixture.Worktree, output);
        var labeler = MediumLabeler();
        var enriched = WorkerWriteIntegrityObservationProducer.Enrich(task, labeler, verification, launch);
        var evidence = Assert.IsType<WorkerWriteIntegrityEvidence>(enriched.WriteIntegrityEvidence);
        Assert.Null(enriched.WriteIntegrityObservation);
        Assert.Equal(2, labeler.Queries.Count);
        Assert.Collection(evidence.Observations,
            first => Assert.Equal(Path.Combine("src", "Feature.cs"), first.WorktreeRelativePath),
            second => Assert.Equal(Path.Combine("src", "Other.cs"), second.WorktreeRelativePath));
        Assert.All(evidence.Observations, observation =>
        {
            Assert.True(observation.EstablishesAboveLow);
            Assert.Equal("Access is denied. (os error 5)", observation.OsDiagnostic);
            Assert.Contains("medium=True", observation.NativeDiagnostic);
        });
        Assert.Null(evidence.EvidenceGap);
        Assert.Equal(launch.Identity!.ProcessId, evidence.LowLaunch!.ProcessId);
        Assert.Equal(launch.Identity.StartedAt, evidence.LowLaunch.ProcessStartedAt);
        Assert.Equal(launch.Identity.ImagePath, evidence.LowLaunch.ImagePath);
        Assert.Equal(launch.Diagnostic, evidence.LowLaunch.NativeDiagnostic);
        var restored = JsonSerializer.Deserialize<TaskVerificationRecord>(JsonSerializer.Serialize(enriched))!;
        Assert.Equal(JsonSerializer.Serialize(enriched.WriteIntegrityEvidence), JsonSerializer.Serialize(restored.WriteIntegrityEvidence));
        var outcome = DispatchFailureClassifier.Classify(task, restored);
        Assert.Equal("worker-write-integrity-blocked", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        Assert.Equal(TaskOutcomeClass.Environmental, outcome.OutcomeClass);
        Assert.NotEqual(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
    }

    [Theory]
    [InlineData("Access is denied. (os error 5)\r\n")]
    [InlineData("EACCES\tpath=\"src/Feature.cs\"\nwrite failed\r\n")]
    [InlineData("Access denied \uD83D\uDE00")]
    public void EscapedOsDiagnosticsSurviveParsingAndSerialization(string diagnostic)
    {
        using var fixture = new Fixture();
        var report = "write-denials " + JsonSerializer.Serialize(new[]
        {
            new { kind = "filesystem-write-denied", path = "src/Feature.cs", osDiagnostic = diagnostic },
            new { kind = "filesystem-write-denied", path = "src/Other.cs", osDiagnostic = diagnostic }
        });
        var (task, verification, launch) = CapturedDispatch(fixture.Worktree, Output(report));
        var enriched = WorkerWriteIntegrityObservationProducer.Enrich(task, MediumLabeler(), verification, launch);
        var restored = JsonSerializer.Deserialize<TaskVerificationRecord>(JsonSerializer.Serialize(enriched))!;
        Assert.Equal(2, restored.GetWriteIntegrityObservations().Count);
        Assert.All(restored.GetWriteIntegrityObservations(), observation => Assert.Equal(diagnostic, observation.OsDiagnostic));
        Assert.Equal(TaskOutcomeClass.Environmental, DispatchFailureClassifier.Classify(task, restored).OutcomeClass);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("query-error")]
    [InlineData("not-low")]
    [InlineData("not-owned")]
    [InlineData("ownership-lost")]
    [InlineData("stale-attempt")]
    [InlineData("wrong-task")]
    [InlineData("wrong-head")]
    [InlineData("wrong-dirty")]
    [InlineData("wrong-child")]
    [InlineData("future-observation")]
    public void NativeLaunchGapsCannotProduceInfrastructureClassification(string kind)
    {
        using var fixture = new Fixture();
        using var child = Process.GetCurrentProcess();
        var (task, verification, validLaunch) = CapturedDispatch(fixture.Worktree,
            Output(Collection("src/Feature.cs", "src/Other.cs")));
        var ownershipQueries = 0;
        var tokenQueries = 0;
        var launch = kind is "query-error" or "not-low" or "not-owned" or "ownership-lost"
            ? WorkerWriteIntegrityObservationProducer.CaptureLaunch(child, LaunchParameters(task),
                _ => { ownershipQueries++; return kind != "not-owned" && (kind != "ownership-lost" || ownershipQueries == 1); },
                _ =>
                {
                    tokenQueries++;
                    if (kind == "query-error") throw new System.ComponentModel.Win32Exception(5, "native token query denied");
                    return kind != "not-low";
                })
            : kind switch
            {
                "absent" => null,
                "stale-attempt" => validLaunch with { DispatchAttemptAt = validLaunch.DispatchAttemptAt!.Value.AddSeconds(-1) },
                "wrong-task" => validLaunch with { TaskId = TaskId.New().Value },
                "wrong-head" => validLaunch with { WorktreeHeadSha = "another-head" },
                "wrong-dirty" => validLaunch with { DirtyStateHash = "another-dirty" },
                "wrong-child" => validLaunch with { Identity = validLaunch.Identity! with { ProcessId = int.MaxValue } },
                "future-observation" => validLaunch with { ObservedAt = verification.CompletedAt.AddSeconds(1) },
                _ => throw new InvalidOperationException(kind)
            };
        var enriched = WorkerWriteIntegrityObservationProducer.Enrich(task, MediumLabeler(), verification, launch);
        Assert.Null(enriched.WriteIntegrityEvidence!.LowLaunch);
        Assert.False(string.IsNullOrWhiteSpace(enriched.WriteIntegrityEvidence.EvidenceGap));
        if (kind == "query-error") Assert.Contains("native token query denied", enriched.WriteIntegrityEvidence.EvidenceGap);
        if (kind == "absent") Assert.Equal("Actual Low execution is unproven: host launch provenance does not record an observed execution integrity level.", enriched.WriteIntegrityEvidence.EvidenceGap);
        if (kind == "not-owned") Assert.Equal(0, tokenQueries);
        if (kind == "ownership-lost") Assert.Equal(1, tokenQueries);
        Assert.Equal(TaskOutcomeClass.RealFailure, DispatchFailureClassifier.Classify(task, enriched).OutcomeClass);
    }

    [Fact]
    public void CompletionConsumesCapturedLaunchFromChildExitArtifact()
    {
        using var fixture = new Fixture();
        using var child = Process.GetCurrentProcess();
        var started = new DateTimeOffset(child.StartTime.ToUniversalTime()).AddSeconds(-1);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Observe denied writes", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, [new AgentDefinition(new AgentId("worker"), "Worker", AgentRole.Developer,
            new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey))]);
        var task = goal.Tasks.Single();
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("worker", "worker", fixture.Worktree, started.AddSeconds(-1),
            SandboxLowIntegrity: true, WorktreeHeadSha: "head", DirtyStateHash: "dirty"));
        var launch = WorkerWriteIntegrityObservationProducer.CaptureLaunch(child, LaunchParameters(task), _ => true, _ => true);
        var stdout = Path.Combine(fixture.Root, "worker.out.log");
        var stderr = Path.Combine(fixture.Root, "worker.err.log");
        var exit = Path.Combine(fixture.Root, "worker.exit.txt");
        var childExit = Path.Combine(fixture.Root, "worker.child.json");
        File.WriteAllText(stdout, Output(Collection("src/Feature.cs", "src/Other.cs")));
        File.WriteAllText(stderr, "");
        File.WriteAllText(exit, "1");
        File.WriteAllText(childExit, JsonSerializer.Serialize(new DispatchProcessHost.DispatchChildExitRecord(
            777, 1, DateTimeOffset.UtcNow, WorkerIntegrity: launch with { CompletionChildProcessId = 777 }),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(4242, "worker", fixture.Worktree,
            stdout, stderr, exit, started, null, null, ChildExitRecordPath: childExit));
        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false, findBuildDaemons: _ => [])
        { WriteIntegrityLabeler = MediumLabeler() };
        var result = runner.ReconcileLatestProcess(kernel, goal.Id, task.Id);
        var verification = Assert.IsType<TaskVerificationRecord>(result.Verification);
        Assert.Null(verification.DispatchAttemptAt);
        Assert.Equal(777, verification.ChildProcessId);
        Assert.Equal(task.LastDispatch!.DispatchedAt, verification.WriteIntegrityEvidence!.DispatchedAt);
        Assert.Equal(launch.Identity!.ProcessId, verification.WriteIntegrityEvidence!.LowLaunch!.ProcessId);
        Assert.Equal(777, verification.WriteIntegrityEvidence.LowLaunch.CompletionChildProcessId);
        Assert.Equal(2, verification.GetWriteIntegrityObservations().Count);
        Assert.Equal(TaskOutcomeClass.Environmental, DispatchFailureClassifier.Classify(task, verification).OutcomeClass);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WindowsOwnedWorkerNativeTokenControl(bool low)
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Native process integrity controls require Windows.");
        using var parent = Process.GetCurrentProcess();
        if (!low && WorkerBuildProcessIntegrity.IsLow(parent))
            Assert.Skip("A non-Low native control requires a non-Low parent; this test host has a native-confirmed Low token.");
        using var fixture = new Fixture();
        var dispatched = DateTimeOffset.UtcNow;
        var info = new ProcessStartInfo(WorkerShell.Executable)
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = fixture.Worktree };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "while ($true) { [Threading.Thread]::Sleep(1000) }" })
            info.ArgumentList.Add(argument);
        using var launch = OwnedProcessGroup.StartSuspendedContained(info);
        try
        {
            if (low) launch.LowerIntegrityBeforeResume(); // Only this newly created test control is changed.
            launch.Resume();
            var (task, verification) = Dispatch(fixture.Worktree, Output(Collection("src/Feature.cs", "src/Other.cs")),
                dispatched.AddSeconds(-1), launch.Process.Id);
            var native = WorkerWriteIntegrityObservationProducer.CaptureLaunch(launch.Process, LaunchParameters(task),
                process => launch.Group.Contains(process.SafeHandle));
            Assert.Equal(low, native.Low);
            Assert.Equal(launch.Process.Id, native.Identity!.ProcessId);
            var enriched = WorkerWriteIntegrityObservationProducer.Enrich(task, MediumLabeler(),
                verification with { CompletedAt = DateTimeOffset.UtcNow }, native);
            Assert.Equal(low ? TaskOutcomeClass.Environmental : TaskOutcomeClass.RealFailure,
                DispatchFailureClassifier.Classify(task, enriched).OutcomeClass);
        }
        finally
        {
            // Confirm termination before disposing the process handle and its working directory.
            launch.Group.TerminateAndConfirmEmpty(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("[{\"kind\":\"filesystem-write-denied\",\"path\":\"Feature.cs\",\"osDiagnostic\":\"Access denied\"}]")]
    [InlineData("[{\"kind\":\"filesystem-write-denied\",\"path\":\"src/Feature.cs\",\"osDiagnostic\":\"\"}]")]
    [InlineData("[{\"kind\":\"filesystem-write-denied\",\"path\":\"src/Feature.cs\"}]")]
    [InlineData("[{\"kind\":\"mentioned\",\"path\":\"src/Feature.cs\",\"osDiagnostic\":\"Access denied\"}]")]
    [InlineData("[{\"kind\":\"filesystem-write-denied\",\"path\":\"src/Feature.cs\",\"osDiagnostic\":\"Access denied\",\"label\":\"Medium\"}]")]
    [InlineData("[{\"kind\":\"filesystem-write-denied\",\"path\":\"src/Feature.cs\",\"path\":\"src/Other.cs\",\"osDiagnostic\":\"Access denied\"}]")]
    [InlineData("[{\"kind\":\"filesystem-write-denied\",\"path\":null,\"osDiagnostic\":\"Access denied\"}]")]
    [InlineData("[{\"kind\":\"filesystem-write-denied\",\"path\":\"src/Feature.cs\",\"osDiagnostic\":\"Access denied\"}] or src/Other.cs")]
    public void MalformedCollectionsNeverQuery(string json)
    {
        using var fixture = new Fixture();
        var labeler = MediumLabeler();
        Assert.False(WorkerWriteIntegrityObservationProducer.TryObserveAll(labeler, fixture.Worktree,
            Output("write-denials " + json), out var observations, out _));
        Assert.Empty(observations);
        Assert.Empty(labeler.Queries);
    }

    [Theory]
    [InlineData("osDiagnostic", "\\uD800")]
    [InlineData("osDiagnostic", "\\uDC00")]
    [InlineData("path", "\\uD800")]
    [InlineData("path", "\\uDC00")]
    [InlineData("property-name", "\\uD800")]
    [InlineData("property-name", "\\uDC00")]
    [InlineData("kind", "\\uD800")]
    [InlineData("kind", "\\uDC00")]
    public void MalformedUnicodeCollectionsRemainUnprovenWithoutQuerying(string field, string invalidEscape)
    {
        using var fixture = new Fixture();
        var kind = field == "kind" ? invalidEscape : "filesystem-write-denied";
        var path = field == "path" ? "src/" + invalidEscape + ".cs" : "src/Other.cs";
        var diagnosticName = field == "property-name" ? invalidEscape : "osDiagnostic";
        var diagnostic = field == "osDiagnostic" ? "Access denied " + invalidEscape : "Access denied";
        // Keep the escapes raw: serializing malformed UTF-16 would replace it before reaching the parser.
        // A valid first entry must not be queried when a later entry cannot be decoded.
        var report = Collection("src/Feature.cs")[..^1] + "," +
            $$"""{"kind":"{{kind}}","path":"{{path}}","{{diagnosticName}}":"{{diagnostic}}"}]""";
        var output = Output(report);
        var labeler = MediumLabeler();
        Assert.False(WorkerWriteIntegrityObservationProducer.TryObserveAll(labeler, fixture.Worktree,
            output, out var observations, out var blocker));
        Assert.Empty(observations);
        Assert.Equal("exact-blocker - " + report, blocker);

        var (task, verification, launch) = CapturedDispatch(fixture.Worktree, output);
        var enriched = WorkerWriteIntegrityObservationProducer.Enrich(task, labeler, verification, launch);
        Assert.Same(verification, enriched);
        Assert.Empty(labeler.Queries);
        Assert.Equal(TaskOutcomeClass.RealFailure, DispatchFailureClassifier.Classify(task, enriched).OutcomeClass);
    }

    [Theory]
    [InlineData("src/../src/Feature.cs")]
    [InlineData("../outside/Other.cs")]
    [InlineData(".git/config")]
    [InlineData("src/../.git")]
    [InlineData("src/Feature.cs:stream")]
    public void CompleteSetIsValidatedBeforeAnyQuery(string second)
    {
        using var fixture = new Fixture();
        var labeler = MediumLabeler();
        Assert.False(WorkerWriteIntegrityObservationProducer.TryObserveAll(labeler, fixture.Worktree,
            Output(Collection("src/Feature.cs", second)), out var observations, out _));
        Assert.Empty(observations);
        Assert.Empty(labeler.Queries);
    }

    [Theory]
    [InlineData("low")]
    [InlineData("missing")]
    [InlineData("native-error")]
    [InlineData("contradictory")]
    [InlineData("unlabeled")]
    [InlineData("unknown")]
    public void MixedNativeStatesRemainUnprovenWithDiagnostics(string kind)
    {
        using var fixture = new Fixture();
        var state = kind switch
        {
            "low" => new IntegrityLabelState(true, true, false),
            "missing" => new IntegrityLabelState(false, false, false, Medium: true),
            "native-error" => new IntegrityLabelState(true, false, false, Medium: true, NativeQueryError: 5),
            "contradictory" => new IntegrityLabelState(true, true, false, Medium: true),
            "unlabeled" => new IntegrityLabelState(true, false, false, Unlabeled: true),
            _ => new IntegrityLabelState(true, false, false)
        };
        var labeler = MediumLabeler();
        labeler.States[Path.Combine(fixture.Worktree, "src", "Other.cs")] = state;
        var (task, verification) = Dispatch(fixture.Worktree, Output(Collection("src/Feature.cs", "src/Other.cs")));
        var enriched = WithHostProof(task, WorkerWriteIntegrityObservationProducer.Enrich(task, labeler, verification));
        var observations = enriched.GetWriteIntegrityObservations();
        Assert.Equal(2, labeler.Queries.Count);
        Assert.Equal(2, observations.Count);
        Assert.True(observations[0].EstablishesAboveLow);
        Assert.False(observations[1].EstablishesAboveLow);
        Assert.Equal(state.NativeQueryError, observations[1].NativeQueryError);
        Assert.Contains($"exists={state.Exists}", observations[1].NativeDiagnostic);
        Assert.Equal("incomplete-scope-declaration", TaskOutcomeClassifier.TryExtractRule(
            DispatchFailureClassifier.Classify(task, enriched).ClassifierReceipt));
    }

    [Theory]
    [InlineData("targets")]
    [InlineData("path")]
    [InlineData("diagnostic")]
    [InlineData("report")]
    [InlineData("output")]
    public void BudgetsRejectWholeReportWithoutQueries(string kind)
    {
        using var fixture = new Fixture();
        var report = kind switch
        {
            "targets" => Collection(Enumerable.Range(0, 9).Select(index => $"src/Feature{index}.cs").ToArray()),
            "path" => Collection("src/" + new string('a', 1025)),
            "diagnostic" => "write-denials " + JsonSerializer.Serialize(new[] { new { kind = "filesystem-write-denied", path = "src/Feature.cs", osDiagnostic = new string('x', 2049) } }),
            "report" => new string('x', WorkerWriteIntegrityObservationProducer.MaxReportCharacters),
            _ => Collection("src/Feature.cs")
        };
        var output = Output(report);
        if (kind == "output") output = new string('x', WorkerWriteIntegrityObservationProducer.MaxOutputCharacters) + output;
        var labeler = MediumLabeler();
        Assert.False(WorkerWriteIntegrityObservationProducer.TryObserveAll(labeler, fixture.Worktree, output, out var observations, out _));
        Assert.Empty(observations);
        Assert.Empty(labeler.Queries);
    }

    [Fact]
    public void EightTargetsGetExactlyOneQueryEach()
    {
        using var fixture = new Fixture();
        var labeler = MediumLabeler();
        Assert.True(WorkerWriteIntegrityObservationProducer.TryObserveAll(labeler, fixture.Worktree,
            Output(Collection(Enumerable.Range(0, 8).Select(index => $"src/Feature{index}.cs").ToArray())), out var observations, out _));
        Assert.Equal(8, observations.Count);
        Assert.Equal(8, labeler.Queries.Distinct().Count());
    }

    [Fact]
    public void ExactPathsNormalizeAndRetainVaryingOsDiagnostics()
    {
        using var fixture = new Fixture();
        var diagnostic = "EACCES errno=13: /unrelated/log.txt";
        var absolute = Path.Combine(fixture.Worktree, "src", "Other.cs");
        var json = JsonSerializer.Serialize(new[]
        {
            new { kind = "filesystem-write-denied", path = "src/../src/Feature.cs", osDiagnostic = diagnostic },
            new { kind = "filesystem-write-denied", path = absolute, osDiagnostic = diagnostic }
        });
        var labeler = MediumLabeler();
        Assert.True(WorkerWriteIntegrityObservationProducer.TryObserveAll(labeler, fixture.Worktree,
            Output("write-denials " + json), out var observations, out _));
        Assert.Equal(2, labeler.Queries.Count);
        Assert.Equal(Path.Combine(fixture.Worktree, "src", "Feature.cs"), labeler.Queries[0]);
        Assert.Equal(absolute, labeler.Queries[1]);
        Assert.Equal("src/../src/Feature.cs", observations[0].ReportedPath);
        Assert.All(observations, observation => Assert.Equal(diagnostic, observation.OsDiagnostic));
    }

    [Fact]
    public void LegacyBareFilenameStillSelectsOnlyThatRootTarget()
    {
        using var fixture = new Fixture();
        var labeler = MediumLabeler();
        Assert.True(WorkerWriteIntegrityObservationProducer.TryObserve(labeler, fixture.Worktree,
            Output("Cannot write Feature.cs"), out var observation));
        Assert.Equal("Feature.cs", observation!.WorktreeRelativePath);
        Assert.Equal(Path.Combine(fixture.Worktree, "Feature.cs"), Assert.Single(labeler.Queries));
    }

    [Fact]
    public void QueryExceptionsRetainRawDiagnosticForEveryTarget()
    {
        using var fixture = new Fixture();
        var labeler = new FakeLabeler(new(true, false, false)) { ThrowOnQuery = true };
        Assert.True(WorkerWriteIntegrityObservationProducer.TryObserveAll(labeler, fixture.Worktree,
            Output(Collection("src/Feature.cs", "src/Other.cs")), out var observations, out _));
        Assert.Equal(2, labeler.Queries.Count);
        Assert.All(observations, observation =>
        {
            Assert.False(observation.EstablishesAboveLow);
            Assert.Equal("Query denied", observation.NativeDiagnostic);
            Assert.Equal("Access is denied. (os error 5)", observation.OsDiagnostic);
        });
    }

    private static string Collection(params string[] paths) => "write-denials " + JsonSerializer.Serialize(
        paths.Select(path => new { kind = "filesystem-write-denied", path, osDiagnostic = "Access is denied. (os error 5)" }));

    private static (TaskSpec Task, TaskVerificationRecord Verification) Dispatch(string worktree, string output,
        DateTimeOffset? dispatchedAt = null, int childProcessId = 4242)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Observe", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, [new AgentDefinition(new AgentId("worker"), "Worker", AgentRole.Developer,
            new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey))]);
        var task = goal.Tasks.Single();
        var dispatched = dispatchedAt ?? DateTimeOffset.Parse("2026-11-07T12:00:00Z");
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("worker", "worker", worktree, dispatched,
            SandboxLowIntegrity: true, WorktreeHeadSha: "head", DirtyStateHash: "dirty"));
        return (task, new("worker", worktree, 0, output, "", dispatched.AddMinutes(1),
            WorkerResultPresent: true, AssignedScopeComplete: false, FullStandardOutput: output,
            DispatchAttemptAt: dispatched, DispatchStartedAt: dispatched.AddSeconds(1), ChildProcessId: childProcessId, EvidenceSource: VerificationEvidenceSource.Dispatch));
    }

    private static DispatchProcessHost.DispatchRunParameters LaunchParameters(TaskSpec task) =>
        new("worker", task.LastDispatch!.WorkingDirectory, "unused.out", "unused.err", "unused.exit", null, false,
            WorktreeHeadSha: task.LastDispatch.WorktreeHeadSha, DirtyStateHash: task.LastDispatch.DirtyStateHash,
            WorkerTaskId: task.Id.Value, DispatchAttemptAt: task.LastDispatch.DispatchedAt);

    private static (TaskSpec Task, TaskVerificationRecord Verification, DispatchProcessHost.WorkerExecutionIntegrityObservation Launch)
        CapturedDispatch(string worktree, string output)
    {
        using var child = Process.GetCurrentProcess();
        var (task, verification) = Dispatch(worktree, output, new DateTimeOffset(child.StartTime.ToUniversalTime()).AddSeconds(-2), child.Id);
        // Fake only the native Low result and job membership. Capture and producer wiring are real;
        // supported Windows controls remain Acceptance-owned.
        var launch = WorkerWriteIntegrityObservationProducer.CaptureLaunch(child, LaunchParameters(task), _ => true, _ => true);
        Assert.True(launch.Low);
        Assert.NotNull(launch.Identity);
        return (task, verification with { CompletedAt = DateTimeOffset.UtcNow }, launch);
    }

    private static TaskVerificationRecord WithHostProof(TaskSpec task, TaskVerificationRecord verification)
    {
        var evidence = verification.WriteIntegrityEvidence!;
        return verification with
        {
            WriteIntegrityEvidence = evidence with
            {
                LowLaunch = new(task.Id, evidence.DispatchedAt, evidence.DispatchStartedAt, evidence.WorktreeHeadSha!, evidence.DirtyStateHash!,
                    4242, evidence.DispatchStartedAt.AddSeconds(1), "C:\\tools\\worker.exe", 4096, "fixture native token query: S-1-16-4096"),
                EvidenceGap = null
            }
        };
    }

    private static FakeLabeler MediumLabeler() => new(new(true, false, false, Medium: true));

    private static string Output(string blocker, bool exact = true) => $$"""
        WORKER_RESULT:
        files: none
        commands: none
        tests: deferred - conductor
        commit: none
        blockers: {{(exact ? "exact-blocker - " : "")}}{{blocker}}
        assigned_scope_complete: false
        model_fit: fixture/model - adequate - producer
        skills: none
        confidence: high
        END_WORKER_RESULT
        """;

    private sealed class FakeLabeler(IntegrityLabelState state) : IWorkerIntegrityLabeler
    {
        public List<string> Queries { get; } = [];
        public Dictionary<string, IntegrityLabelState> States { get; } = [];
        public bool ThrowOnQuery { get; init; }
        public IntegrityLabelState Query(string path)
        {
            Queries.Add(path);
            if (ThrowOnQuery) throw new UnauthorizedAccessException("Query denied");
            return States.GetValueOrDefault(path, state);
        }
        public bool SetIntegrity(string path, string level, bool recursive) =>
            throw new InvalidOperationException("Observation must never change a label.");
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"mcg-write-observation-{Guid.NewGuid():N}");
        public string Worktree => Path.Combine(Root, "worktree");
        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Worktree, "src"));
            File.WriteAllText(Path.Combine(Worktree, "src", "Feature.cs"), "source");
            File.WriteAllText(Path.Combine(Worktree, "src", "Other.cs"), "source");
        }
        public void Dispose()
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.Delete(Root, recursive: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Temp cleanup must not replace the test's assertion result with a failure.
                }

                if (attempt < 4) Thread.Sleep(500);
            }
        }
    }
}
