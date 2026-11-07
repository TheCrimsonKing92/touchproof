using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: in-memory kernels, unique temporary logs, and injected clipboard sinks.
public sealed class ActivityEventStructuredFieldsTests
{
    [Fact]
    public void Escalation_WriterAndReader_PreserveTypedPartsAndGoldenDetail()
    {
        var outcome = Escalation();
        const string golden = "GOAL goal=abcdef12 result=escalated state=Failed slot=slot-2 reason=Goal_is_in_Failed_state;_TaskFailed:_Dispatch_failed:_rule=worker-build-evidence-missing:_worker_build_applicability=other-dispatch;_next=supply_authorized_current-dispatch_build_evidence_|_worker_build_applicability=tree-mismatch;_next=supply_evidence_matching_the_current_candidate;_operator_action_required decision=failed-goal/6/failed-terminal-fallthrough";
        var line = ConductorBatchLoop.FormatGoalProgressLine("abcdef12", outcome, 2);
        Assert.Equal(golden, line);
        var source = WriteRead(outcome, line);
        Assert.Equal(golden, source.Detail);
        var progress = Assert.IsType<GoalProgressEventFields>(source.GoalProgress);
        Assert.Equal("abcdef1234567890", progress.GoalId);
        Assert.Equal("escalated", progress.Result);
        Assert.Equal("Failed", progress.State);
        Assert.Equal(2, progress.Slot);
        Assert.Equal(outcome.Reason, progress.Reason);
        Assert.Equal(outcome.Failure, progress.Failure);
        Assert.Equal("TaskFailed", progress.Failure!.Kind);
        Assert.Equal("worker-build-evidence-missing", progress.Failure.Rule);
        Assert.Equal(new[] { "other-dispatch", "tree-mismatch" }, progress.Failure.Evidence.Select(entry => entry.Value));
        Assert.Equal(new[] { "supply authorized current-dispatch build evidence", "supply evidence matching the current candidate" },
            progress.Failure.Evidence.Select(entry => entry.NextStep));
        Assert.Equal(outcome.Decision!.Stage, progress.Decision!.Stage);
        Assert.Equal(outcome.Decision.Facts, progress.Decision.Facts);
    }

    [Fact]
    public void Escalation_ActivityDetail_ExposesEachPartAndCopiesFullValues()
    {
        var outcome = Escalation() with { Reason = new string('x', 700) + " real spaces 日本語" };
        var source = WriteRead(outcome, ConductorBatchLoop.FormatGoalProgressLine("abcdef12", outcome, 2));
        var detail = OwnerActivityExplanation.ExplainWithFields(Assert.Single(OwnerActivityNarrator.Narrate([source], _ => "Title")));
        var original = detail.Text[detail.Text.IndexOf("\n\nOriginal event\n", StringComparison.Ordinal)..];
        Assert.Contains("\nGoal ID: abcdef1234567890\n", original);
        Assert.Contains("\nResult: escalated\nState: Failed\nSlot: 2\nReason: " + outcome.Reason, original);
        Assert.Contains("\nDecision: failed-goal/6/failed-terminal-fallthrough; action=Escalate; reason=Operator action", original);
        Assert.Contains("\nFailure kind: TaskFailed\nRule: worker-build-evidence-missing\n", original);
        Assert.Contains("\nWorker build applicability 1: other-dispatch; next=supply authorized current-dispatch build evidence\n", original);
        Assert.Contains("\nWorker build applicability 2: tree-mismatch; next=supply evidence matching the current candidate\n", original);
        Assert.EndsWith("Raw event: " + source.Detail, original);
        Assert.Equal("F2 fields  Ctrl+C copy field", detail.Structured.FooterHint);
        using var activity = new OwnerConsoleTextDialog("Activity detail", detail.Text) { Structured = detail.Structured };
        Assert.Contains(activity.SubViews.OfType<Terminal.Gui.Views.Label>(),
            label => label.Visible && label.Text == "F2 fields  Ctrl+C copy field");
        var fields = detail.Fields.Fields;
        Assert.Equal(fields.Length, fields.Select(field => field.Key).Distinct().Count());
        Assert.Equal(fields.Length, fields.Select(field => field.Label).Distinct().Count());
        Assert.All(fields, field => Assert.False(string.IsNullOrWhiteSpace(field.Value)));
        Assert.DoesNotContain(fields, field => field.Key == "detail");
        Assert.Contains(fields, field => field.Label == "Original event: Timestamp");
        Assert.Equal(outcome.Reason, fields.Single(field => field.Key == "event/0/reason").Value);
        var copies = new List<string>();
        foreach (var field in fields.Where(field => field.Key.StartsWith("event/0/", StringComparison.Ordinal)))
        {
            using var dialog = new OwnerConsoleFieldsDialog(detail.Fields, new(value => { copies.Add(value); return true; }), field.Key);
            dialog.Layout(new System.Drawing.Size(60, 24));
            Assert.True(dialog.Value.WordWrap);
            Assert.Equal(field.Value, dialog.Value.Text);
            Assert.True(dialog.HandleKey(OwnerConsoleBindings.CopyField));
            Assert.Equal(field.Value, copies[^1]);
        }
        Assert.Contains(outcome.Reason, copies);
        Assert.Contains("tree-mismatch; next=supply evidence matching the current candidate", copies);
    }

