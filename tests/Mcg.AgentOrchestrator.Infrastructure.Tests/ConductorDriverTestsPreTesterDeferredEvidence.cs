using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsPreTesterDeferredEvidence
{
    private const string CandidateSha = "bbb2222";
    private const string BaseSha = "aaa1111";
    private const string GuardPath = "src/X/Y.cs";
    private static readonly string[] SelectionSteps =
    [
        "walk-pretester-declared-resolve", "walk-pretester-changed-file-guards", "walk-pretester-cli-surface",
        "walk-pretester-coupled-class", "walk-pretester-reverse-dependency", "walk-pretester-always-run",
        "walk-pretester-test-source"
    ];

    [Fact]
    public void TruncatedStartedEntryIsAbandonedAndEvidenceIsRequestedAgain()
    {
        using var scenario = new Scenario("deferred - DeferredAlphaTests", BaseSha, ["DeferredAlphaTests"]);
        const string receipt = "truncated-started-receipt";
        var selections = Enumerable.Range(0, 600)
            .Select(index => $"Infrastructure.Tests:RecordedSelection{index:D4}Tests").ToArray();
        var message = PreTesterEvidenceIndexLines.FormatMarker(
            new("started", CandidateSha, receipt, selections, [], null, []));
        Assert.True(message.Length > TimelineMessageBound.CharacterLimit);
        var truncated = TimelineMessageBound.Bound(message, scenario.Goal.Id.Value, scenario.Tester.Id.Value);
        Assert.Contains("[omitted", truncated, StringComparison.Ordinal);
        scenario.Kernel.RecordFindingEvidenceRequest(scenario.Goal.Id, scenario.Tester.Id, truncated);
        Assert.Equal("started", PreTesterEvidenceIndexLines.Latest(scenario.Goal, scenario.Tester.Id, CandidateSha)?.Outcome);
        var requests = new List<string>();
        var roles = new List<AgentRole>();
        var driver = scenario.Driver(request =>
        {
            requests.Add(request);
            return ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
                scenario.Root, request, CandidateSha);
        }, roles.Add);

        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);
            Assert.Equal("green", PreTesterEvidenceIndexLines.Latest(scenario.Goal, scenario.Tester.Id, CandidateSha)?.Outcome);
            driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);
        });

        var abandoned = Assert.Single(scenario.Goal.Timeline.Where(evt =>
            evt.TaskId == scenario.Tester.Id && evt.Kind == ProgressKind.FindingEvidenceRunRecorded &&
            PreTesterEvidenceIndexLines.Parse(evt.Message) is { Outcome: "abandoned", ReceiptId: receipt }));
        Assert.EndsWith("; abandoned_reason=truncated-stored-message", abandoned.Message, StringComparison.Ordinal);
        var entry = Assert.IsType<PreTesterEvidenceEntry>(PreTesterEvidenceIndexLines.Parse(abandoned.Message));
        Assert.Equal(CandidateSha, entry.CandidateSha);
        Assert.Null(entry.ResultPath);
        Assert.Empty(entry.FailingTests);
        var request = Assert.Single(requests);
        Assert.Equal("Infrastructure.Tests:DeferredAlphaTests", request);
        Assert.DoesNotContain("[omitted", request, StringComparison.Ordinal);
        Assert.Equal("green", PreTesterEvidenceIndexLines.Latest(scenario.Goal, scenario.Tester.Id, CandidateSha)?.Outcome);
        Assert.Contains(AgentRole.Tester, roles);
        Assert.Contains(scenario.Goal.Timeline, evt => evt.Message == truncated);
        var notice = Assert.Single(output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("PRE_TESTER_STARTED_ENTRY_TRUNCATED ", StringComparison.Ordinal)));
        Assert.Contains("receipt=" + receipt, notice, StringComparison.Ordinal);
    }

    [Fact]
    public void InterruptedTruncationRetriesEvidenceAndOtherAbandonmentsStayFinished()
    {
        using (var scenario = new Scenario("deferred - DeferredAlphaTests", BaseSha, ["DeferredAlphaTests"]))
        {
            const string receipt = "interrupted-truncation-receipt";
            var selections = Enumerable.Range(0, 600)
                .Select(index => $"Infrastructure.Tests:RecordedSelection{index:D4}Tests").ToArray();
            var truncated = TimelineMessageBound.Bound(PreTesterEvidenceIndexLines.FormatMarker(
                new("started", CandidateSha, receipt, selections, [], null, [])), scenario.Goal.Id.Value, scenario.Tester.Id.Value);
            Assert.Contains("[omitted", truncated, StringComparison.Ordinal);
            scenario.Kernel.RecordFindingEvidenceRequest(scenario.Goal.Id, scenario.Tester.Id, truncated);
            var started = Assert.IsType<PreTesterEvidenceEntry>(PreTesterEvidenceIndexLines.Parse(truncated));
            // Pin the suffix written before this change, independently of the current abandonment writer.
            scenario.Kernel.RecordFindingEvidenceRun(scenario.Goal.Id, scenario.Tester.Id,
                PreTesterEvidenceIndexLines.FormatMarker(started with { Outcome = "abandoned" }) +
                "; abandoned_reason=truncated-stored-message");
            Assert.Equal("abandoned", PreTesterEvidenceIndexLines.Latest(scenario.Goal, scenario.Tester.Id, CandidateSha)?.Outcome);
            var before = scenario.Goal.Timeline.Count;
            var requests = new List<string>();
            var roles = new List<AgentRole>();
            var notes = new List<string>();
            var driver = scenario.Driver(request =>
            {
                // Recovery must record its replacement even when this attempt completes inline.
                Assert.Equal("started", PreTesterEvidenceIndexLines.Latest(scenario.Goal, scenario.Tester.Id, CandidateSha)?.Outcome);
                requests.Add(request);
                return ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
                    scenario.Root, request, CandidateSha);
            }, roles.Add, notes: notes.Add);

            driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

            Assert.Equal("Infrastructure.Tests:DeferredAlphaTests", Assert.Single(requests));
            var replacement = Assert.Single(scenario.Goal.Timeline.Skip(before).Where(evt =>
                evt.TaskId == scenario.Tester.Id && evt.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
                PreTesterEvidenceIndexLines.Parse(evt.Message)?.Outcome == "started"));
            Assert.Equal(["Infrastructure.Tests:DeferredAlphaTests"], PreTesterEvidenceIndexLines.Parse(replacement.Message)!.Selections);
            Assert.DoesNotContain("[omitted", replacement.Message, StringComparison.Ordinal);
            Assert.Single(scenario.Goal.Timeline.Where(evt => evt.TaskId == scenario.Tester.Id &&
                PreTesterEvidenceIndexLines.Parse(evt.Message)?.Outcome == "abandoned"));
            Assert.DoesNotContain(notes, note => note.Contains("exit=prior-outcome-not-started", StringComparison.Ordinal));
            Assert.Equal("green", PreTesterEvidenceIndexLines.Latest(scenario.Goal, scenario.Tester.Id, CandidateSha)?.Outcome);
            Assert.Equal([AgentRole.Tester], roles);
        }

        foreach (var reason in new[] { "malformed-selection-name", "truncated-stored-message-extra", "Truncated-stored-message" })
        {
            using var scenario = new Scenario("deferred - DeferredAlphaTests", BaseSha, ["DeferredAlphaTests"]);
            scenario.Kernel.RecordFindingEvidenceRun(scenario.Goal.Id, scenario.Tester.Id,
                PreTesterEvidenceIndexLines.FormatMarker(new("abandoned", CandidateSha, "other-abandonment",
                    ["Infrastructure.Tests:DeferredAlphaTests"], [], null, [])) + "; abandoned_reason=" + reason);
            var before = scenario.Goal.Timeline.Count;
            var requests = new List<string>();
            var roles = new List<AgentRole>();
            var notes = new List<string>();
            var driver = scenario.Driver(request =>
            {
                requests.Add(request);
                return ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
                    scenario.Root, request, CandidateSha);
            }, roles.Add, notes: notes.Add);

            driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

            Assert.Empty(requests);
            Assert.DoesNotContain(scenario.Goal.Timeline.Skip(before), evt =>
                PreTesterEvidenceIndexLines.Parse(evt.Message)?.Outcome == "started");
            Assert.Contains(notes, note => note.Contains("exit=prior-outcome-not-started prior=abandoned", StringComparison.Ordinal));
            Assert.Equal("abandoned", PreTesterEvidenceIndexLines.Latest(scenario.Goal, scenario.Tester.Id, CandidateSha)?.Outcome);
            Assert.Equal([AgentRole.Tester], roles);
        }
    }

    [Fact]
    public void StartedEntryWithColonlessNameIsAbandonedAndDeclaredClassesRun()
    {
        using var scenario = new Scenario("deferred - DeferredAlphaTests, DeferredBetaTests", BaseSha,
            ["DeferredAlphaTests", "DeferredBetaTests"]);
        const string receipt = "malformed-selection-receipt";
        var declared = new[] { "Infrastructure.Tests:DeferredAlphaTests", "Infrastructure.Tests:DeferredBetaTests" };
        scenario.Kernel.RecordFindingEvidenceRequest(scenario.Goal.Id, scenario.Tester.Id,
            PreTesterEvidenceIndexLines.FormatMarker(new("started", CandidateSha, receipt,
                [declared[0], "ColonlessSelection", declared[1]], [], null, [])));
        var before = scenario.Goal.Timeline.Count;
        var requests = new List<string>();
        var roles = new List<AgentRole>();
        var driver = scenario.Driver(request =>
        {
            requests.Add(request);
            return ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
                scenario.Root, request, CandidateSha);
        }, roles.Add);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(declared, Assert.Single(requests).Split("; ", StringSplitOptions.RemoveEmptyEntries));
        var records = scenario.Goal.Timeline.Skip(before).Where(evt =>
            evt.TaskId == scenario.Tester.Id && PreTesterEvidenceIndexLines.Parse(evt.Message) is not null).ToArray();
        Assert.Equal(["abandoned", "started", "green"], records.Select(evt => PreTesterEvidenceIndexLines.Parse(evt.Message)!.Outcome));
        Assert.Equal(receipt, PreTesterEvidenceIndexLines.Parse(records[0].Message)!.ReceiptId);
        Assert.EndsWith("; abandoned_reason=malformed-selection-name", records[0].Message, StringComparison.Ordinal);
        Assert.Equal(declared, PreTesterEvidenceIndexLines.Parse(records[1].Message)!.Selections);
        Assert.Equal(declared, PreTesterEvidenceIndexLines.Parse(records[2].Message)!.Selections);
        Assert.Equal([AgentRole.Tester], roles);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void StartedBackgroundAttemptHoldsTesterAcrossTicks()
    {
        using var scenario = new Scenario("deferred - DeferredAlphaTests", BaseSha, ["DeferredAlphaTests"]);
        scenario.AddObservedGuard();
        var roles = new List<AgentRole>();
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(scenario.Root, "background-attempts"), Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
            isProcessAlive: processId => processId == 7103,
            launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7103),
            acquireStableSlotLease: (_, _) => null);
        var driver = scenario.Driver(
            _ => throw new Xunit.Sdk.XunitException("Background runner must not run inline."),
            roles.Add, coordinator: coordinator, changedPaths: [GuardPath]);
        var mainReads = 0;
        driver.PreTesterMainRevision = _ => { mainReads++; return "fixture-main-revision"; };

        var first = MeasureAdvance(driver, scenario.Goal);
        Assert.Equal(SelectionSteps, first.Parts);
        Assert.Contains("PRE_TESTER_GUARD_SELECTED class=ObservedSourceTests", first.Console, StringComparison.Ordinal);
        Assert.Equal(1, mainReads);
        var attempt = Assert.IsType<ConductorParallelAcceptanceAttempt>(coordinator.TryReadLatest(scenario.Goal.Id.Value));
        var prior = Assert.IsType<PreTesterEvidenceEntry>(PreTesterEvidenceIndexLines.Latest(scenario.Goal, scenario.Tester.Id, CandidateSha));
        Assert.Equal(attempt.FocusedEvidenceReceiptId, prior.ReceiptId);
        Assert.Equal("started", prior.Outcome);

        var second = MeasureAdvance(driver, scenario.Goal);

        Assert.Empty(second.Parts);
        Assert.DoesNotContain("PRE_TESTER_GUARD_SELECTED", second.Console, StringComparison.Ordinal);
        Assert.Equal(1, mainReads);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(second.Result.Outcome);
        Assert.Equal($"Background pre-tester-deferred focused evidence is running in attempt {attempt.AttemptId}.", held.Reason);
        Assert.Equal(ConductorHoldOwner.BackgroundAttempt, held.Owner);

        Assert.Empty(roles);
        Assert.Single(scenario.Goal.Timeline.Where(evt =>
            evt.TaskId == scenario.Tester.Id &&
            evt.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
            evt.Message.StartsWith("finding-evidence pre-tester outcome=started;", StringComparison.Ordinal)));
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    [InlineData("result-artifact")]
    [InlineData("reconciled")]
    [InlineData("build-slot-blocked")]
    [InlineData("different-candidate")]
    [InlineData("no-prior-entry")]
    [InlineData("different-receipt")]
    public void NonLiveRecordedAttempt_ComputesSelectionAndKeepsOutcome(string state)
    {
        using var scenario = new Scenario("deferred - DeferredAlphaTests", BaseSha, ["DeferredAlphaTests"]);
        scenario.AddObservedGuard();
        var roles = new List<AgentRole>();
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(scenario.Root, "background-attempts"), TrunkBranchName.Default,
            runInline: state == "build-slot-blocked",
            isProcessAlive: processId => processId == 7103,
            launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7103),
            acquireStableSlotLease: (_, _) => state == "build-slot-blocked"
                ? throw new DotnetBuildSlotsBusyException(new DotnetBuildLeaseAcquisition.SlotsBusy("pre-tester-evidence", []))
                : null);
        ConductorDriver Driver(string sha = CandidateSha) => scenario.Driver(
            _ => throw new Xunit.Sdk.XunitException("Background runner must not run inline."),
            roles.Add, coordinator: coordinator, changedPaths: [GuardPath], candidateSha: sha);
        var driver = Driver();
        var candidateSha = CandidateSha;
        if (state != "no-prior-entry")
        {
            var first = MeasureAdvance(driver, scenario.Goal);
            Assert.Equal(SelectionSteps, first.Parts);
            Assert.IsType<ConductorAdvanceOutcome.Held>(first.Result.Outcome);
            var attempt = Assert.IsType<ConductorParallelAcceptanceAttempt>(coordinator.TryReadLatest(scenario.Goal.Id.Value));
            if (state == "result-artifact")
                CompleteAttempt(scenario, coordinator, attempt);
            else if (state == "reconciled")
            {
                coordinator.MarkReconciled(attempt);
                Assert.NotNull(coordinator.TryReadLatest(scenario.Goal.Id.Value)!.ReconciledAt);
            }
            else if (state == "build-slot-blocked")
                Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot, attempt.Outcome);
            else if (state == "different-candidate")
            {
                candidateSha = "ccc3333";
                Assert.NotEqual(candidateSha, attempt.BranchHeadSha);
                driver = Driver(candidateSha);
            }

            var prior = PreTesterEvidenceIndexLines.Latest(scenario.Goal, scenario.Tester.Id, CandidateSha);
            if (state is "build-slot-blocked" or "different-candidate" or "different-receipt")
                scenario.Kernel.RecordFindingEvidenceRequest(scenario.Goal.Id, scenario.Tester.Id,
                    PreTesterEvidenceIndexLines.FormatMarker(new("started", candidateSha,
                        state == "different-receipt" ? "unrelated-receipt" : attempt.FocusedEvidenceReceiptId!,
                        prior?.Selections ?? attempt.FocusedEvidenceRequest!.Split("; ", StringSplitOptions.RemoveEmptyEntries), [], null, [])));
            var started = Assert.IsType<PreTesterEvidenceEntry>(PreTesterEvidenceIndexLines.Latest(scenario.Goal, scenario.Tester.Id, candidateSha));
            Assert.Equal("started", started.Outcome);
            Assert.Null(coordinator.ReadLiveFocusedReceiptAttempt(scenario.Goal.Id, started.ReceiptId, candidateSha));
        }

        var measured = MeasureAdvance(driver, scenario.Goal);

        Assert.Equal(state == "no-prior-entry" ? SelectionSteps : SelectionSteps.Where(name => name != "walk-pretester-cli-surface"), measured.Parts);
        Assert.Contains("PRE_TESTER_GUARD_SELECTED class=ObservedSourceTests", measured.Console, StringComparison.Ordinal);
        var latest = Assert.IsType<PreTesterEvidenceEntry>(PreTesterEvidenceIndexLines.Latest(scenario.Goal, scenario.Tester.Id, candidateSha));
        if (state == "result-artifact")
        {
            Assert.IsType<ConductorAdvanceOutcome.Executed>(measured.Result.Outcome);
            Assert.Equal([AgentRole.Tester], roles);
            Assert.Equal("green", latest.Outcome);
        }
        else
        {
            Assert.Empty(roles);
            Assert.Equal("started", latest.Outcome);
            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(measured.Result.Outcome);
            Assert.StartsWith(state == "build-slot-blocked"
                ? "Background pre-tester-deferred focused evidence did not run (BlockedBuildSlot)"
                : "Background pre-tester-deferred focused evidence is running in attempt ", held.Reason, StringComparison.Ordinal);
        }
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void CompletionDuringLiveRead_ConsumesGreenEvidenceOnce()
    {
        using var scenario = new Scenario("deferred - DeferredAlphaTests", BaseSha, ["DeferredAlphaTests"]);
        Action? onLiveness = null;
        var roles = new List<AgentRole>();
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(scenario.Root, "background-attempts"), TrunkBranchName.Default,
            isProcessAlive: processId => { onLiveness?.Invoke(); return processId == 7103; },
            launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7103),
            acquireStableSlotLease: (_, _) => null);
        var driver = scenario.Driver(_ => throw new Xunit.Sdk.XunitException("Must not run inline."), roles.Add, coordinator: coordinator);
        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);
        var attempt = Assert.IsType<ConductorParallelAcceptanceAttempt>(coordinator.TryReadLatest(scenario.Goal.Id.Value));
        var completions = 0;
        onLiveness = () =>
        {
            onLiveness = null;
            completions++;
            CompleteAttempt(scenario, coordinator, attempt);
        };

        var measured = MeasureAdvance(driver, scenario.Goal);

        Assert.Equal(1, completions);
        Assert.IsType<ConductorAdvanceOutcome.Executed>(measured.Result.Outcome);
        Assert.Equal([AgentRole.Tester], roles);
        Assert.Equal(SelectionSteps.Where(name => name != "walk-pretester-cli-surface"), measured.Parts);
        Assert.Single(scenario.Goal.Timeline.Where(evt => evt.Kind == ProgressKind.FindingEvidenceRunRecorded &&
            PreTesterEvidenceIndexLines.Parse(evt.Message)?.Outcome == "green"));
        Assert.Equal(attempt.AttemptId, coordinator.TryReadLatest(scenario.Goal.Id.Value)!.AttemptId);
    }

    private static (ConductorAdvanceResult Result, string Console, List<string> Parts) MeasureAdvance(ConductorDriver driver, Goal goal)
    {
        var parts = new List<string>();
        var ledger = new ConductorTickStepLedger(() => TimeSpan.Zero, name =>
        {
            if (SelectionSteps.Contains(name, StringComparer.Ordinal)) parts.Add(name);
        });
        using var active = ledger.Activate();
        ConductorAdvanceResult result = null!;
        var console = AsyncLocalConsoleRouter.Capture(() =>
            ConductorTickStepLedger.Measure("walk-advance", () =>
                result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative)));
        return (result, console, parts);
    }

    private static void CompleteAttempt(Scenario scenario, ConductorParallelAcceptanceAttemptCoordinator coordinator,
        ConductorParallelAcceptanceAttempt attempt)
    {
        var candidate = ConductorParallelAcceptanceCandidate.Create(scenario.Goal, 0, [], branchHeadSha: CandidateSha);
        coordinator.RunAttemptForTests(attempt, candidate, ConductorAutonomyPolicy.Conservative,
            (current, _, _, _, _) => ConductorParallelAcceptanceRunResult.Focused(current,
                ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
                    scenario.Root, attempt.FocusedEvidenceRequest!, CandidateSha)));
        Assert.True(File.Exists(attempt.ResultPath), "The completed attempt must have a result artifact.");
        Assert.NotNull(coordinator.ReadFocusedReceiptEvidence(scenario.Goal.Id, attempt.FocusedEvidenceReceiptId!, CandidateSha));
    }

    [Fact]
    public void ChangedDeferredClassesRunBeforeTesterAndGreenReceiptIsIndexed()
    {
        using var scenario = new Scenario("deferred - DeferredAlphaTests, DeferredBetaTests", BaseSha,
            ["DeferredAlphaTests", "DeferredBetaTests"]);
        var order = new List<string>();
        var requests = new List<string>();
        var driver = scenario.Driver(
            request =>
            {
                order.Add("focused");
                requests.Add(request);
                return ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
                    scenario.Root, request, CandidateSha);
            },
            role => order.Add(role.ToString()));

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(["focused", "Tester"], order);
        var request = Assert.Single(requests);
        Assert.Equal(2, request.Split(';', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("DeferredAlphaTests", request, StringComparison.Ordinal);
        Assert.Contains("DeferredBetaTests", request, StringComparison.Ordinal);
        var lines = PreTesterEvidenceIndexLines.ForTester(scenario.Goal, scenario.Tester, CandidateSha);
        Assert.Contains(lines, line => line.Contains("state=executed-on-candidate", StringComparison.Ordinal) &&
                                       line.Contains("candidate_sha=" + CandidateSha, StringComparison.Ordinal));
        var brief = scenario.Kernel.BuildTaskBriefSource(
            scenario.Goal.Id, scenario.Tester.Id, targetHeadCommit: CandidateSha);
        Assert.Contains("state=executed-on-candidate",
            string.Join(Environment.NewLine, brief.Segments.SelectMany(segment => segment.Lines)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ActionableRedRetriesDeveloperWithoutTesterDispatch()
    {
        using var scenario = new Scenario("deferred - DeferredAlphaTests", BaseSha, ["DeferredAlphaTests"]);
        var roles = new List<AgentRole>();
        var feedback = new List<string>();
        var driver = scenario.Driver(
            request => CandidateRedFindingEvidence(request, CandidateSha,
                "DeferredAlphaTests.FailsOnCandidate"),
            roles.Add,
            feedback.Add);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal([AgentRole.Developer], roles);
        Assert.DoesNotContain(AgentRole.Tester, roles);
        Assert.StartsWith("ACTIONABLE_CANDIDATE_RED", Assert.Single(feedback), StringComparison.Ordinal);
        Assert.Contains("DeferredAlphaTests.FailsOnCandidate", feedback[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ActionableRedRetryCarriesCandidateFailureDetail()
    {
        using var scenario = new Scenario("deferred - DeferredAlphaTests", BaseSha, ["DeferredAlphaTests"]);
        var trxPath = Path.Combine(scenario.Root, "candidate.trx");
        ConductorDriverTestsActionableRedFailureDetail.WriteFailureTrx(
            trxPath, "DeferredAlphaTests.FailsOnCandidate");
        var roles = new List<AgentRole>();
        var feedback = new List<string>();
        var driver = scenario.Driver(request =>
        {
            var red = CandidateRedFindingEvidence(request, CandidateSha,
                "DeferredAlphaTests.FailsOnCandidate");
            var candidate = red.Arms!.Single(arm => arm.Arm == FindingEvidenceArm.Candidate);
            var check = candidate.Checks.Single() with { TestResultPaths = [trxPath] };
            return red with
            {
                Checks = [check],
                Arms = red.Arms.Select(arm => arm.Arm == FindingEvidenceArm.Candidate
                    ? arm with { Checks = [check] } : arm).ToArray()
            };
        }, roles.Add, feedback.Add);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal([AgentRole.Developer], roles);
        var message = Assert.Single(feedback);
        Assert.StartsWith("ACTIONABLE_CANDIDATE_RED", message, StringComparison.Ordinal);
        Assert.Contains("Candidate failure detail (receipt ", message, StringComparison.Ordinal);
        Assert.Contains("DeferredAlphaTests.FailsOnCandidate", message, StringComparison.Ordinal);
        Assert.Contains("fatal: synthetic too big", message, StringComparison.Ordinal);
        Assert.Contains("at Synthetic.First()", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CandidateOnlyRedUsesChangedTestSourceForAttribution(bool sourceChanged)
    {
        using var scenario = new Scenario("deferred - DeferredAlphaTests", BaseSha, ["DeferredAlphaTests"]);
        var roles = new List<AgentRole>();
        var feedback = new List<string>();
        var sourcePath = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/DeferredAlphaTests.cs";
        var driver = scenario.Driver(request =>
        {
            var red = CandidateRedFindingEvidence(request, CandidateSha,
                "DeferredAlphaTests.FailsOnCandidate");
            return red with { Arms = red.Arms!.Where(arm => arm.Arm == FindingEvidenceArm.Candidate).ToArray() };
        }, roles.Add, feedback.Add,
            changedPaths: sourceChanged ? [sourcePath] : ["tests/OtherTests.cs"]);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal([sourceChanged ? AgentRole.Developer : AgentRole.Tester], roles);
        if (sourceChanged)
            Assert.Contains("DeferredAlphaTests.FailsOnCandidate", Assert.Single(feedback), StringComparison.Ordinal);
        else
            Assert.Empty(feedback);
    }

    [Fact]
    public void InheritedRedContinuesToTesterWithoutDeveloperRetry()
    {
        using var scenario = new Scenario("deferred - DeferredAlphaTests", BaseSha, ["DeferredAlphaTests"]);
        var roles = new List<AgentRole>();
        var feedback = new List<string>();
        var driver = scenario.Driver(request =>
        {
            var red = CandidateRedFindingEvidence(request, CandidateSha,
                "DeferredAlphaTests.FailsOnBothArms");
            var baseline = red.Arms!.Single(arm => arm.Arm == FindingEvidenceArm.Baseline);
            return red with { Arms = red.Arms.Select(arm => arm.Arm == FindingEvidenceArm.Baseline
                ? baseline with { Disposition = FindingEvidenceArmDisposition.Red, Passed = false }
                : arm).ToArray() };
        }, roles.Add, feedback.Add);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal([AgentRole.Tester], roles);
        Assert.Empty(feedback);
    }

    [Fact]
    public void PartlyUnselectableDeclarationRunsResolvedClassAndReportsNotRun()
    {
        using var scenario = new Scenario("deferred - DeferredAlphaTests, MissingClass", BaseSha,
            ["DeferredAlphaTests"]);
        var requests = new List<string>();
        var roles = new List<AgentRole>();
        var driver = scenario.Driver(
            request =>
            {
                requests.Add(request);
                return ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
                    scenario.Root, request, CandidateSha);
            },
            roles.Add);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal([AgentRole.Tester], roles);
        Assert.Contains("DeferredAlphaTests", Assert.Single(requests), StringComparison.Ordinal);
        Assert.DoesNotContain("MissingClass", requests[0], StringComparison.Ordinal);
        Assert.Contains(PreTesterEvidenceIndexLines.ForTester(scenario.Goal, scenario.Tester, CandidateSha),
            line => line.Contains("evidence_not_run: MissingClass", StringComparison.Ordinal));
    }

    [Fact]
    public void RedWithoutFailingTestIdentityContinuesToTesterWithOutcome()
    {
        using var scenario = new Scenario("deferred - DeferredAlphaTests", BaseSha, ["DeferredAlphaTests"]);
        var roles = new List<AgentRole>();
        var driver = scenario.Driver(
            request =>
            {
                var red = CandidateRedFindingEvidence(request, CandidateSha);
                var arm = red.Arms!.Single(item => item.Arm == FindingEvidenceArm.Candidate);
                var check = arm.Checks.Single() with { FailingTestIdentities = [] };
                return red with { Checks = [check], Arms = [arm with { Checks = [check] }] };
            },
            roles.Add);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal([AgentRole.Tester], roles);
        Assert.Contains(PreTesterEvidenceIndexLines.ForTester(scenario.Goal, scenario.Tester, CandidateSha),
            line => line.Contains("state=red", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("deferred - DeferredAlphaTests", CandidateSha, true)]
    [InlineData("deferred - no named classes", BaseSha, false)]
    [InlineData("deferred - MissingClass", BaseSha, false)]
    public void UnchangedOrUnselectableDeclarationKeepsTesterDispatch(
        string tests, string baseSha, bool createSource)
    {
        using var scenario = new Scenario(tests, baseSha,
            createSource ? ["DeferredAlphaTests"] : []);
        var roles = new List<AgentRole>();
        var runs = 0;
        var driver = scenario.Driver(
            request =>
            {
                runs++;
                return ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
                    scenario.Root, request, CandidateSha);
            },
            roles.Add);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(0, runs);
        Assert.Equal([AgentRole.Tester], roles);
    }

    private sealed class Scenario : IDisposable
    {
        public string Root { get; } = ConductorDriverTests.CreateTempDirectory();
        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public TaskSpec Developer { get; }
        public TaskSpec Tester { get; }

        public Scenario(string tests, string baseSha, IReadOnlyList<string> classNames)
        {
            (Kernel, Goal) = SoftwareGoal("Pre-Tester deferred evidence");
            Developer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            Tester = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            foreach (var task in Goal.Tasks.TakeWhile(task => task.Id != Developer.Id))
                PassVerification(Kernel, Goal, task);
            DispatchTask(Kernel, Goal, Developer, baseCommit: baseSha);
            var output = string.Join(Environment.NewLine,
                "WORKER_RESULT:", "files: tests/DeferredAlphaTests.cs", "commands: none",
                "tests: " + tests, "commit: none", "blockers: none",
                "assigned_scope_complete: true", "model_fit: test/model - adequate - fixture",
                "skills: none", "confidence: high", "END_WORKER_RESULT");
            Kernel.RecordTaskVerification(Goal.Id, Developer.Id, new TaskVerificationRecord(
                "test.exe", "C:\\tmp", 0, output, "", DateTimeOffset.UtcNow,
                WorkerResultPresent: true, HasCommittedChanges: true));
            var worktree = GoalWorktrees.WorktreePath(Root, Goal.Id);
            Directory.CreateDirectory(worktree);
            File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: fixture");
            var project = Path.Combine(worktree, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            foreach (var name in classNames)
                File.WriteAllText(Path.Combine(project, name + ".cs"), "public class " + name + " { [Xunit.Fact] public void Runs() {} }");
        }

        public ConductorDriver Driver(
            Func<string, FocusedEvidenceRunResult> run,
            Action<AgentRole> dispatched,
            Action<string>? feedback = null,
            ConductorParallelAcceptanceAttemptCoordinator? coordinator = null,
            IReadOnlyList<string>? changedPaths = null,
            Action<string>? notes = null,
            string candidateSha = CandidateSha)
        {
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
                getLandingFileScopes: _ => changedPaths ?? [],
                focusedEvidenceAttemptCoordinator: coordinator ?? new ConductorParallelAcceptanceAttemptCoordinator(
                    Path.Combine(Root, "attempts"), Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, runInline: true, acquireStableSlotLease: (_, _) => null),
                runFocusedEvidence: (_, request) => run(request),
                recordTaskNote: (_, _, message) => notes?.Invoke(message),
                dispatchAndStart: goal =>
                {
                    dispatched(goal.Tasks.First(task => task.Status == WorkTaskStatus.Assigned).RequiredRole);
                    return DispatchStartOutcome.Started();
                },
                retryTaskWithCause: (goalId, taskId, message, round, cause) =>
                {
                    feedback?.Invoke(message);
                    return Kernel.RetryTask(goalId, taskId, message, retryRoundKind: round, retryCause: cause);
                },
                recordFindingEvidenceRequest: (goalId, taskId, message) =>
                    Kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceRun: (goalId, taskId, message) =>
                    Kernel.RecordFindingEvidenceRun(goalId, taskId, message),
                executionDirectory: Root);
            driver.OverrideDeveloperCompletionStructuralPreflightForTests(
                _ => new DeveloperCompletionStructuralFindings(false, "clean"), TimeSpan.FromSeconds(5));
            return driver;
        }

        public void AddObservedGuard()
        {
            var project = Path.Combine(GoalWorktrees.WorktreePath(Root, Goal.Id), "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
            File.WriteAllText(Path.Combine(project, "ObservedSourceTests.cs"), """
                public class ObservedSourceTests {
                    [Xunit.Fact] public void ReadsChangedSource() {
                        var path = Path.Combine(FindRoot(), "src", "X", "Y.cs");
                        var source = File.ReadAllText(path);
                    }
                }
                """);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