    [Fact]
    public void LegacyGoal_WithoutPayload_SplitsTokensAndRetainsRawText()
    {
        const string raw = "GOAL goal=abcdef12 result=escalated state=Failed reason=Goal_is_in_Failed_state;_rule=opaque empty= token=a=b";
        var source = Read(raw);
        Assert.Null(source.GoalProgress);
        var detail = OwnerActivityExplanation.ExplainWithFields(Assert.Single(OwnerActivityNarrator.Narrate([source], _ => "Title")));
        Assert.Contains("Result: escalated\nState: Failed\nReason: Goal is in Failed state; rule=opaque", detail.Text);
        Assert.EndsWith("Raw event: " + raw, detail.Text);
        Assert.Equal("Goal is in Failed state; rule=opaque", detail.Fields.Fields.Single(field => field.Label == "Reason").Value);
        Assert.Equal("a=b", detail.Fields.Fields.Single(field => field.Label == "Token").Value);
        Assert.DoesNotContain(detail.Fields.Fields, field => field.Label is "Empty" or "Rule");
    }

    [Fact]
    public void PresentPayload_NeverFillsGapsFromRawTokens_AndOmitsEmptyFields()
    {
        var source = Read("GOAL goal=legacy result=escalated state=Failed reason=legacy_rule=wrong",
            new GoalProgressEventFields(Result: "held", Reason: "  ", Slot: 0,
                Failure: new("", null, [new("worker_build_applicability", "", " ")])));
        var detail = OwnerActivityExplanation.ExplainWithFields(Assert.Single(OwnerActivityNarrator.Narrate([source], _ => "Title")));
        Assert.Equal("held", detail.Fields.Fields.Single(field => field.Key == "event/0/result").Value);
        Assert.Equal("0", detail.Fields.Fields.Single(field => field.Key == "event/0/slot").Value);
        Assert.DoesNotContain(detail.Fields.Fields, field => field.Key is "event/0/state" or "event/0/reason" or "event/0/rule" or "event/0/failure-kind" || field.Key.StartsWith("event/0/legacy/", StringComparison.Ordinal));
        Assert.DoesNotContain(detail.Fields.Fields, field => field.Key.StartsWith("event/0/evidence/", StringComparison.Ordinal));
        Assert.All(detail.Fields.Fields, field => Assert.False(string.IsNullOrWhiteSpace(field.Value)));
    }

    [Theory]
    [InlineData("executed")]
    [InlineData("held")]
    [InlineData("done")]
    public void OutcomeKinds_Builder_PreservesAvailableValues(string result)
    {
        var decision = Escalation().Decision;
        ConductorAdvanceOutcome outcome = result switch
        {
            "executed" => new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Verified, "Run") { Decision = decision },
            "held" => new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, "Wait for gate") { Decision = decision },
            _ => new ConductorAdvanceOutcome.Done(GoalLifecycleState.Verified) { Decision = decision }
        };
        var fields = Assert.IsType<GoalProgressEventFields>(GoalProgressEventFields.From("full-id", outcome, null));
        Assert.Equal(result, fields.Result);
        Assert.Equal("Verified", fields.State);
        Assert.Equal("full-id", fields.GoalId);
        Assert.Same(decision, fields.Decision);
        Assert.Equal(result == "held" ? "Wait for gate" : null, fields.Reason);
        Assert.Null(fields.Slot);
    }

    [Theory]
    [InlineData("{\"reason\":42}")]
    [InlineData("{\"unknown\":\"value\"}")]
    [InlineData("{\"result\":\"held\",\"failure\":{\"evidence\":null}}")]
    public void MalformedPayload_Reader_KeepsLegacyEvent(string payload)
    {
        var json = "{\"timestamp\":\"1970-01-01T00:00:00Z\",\"eventKind\":\"goal-escalation\",\"detail\":\"GOAL goal=old result=escalated\",\"goalProgress\":" + payload + "}";
        Assert.True(ConductEventFileSource.TryParse(json, out var source));
        Assert.Null(source!.GoalProgress);
        Assert.Equal("GOAL goal=old result=escalated", source.Detail);
        Assert.Equal("escalated", OwnerGoalProgressDetail.Fields(source).Single(field => field.Label == "Result").Value);
    }

    [Fact]
    public void RepeatedReads_WithDecisionAndEvidence_DeduplicateByValue()
    {
        var progress = GoalProgressEventFields.From("full-id", Escalation(), 2);
        var first = Read("GOAL goal=abcdef12 result=escalated state=Failed", progress);
        var second = Read(first.Detail, progress);
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Single(OwnerActivityNarrator.Narrate([first, second], _ => "Title"));
        var different = second with { GoalProgress = progress! with { Failure = progress.Failure! with { Evidence = progress.Failure.Evidence.Reverse().ToArray() } } };
        Assert.NotEqual(first, different);
    }

    [Fact]
    public void DispatchFailure_KernelAndSnapshot_PreserveFactsForEscalation()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
        var goal = kernel.CreateGoal("Structured failure", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        const string rule = DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing;
        const string diagnostic = "worker_build_applicability=other-dispatch; next=supply authorized current-dispatch build evidence\nworker_build_applicability=tree-mismatch; next=supply evidence matching the current candidate";
        var at = DateTimeOffset.UnixEpoch;
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("worker", "worker.exe", "fixture", at));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord("worker.exe", "fixture", 1,
            "worker output", DispatchFailureDiagnosticMarker.Format(rule) + "\n" + diagnostic, at.AddSeconds(1), OrchestratorFailureReason: rule));
        var recorded = Assert.Single(goal.Timeline, entry => entry.Kind == ProgressKind.TaskFailed);
        Assert.Equal("Dispatch failed: rule=worker-build-evidence-missing: " + diagnostic.Replace("\n", " | ") + " Command: worker.exe", recorded.Message);
        Assert.Equal("TaskFailed", recorded.Failure!.Kind);
        Assert.Equal(rule, recorded.Failure.Rule);
        Assert.Equal(2, recorded.Failure.Evidence.Count);
        var snapshot = JsonSerializer.Deserialize<OrchestratorSnapshot>(JsonSerializer.Serialize(kernel.ExportSnapshot()))!;
        var restored = AgentOrchestratorKernel.FromSnapshot(snapshot).GetGoal(goal.Id);
        var terminal = ConductorOutcomeText.TerminalFailure(restored, GoalLifecycleState.Failed);
        Assert.Equal(recorded.Failure, terminal);
        Assert.Equal("supply evidence matching the current candidate", terminal!.Evidence[1].NextStep);
        Assert.Equal("Goal is in Failed state; TaskFailed: " + recorded.Message + "; operator action required",
            ConductorOutcomeText.BuildTerminalEscalationReason(restored, GoalLifecycleState.Failed, value => value));
    }

    [Fact]
    public void DiagnosticEvidence_HostBoundary_PreservesOrderDuplicatesAndFullValues()
    {
        const string rule = DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing;
        var full = new string('x', 1200) + " full guidance";
        var stderr = "worker_build_applicability=worker-spoof; next=wrong\n" + DispatchFailureDiagnosticMarker.Format(rule) +
            "\nworker_build_applicability=tree-mismatch; next=" + full +
            "\nworker_build_applicability=tree-mismatch\naccounting\nworker_build_applicability=outside; next=wrong";
        var entries = DispatchContractDiagnostic.ExtractEvidence(stderr, rule);
        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.Equal("tree-mismatch", entry.Value));
        Assert.Equal(full, entries[0].NextStep);
        Assert.Null(entries[1].NextStep);
        Assert.Empty(DispatchContractDiagnostic.ExtractEvidence(stderr, "unknown"));
    }

    private static ConductorAdvanceOutcome.Escalated Escalation() => new(GoalLifecycleState.Failed,
        "Goal is in Failed state; TaskFailed: Dispatch failed: rule=worker-build-evidence-missing: worker_build_applicability=other-dispatch; next=supply authorized current-dispatch build evidence | worker_build_applicability=tree-mismatch; next=supply evidence matching the current candidate; operator action required")
    {
        Decision = new("failed-goal", "Escalate", 6, "failed-terminal-fallthrough", "Operator action", [new("state", "Failed")]),
        Failure = new("TaskFailed", "worker-build-evidence-missing", [
            new("worker_build_applicability", "other-dispatch", "supply authorized current-dispatch build evidence"),
            new("worker_build_applicability", "tree-mismatch", "supply evidence matching the current candidate")])
    };

    private static OwnerConductEvent Read(string detail, GoalProgressEventFields? progress = null)
    {
        var line = JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UnixEpoch, eventKind = "goal-escalation", goalId = "abcdef1234567890", detail, goalProgress = progress }, JsonSerializerOptions.Web);
        Assert.True(ConductEventFileSource.TryParse(line, out var source));
        return source!;
    }

    private static OwnerConductEvent WriteRead(ConductorAdvanceOutcome outcome, string detail)
    {
        var root = Path.Combine(Path.GetTempPath(), "activity-fields-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "conduct-events.log");
            var writer = new ConductEventLogWriter(path, utcNow: () => DateTimeOffset.UnixEpoch);
            writer.AppendGoalProgress("goal-escalation", "abcdef12", detail,
                GoalProgressEventFields.From("abcdef1234567890", outcome, 2));
            Assert.True(ConductEventFileSource.TryParse(Assert.Single(File.ReadAllLines(path)), out var source));
            return source!;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
