using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Owned databases/clock; console capture shares the existing CLI collection.
[Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliAttentionJsonSelectiveReadTests : CliTaskQueryTestSupport
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    [Theory]
    [InlineData(4, false)]
    [InlineData(80, true)]
    public async Task MixedHistory_EnvelopeMatchesBaseline_LoadsOnlyActionableOwners(int history, bool compress)
    {
        using var fixture = await Fixture.CreateAsync(history + 10);
        var goals = fixture.Goals;
        var current = goals[0] with
        {
            Status = GoalStatus.Active, BriefVersions =
            [new(1, goals[0].Objective, Now.AddDays(-1), SupersededByVersion: 2), new(2, goals[0].Objective, Now)],
            GateTestObligations = [Gate(1), Gate(2)]
        };
        await fixture.WriteGoalAsync(current);
        await fixture.WriteWaitAsync(new("current-wait", current.Id, null, "Current", Now,
            Kind: HumanWaitKind.ProspectiveAcceptanceEvidence, PlannerIdentity: Identity(current, 2)));
        await fixture.WriteWaitAsync(new("stale-wait", current.Id, null, "Stale", Now,
            PlannerIdentity: Identity(current, 1)));
        await fixture.WriteWaitAsync(new("completed-wait", current.Id, null, "Answered", Now,
            IsCompleted: true, Answer: "Proceed", AnsweredAt: Now));
        await fixture.WriteGoalAsync(goals[1] with { Status = GoalStatus.Active });
        await fixture.WriteGoalAsync(goals[2] with { Status = GoalStatus.Parked, CurrentHold =
            Hold("author-owner-question", "author-owner-question item=HumanInput:wait-x reason=choose question=Choose? recommendation=Yes") });
        await fixture.WriteGoalAsync(goals[3] with { Status = GoalStatus.Failed, CurrentHold = Hold("steward-owner-question",
            $"held trigger=worker:{goals[3].Tasks[1].Id}:round:1 question=Retry? evidence=[receipt]") });
        await fixture.WriteGoalAsync(goals[4] with { Status = GoalStatus.Active,
            CurrentHold = Hold("steward-owner-question", "malformed marker fallback") });
        foreach (var index in new[] { 2, 3, 5, 6, 7 })
        {
            var status = index switch { 2 => GoalStatus.Parked, 3 => GoalStatus.Failed,
                5 => GoalStatus.Completed, 6 => GoalStatus.Cancelled, _ => GoalStatus.Superseded };
            if (index >= 5) await fixture.WriteGoalAsync(goals[index] with { Status = status,
                CurrentHold = Hold("steward-owner-question", "excluded question"), GateTestObligations = [Gate(1)] },
                archive: index != 5, compress: compress);
            await fixture.WriteWaitAsync(new("hidden-" + index, goals[index].Id, null, "Nonlive", Now));
            await fixture.Store.RaiseAsync(CollaborationItemType.Decision, goals[index].Id, "Hidden", "Body");
        }
        await fixture.WriteGoalAsync(goals[8] with { Status = GoalStatus.Active, BriefVersions =
            [new(1, goals[8].Objective, Now.AddDays(-1), SupersededByVersion: 2), new(2, goals[8].Objective, Now)] });
        await fixture.WriteWaitAsync(new("stale-only", goals[8].Id, null, "Stale only", Now,
            PlannerIdentity: Identity(goals[8], 1)));
        await fixture.WriteWaitAsync(new("orphan-wait", Id(9999), null, "Orphan", Now));
        await fixture.Store.RaiseAsync(CollaborationItemType.Decision, goals[1].Id, "Live item", "Body");
        await fixture.Store.RaiseAsync(CollaborationItemType.Decision, null, "Global", "Body");
        await fixture.Store.RaiseAsync(CollaborationItemType.Decision, Id(9999), "Orphan", "Body");
        await fixture.WriteGoalAsync(WithArchiveEvidence(goals[9], "providerHolds"), archive: true, compress: compress);
        for (var index = 10; index < goals.Length; index++)
            await fixture.WriteGoalAsync(goals[index] with { Status = GoalStatus.Completed }, archive: true, compress: compress);

        await fixture.WriteAttentionMarkersAsync();
        var observations = new List<GoalSnapshotReadObservation>();
        var queries = SqliteOrchestratorStateRepository.OpenReadOnly(fixture.Workspace.SqliteStatePath, observations.Add);
        var probe = new QueryProbe(queries);
        var evidenceFreeIds = goals.Skip(10).Select(goal => goal.Id).ToHashSet();
        var expected = await fixture.BaselineAsync();
        foreach (var args in new[] { new[] { "attention", "--json" }, new[] { "attention", "list", "--json" } })
        {
            probe.Loads.Clear();
            observations.Clear();
            var actual = CaptureConsole(() => CliAttentionJsonQueryCommand.Execute(args, probe, fixture.Workspace, new FixedTime()));
            AssertJsonEqual(expected, actual);
            Assert.Equal(new[] { current.Id, goals[1].Id }, Assert.Single(probe.Loads));
            Assert.Equal(3, probe.LastProjection!.Reads.ArchivePayloadsRead);
            Assert.Equal(3, probe.LastProjection.Reads.ArchivePayloadsDecoded);
            Assert.Contains(observations, read => read.Operation == "attention-task-history");
            Assert.Contains(observations, read => read.Operation == "attention-timeline");
            Assert.DoesNotContain(observations, read => read.GoalId is { } id && evidenceFreeIds.Contains(id) &&
                read.Operation is "attention-archive-decode" or "attention-task-history" or "attention-timeline");
        }
        using var document = JsonDocument.Parse(expected);
        var payload = document.RootElement.GetProperty("payload");
        Assert.Single(payload.GetProperty("waits").EnumerateArray());
        Assert.Equal(2, payload.GetProperty("items").GetArrayLength());
        Assert.Equal(3, payload.GetProperty("ownerQuestionHolds").GetArrayLength());
        Assert.Equal(2, payload.GetProperty("ownerQuestionHolds")[1].GetProperty("taskNumber").GetInt32());
        Assert.Equal(4, payload.GetProperty("gateEvidence").GetArrayLength());
        Assert.Single(payload.GetProperty("providerHolds").EnumerateArray());
        Assert.Equal(0, probe.BoardLoads);
        foreach (var args in SummaryForms())
        {
            probe.Loads.Clear();
            var summary = CaptureConsole(() => CliAttentionJsonQueryCommand.Execute(args, probe, fixture.Workspace, new FixedTime()));
            AssertSummaryMatches(expected, summary);
            Assert.Empty(probe.LastProjection!.GateEvidence);
            Assert.Equal(new[] { current.Id, goals[1].Id }, Assert.Single(probe.Loads));
        }
    }

    [Fact]
    public async Task MarkerBackedArchiveGrowth_PreservesOutputBytesAndReadCounts()
    {
        using var large = await Fixture.CreateAsync(84);
        using var small = await Fixture.CreateAsync(large.Goals[..8]);
        await small.SeedMarkedHistoryAsync();
        await large.SeedMarkedHistoryAsync();
        var smallReads = new List<GoalSnapshotReadObservation>();
        var largeReads = new List<GoalSnapshotReadObservation>();
        var smallProbe = new QueryProbe(small.OpenObservedQueries(smallReads));
        var largeProbe = new QueryProbe(large.OpenObservedQueries(largeReads));
        var evidenceFreeIds = large.Goals.Skip(4).Select(goal => goal.Id).ToHashSet();

        foreach (var args in AttentionForms())
        {
            var summary = args.Length == 4;
            smallReads.Clear();
            largeReads.Clear();
            var smallOutput = RunAttention(args, small, smallProbe);
            var largeOutput = RunAttention(args, large, largeProbe);
            Assert.Equal(smallOutput, largeOutput);
            Assert.Equal(await small.BaselineAsync(summary) + Environment.NewLine, smallOutput);
            Assert.Equal(await large.BaselineAsync(summary) + Environment.NewLine, largeOutput);

            var smallCounts = smallProbe.LastProjection!.Reads;
            var largeCounts = largeProbe.LastProjection!.Reads;
            Assert.Equal(small.Goals.Length, smallCounts.HotRows);
            Assert.Equal(large.Goals.Length, largeCounts.HotRows);
            Assert.Equal(summary ? 2 : 3, smallCounts.ArchivePayloadsRead);
            Assert.Equal(smallCounts.ArchivePayloadsRead, largeCounts.ArchivePayloadsRead);
            Assert.Equal(smallCounts.ArchivePayloadsRead, smallCounts.ArchivePayloadsDecoded);
            Assert.Equal(smallCounts.ArchivePayloadsDecoded, largeCounts.ArchivePayloadsDecoded);
            Assert.DoesNotContain(smallReads.Concat(largeReads), read => read.GoalId is { } id &&
                evidenceFreeIds.Contains(id) &&
                read.Operation is "attention-archive-decode" or "attention-task-history" or "attention-timeline");
        }
    }

    [Theory]
    [InlineData("gateEvidence", "goalId")]
    [InlineData("postLandingDuties", "goalId")]
    [InlineData("providerHolds", "sourceGoalId")]
    public async Task EvidenceBearingTerminalArchive_IsDecodedAndRendered(string section, string goalIdProperty)
    {
        using var fixture = await Fixture.CreateAsync(2);
        var goal = WithArchiveEvidence(fixture.Goals[0], section);
        await fixture.WriteGoalAsync(goal, archive: true, compress: true);
        await fixture.WriteGoalAsync(fixture.Goals[1] with { Status = GoalStatus.Completed }, archive: true, compress: true);
        await fixture.WriteAttentionMarkersAsync();
        var reads = new List<GoalSnapshotReadObservation>();
        var probe = new QueryProbe(fixture.OpenObservedQueries(reads));

        var output = RunAttention(["attention", "--json"], fixture, probe);

        Assert.Equal(goal.Id, Assert.Single(reads.Where(read => read.Operation == "attention-archive-decode")).GoalId);
        Assert.Equal(1, probe.LastProjection!.Reads.ArchivePayloadsDecoded);
        using var document = JsonDocument.Parse(output);
        var row = Assert.Single(document.RootElement.GetProperty("payload").GetProperty(section).EnumerateArray());
        Assert.Equal(goal.Id, row.GetProperty(goalIdProperty).GetString());
        if (section == "postLandingDuties")
            Assert.Equal("Pending", row.GetProperty("state").GetString());
    }

    [Fact]
    public async Task UnsupportedProjection_MatchesMarkerBackedCliOutputBytes()
    {
        using var fixture = await Fixture.CreateAsync(84);
        await fixture.SeedMarkedHistoryAsync();

        foreach (var args in AttentionForms())
        {
            var bounded = new QueryProbe(fixture.Queries);
            var fallback = new QueryProbe(fixture.Queries) { Unsupported = true };

            Assert.Equal(RunAttention(args, fixture, bounded), RunAttention(args, fixture, fallback));

            Assert.Equal(args.Length == 4 ? 2 : 3, bounded.LastProjection!.Reads.ArchivePayloadsDecoded);
            Assert.Equal(new[] { fixture.Goals[0].Id }, Assert.Single(bounded.Loads));
            Assert.Equal(1, fallback.MetadataLoads);
            Assert.Equal(fixture.Goals.Select(goal => goal.Id).Order(StringComparer.Ordinal),
                Assert.Single(fallback.Loads).Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task HistoricalGateGrowth_LeavesSummaryBytesAndActionableRowsUnchanged()
    {
        using var fixture = await Fixture.CreateAsync(4);
        for (var index = 1; index < 4; index++)
            await fixture.WriteGoalAsync(fixture.Goals[index] with { Status = GoalStatus.Completed });
        await fixture.WriteWaitAsync(new("fixed-wait", fixture.Goals[0].Id, null, "Choose?", Now));
        await fixture.Store.RaiseAsync(CollaborationItemType.Decision, fixture.Goals[0].Id, "Fixed item", "Body");
        string Read(string[] args) => CaptureConsole(() =>
            CliAttentionJsonQueryCommand.Execute(args, fixture.Queries, fixture.Workspace, new FixedTime()));
        var before = Read(SummaryForms()[0]);
        AssertSummaryMatches(Read(["attention", "list", "--json"]), before);
        for (var index = 1; index < 4; index++)
            await fixture.WriteGoalAsync(fixture.Goals[index] with
            {
                Status = GoalStatus.Completed,
                GateTestObligations = Enumerable.Range(0, 150)
                    .Select(number => new GateTestObligation("historical-" + number, 1, ["Selector"], null, "Pending", Now)).ToArray()
            }, archive: index > 1, compress: index == 3);
        var full = Read(["attention", "list", "--json"]);
        using var document = JsonDocument.Parse(full);
        Assert.Equal(450, document.RootElement.GetProperty("payload").GetProperty("gateEvidence").GetArrayLength());
        foreach (var args in SummaryForms())
        {
            var after = Read(args);
            Assert.Equal(before, after);
            Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(before), System.Text.Encoding.UTF8.GetByteCount(after));
            AssertSummaryMatches(full, after);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoWaits_GlobalOrEmpty_LoadsNoSnapshots(bool global)
    {
        using var fixture = await Fixture.CreateAsync(24);
        foreach (var goal in fixture.Goals)
            await fixture.WriteGoalAsync(goal with { Status = GoalStatus.Completed }, archive: true, compress: true);
        if (global) await fixture.Store.RaiseAsync(CollaborationItemType.Decision, null, "Global only", "Body");
        var probe = new QueryProbe(fixture.Queries);
        var actual = await AttentionJsonReadContext.ReadAsync(probe, fixture.Workspace, Now);
        AssertJsonEqual(await fixture.BaselineAsync(), Render(actual));
        Assert.Empty(probe.Loads);
        Assert.Empty(actual.Waits);
        Assert.Equal(global ? 1 : 0, actual.Items.Count);
        Assert.Equal(24, probe.LastProjection!.Reads.ArchivePayloadsRead);
        Assert.Equal(0, probe.LastProjection.Reads.ProviderTasks);
    }

    [Fact]
    public async Task MissingPersistedWait_SelectsItsWaitingTaskOwner_AndMatchesRepair()
    {
        using var fixture = await Fixture.CreateAsync(3);
        var goal = fixture.Goals[0];
        await fixture.WriteGoalAsync(goal with { Status = GoalStatus.WaitingForHuman,
            Tasks = goal.Tasks.Select((task, index) => index == 0 ? task with { Status = WorkTaskStatus.WaitingForHuman } : task).ToArray(),
            Timeline = [new(goal.Id, goal.Tasks[0].Id, ProgressKind.HumanInputRequested, "Recovered question", Now)] });
        var probe = new QueryProbe(fixture.Queries);
        var actual = await AttentionJsonReadContext.ReadAsync(probe, fixture.Workspace, Now);
        AssertJsonEqual(await fixture.BaselineAsync(), Render(actual));
        Assert.Equal("Recovered question", Assert.Single(actual.Waits).Question);
        Assert.Equal(new[] { goal.Id }, Assert.Single(probe.Loads));
    }

    [Theory]
    [InlineData(false, 120, false)]
    [InlineData(true, 120, false)]
    [InlineData(false, -20, false)]
    [InlineData(true, -20, false)]
    [InlineData(false, -20, true)]
    [InlineData(true, -20, true)]
    public async Task ProviderEnvelope_CrossGoalArchivedResetAndProbe_NeedsNoFullLoads(
        bool compress, int resetDelayMinutes, bool currentGeneration)
    {
        using var fixture = await Fixture.CreateAsync(4);
        var goals = fixture.Goals;
        var dispatch = new TaskDispatchSnapshot("claude-cli", "claude", "fixture", Now.AddHours(-2),
            ProviderName: "Claude", ClaudeCredentialSourceDirectory: "account-a");
        var verification = new TaskVerificationSnapshot("claude", "fixture", 1, "", "You've hit your usage limit",
            Now.AddHours(-1), StandardErrorPath: "receipt", ProviderFailureKind: ProviderFailureKind.BudgetExhausted);
        await fixture.WriteGoalAsync(goals[0] with { Status = GoalStatus.Completed,
            Tasks = [goals[0].Tasks[0] with { LastDispatch = dispatch, LastVerification = verification }] },
            archive: true, compress: compress);
        var resetAt = Now.AddMinutes(resetDelayMinutes);
        const string binding = "claude::account-a";
        await fixture.WriteGoalAsync(goals[1] with { Status = GoalStatus.Cancelled, Timeline =
            [new(goals[1].Id, null, ProgressKind.TaskNote,
                ProviderBudgetHoldSchedules.FormatReset(new(binding, resetAt, "intent")), Now.AddMinutes(-30))] },
            archive: true, compress: !compress);
        var probeAt = Now.AddMinutes(-10);
        var marker = new ProviderBudgetHoldSchedules.ProbeMarker(binding, resetAt, 1, "operator");
        if (currentGeneration)
        {
            Assert.True(probeAt >= resetAt, "A current-generation admission must occur at or after the reset is due.");
            marker = marker with { ResetIntentId = "intent", HoldPlacedAt = verification.CompletedAt };
        }
        await fixture.WriteGoalAsync(goals[2] with { Status = GoalStatus.Active,
            Tasks = [goals[2].Tasks[0] with { Status = WorkTaskStatus.Assigned, AssignedAgentId = "agent" }], Timeline =
            [new(goals[2].Id, goals[2].Tasks[0].Id, ProgressKind.TaskNote,
                ProviderBudgetHoldSchedules.FormatProbe(marker), probeAt)] });
        await fixture.Store.RaiseAsync(CollaborationItemType.Decision, null, "Global", "Body");
        var probe = new QueryProbe(fixture.Queries);
        var context = await AttentionJsonReadContext.ReadAsync(probe, fixture.Workspace, Now);
        AssertJsonEqual(await fixture.BaselineAsync(), Render(context));
        Assert.Empty(probe.Loads);
        var hold = Assert.Single(context.ProviderHolds);
        Assert.Equal(goals[0].Id, hold.Hold.SourceGoalId.Value);
        Assert.Equal(verification.CompletedAt, hold.Hold.PlacedAt);
        var schedule = Assert.IsType<ProviderBudgetHoldSchedule>(hold.Schedule);
        Assert.Equal(resetAt, schedule.NextProbeAt);
        Assert.Equal("intent", schedule.ResetIntentId);
        if (currentGeneration)
        {
            Assert.Equal(new GoalId(goals[2].Id), schedule.AdmittedGoalId);
            Assert.Equal(new TaskId(goals[2].Tasks[0].Id), schedule.AdmittedTaskId);
        }
        else
        {
            Assert.Null(schedule.AdmittedGoalId);
            Assert.Null(schedule.AdmittedTaskId);
        }
        Assert.Equal(2, probe.LastProjection!.Reads.ArchivePayloadsRead);
        var fallback = new QueryProbe(fixture.Queries) { Unsupported = true };
        AssertJsonEqual(Render(context), Render(await AttentionJsonReadContext.ReadAsync(fallback, fixture.Workspace, Now)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImplementedProjectionFailure_Propagates_WithoutFallback(bool cancel)
    {
        using var fixture = await Fixture.CreateAsync(2);
        using var cancellation = new CancellationTokenSource();
        if (cancel) cancellation.Cancel();
        var failure = cancel ? (Exception)new OperationCanceledException(cancellation.Token) : new IOException("projection fault");
        var probe = new QueryProbe(fixture.Queries) { Failure = failure };
        var actual = await Assert.ThrowsAnyAsync<Exception>(() =>
            AttentionJsonReadContext.ReadAsync(probe, fixture.Workspace, Now, cancellation.Token));
        Assert.IsType(failure.GetType(), actual);
        Assert.Empty(probe.Loads);
        Assert.Equal(0, probe.MetadataLoads);
        var summaryFailure = await Assert.ThrowsAnyAsync<Exception>(() =>
            AttentionJsonReadContext.ReadAsync(probe, fixture.Workspace, Now, new(false), cancellation.Token));
        Assert.IsType(failure.GetType(), summaryFailure);
        Assert.Empty(probe.Loads);
        Assert.Equal(0, probe.MetadataLoads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringSelectedOrFallbackLoad_Propagates(bool unsupported)
    {
        using var fixture = await Fixture.CreateAsync(3);
        await fixture.WriteWaitAsync(new("wait", fixture.Goals[0].Id, null, "Question", Now));
        using var cancellation = new CancellationTokenSource();
        var probe = new QueryProbe(fixture.Queries) { Unsupported = unsupported, BeforeLoad = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AttentionJsonReadContext.ReadAsync(probe, fixture.Workspace, Now, cancellation.Token));
        Assert.Single(probe.Loads);
        probe.Loads.Clear();
        using var summaryCancellation = new CancellationTokenSource();
        var summaryProbe = new QueryProbe(fixture.Queries)
            { Unsupported = unsupported, BeforeLoad = summaryCancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AttentionJsonReadContext.ReadAsync(
            summaryProbe, fixture.Workspace, Now, new(false), summaryCancellation.Token));
        Assert.Single(summaryProbe.Loads);
    }

    [Fact]
    public async Task ImplementedNotSupportedFailure_IsNotTheUnsupportedCapabilityValue()
    {
        using var fixture = await Fixture.CreateAsync(1);
        var probe = new QueryProbe(fixture.Queries) { Failure = new NotSupportedException("implemented query failed") };
        await Assert.ThrowsAsync<NotSupportedException>(() => AttentionJsonReadContext.ReadAsync(probe, fixture.Workspace, Now));
        Assert.Empty(probe.Loads);
        Assert.Equal(0, probe.MetadataLoads);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            AttentionJsonReadContext.ReadAsync(probe, fixture.Workspace, Now, new AttentionProjectionReadOptions(false), default));
        Assert.Empty(probe.Loads);
        Assert.Equal(0, probe.MetadataLoads);
    }

    [Fact]
    public async Task EmptySqliteStore_ProducesBaselineEnvelopeWithoutGoalLoads()
    {
        using var fixture = await Fixture.CreateAsync(0);
        var probe = new QueryProbe(fixture.Queries);
        var actual = await AttentionJsonReadContext.ReadAsync(probe, fixture.Workspace, Now);
        AssertJsonEqual(await fixture.BaselineAsync(), Render(actual));
        Assert.Empty(probe.Loads);
        Assert.Equal(new AttentionProjectionReadCounts(0, 0, 0, 0, 0), probe.LastProjection!.Reads);
    }

    private static string[][] SummaryForms() =>
        [["attention", "list", "--summary", "--json"], ["attention", "list", "--json", "--summary"]];

    private static string[][] AttentionForms() =>
        [["attention", "--json"], .. SummaryForms()];

    private static string RunAttention(string[] args, Fixture fixture, QueryProbe probe) =>
        CaptureConsole(() => CliAttentionJsonQueryCommand.Execute(args, probe, fixture.Workspace, new FixedTime()));

    private static void AssertSummaryMatches(string full, string summary)
    {
        using var ordinary = JsonDocument.Parse(full);
        using var compact = JsonDocument.Parse(summary);
        var envelope = compact.RootElement;
        Assert.Equal(new[] { "schema", "schemaVersion", "generatedAt", "omittedSections", "payload" },
            envelope.EnumerateObject().Select(property => property.Name));
        Assert.Equal("mcg.attention-summary", envelope.GetProperty("schema").GetString());
        Assert.Equal(1, envelope.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(ordinary.RootElement.GetProperty("generatedAt").GetString(), envelope.GetProperty("generatedAt").GetString());
        Assert.Equal("gateEvidence", Assert.Single(envelope.GetProperty("omittedSections").EnumerateArray()).GetString());
        var payload = envelope.GetProperty("payload");
        var fields = new[] { "waits", "items", "providerHolds", "ownerQuestionHolds" };
        if (ordinary.RootElement.GetProperty("payload").TryGetProperty("unlinkedGoals", out _))
            fields = [.. fields, "unlinkedGoals"];
        Assert.Equal(fields,
            payload.EnumerateObject().Select(property => property.Name));
        foreach (var section in payload.EnumerateObject())
            Assert.Equal(ordinary.RootElement.GetProperty("payload").GetProperty(section.Name).GetRawText(), section.Value.GetRawText());
    }

    private static void AssertJsonEqual(string expected, string actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)), "Attention envelopes differ");
    private static string Render(AttentionJsonReadContext context) => AttentionAgentReadView.Render(context.Waits,
        context.Items, new FixedTime(), context.ProviderHolds, gateEvidence: context.GateEvidence, ownerQuestions: context.OwnerQuestions,
        unlinkedGoals: UnlinkedGoalAttentionView.Rows(context.GoalFacts, ""));
    private static string Id(int index) => $"{index:x8}aaaaaaaaaaaaaaaaaaaaaaaa";
    private static PlannerEvidenceIdentity Identity(GoalSnapshot goal, int version) => new(goal.Id, goal.Tasks[0].Id, version, 0, "wait");
    private static GateTestObligation Gate(int version) => new("gate-" + version, version, ["Selector"], null, "Pending", Now);
    private static GoalHoldSnapshot Hold(string state, string blocker) => new("hold-" + state, state, blocker, Now);
    private sealed class FixedTime : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }

    private static GoalSnapshot WithArchiveEvidence(GoalSnapshot goal, string section)
    {
        var terminal = goal with { Status = GoalStatus.Completed, TerminatedAt = Now };
        return section switch
        {
            "gateEvidence" => terminal with { GateTestObligations = [Gate(1)] },
            "postLandingDuties" => WithPendingDuty(terminal),
            "providerHolds" => terminal with { Tasks = [goal.Tasks[0] with
            {
                LastDispatch = new("claude-cli", "claude", "fixture", Now.AddHours(-2),
                    ProviderName: "Claude", ClaudeCredentialSourceDirectory: "account-a"),
                LastVerification = new("claude", "fixture", 1, "", "You've hit your usage limit", Now.AddHours(-1),
                    StandardErrorPath: "receipt", ProviderFailureKind: ProviderFailureKind.BudgetExhausted)
            }] },
            _ => throw new ArgumentOutOfRangeException(nameof(section))
        };
    }

    private static GoalSnapshot WithPendingDuty(GoalSnapshot goal)
    {
        const string criterion = "Observe the promoted fixture";
        var scope = new CriterionObservationScope(CriterionObservationKind.RepositoryState, "Fixture published", "main");
        var spec = new RefinedSpecSnapshot("Publish fixture", [criterion], nameof(VerificationClass.RealWorldDependent), [], [],
            OperatorOwnedAcceptanceCriteria: [criterion],
            CriterionEvidenceDeclarations: [new(criterion, CriterionEvidencePhase.PostLanding, scope, Atomic: true)]);
        var revision = new string('a', 40);
        var publication = new GoalPromotionFact(goal.Id, revision, new string('b', 40), revision, Now,
            GoalPromotionFact.SoloProvenance);
        return goal with
        {
            RefinedSpecVersions = [new(1, spec, Now.AddDays(-1), 1)],
            CriterionEvidenceObligations =
            [
                new(CriterionEvidenceObligation.BuildId(1, 0), 0, 1, criterion, CriterionEvidenceOwner.Operator,
                    CriterionEvidenceState.Pending, "fixture observation", "fixture", Now.AddDays(-1),
                    ExpectedCandidateSha: revision, Phase: CriterionEvidencePhase.PostLanding, ObservationScope: scope,
                    Publication: publication)
            ]
        };
    }

    private sealed class QueryProbe(IOrchestratorStateQueries inner) : IOrchestratorStateQueries
    {
        public List<string[]> Loads { get; } = [];
        public int BoardLoads { get; private set; }
        public int MetadataLoads { get; private set; }
        public bool Unsupported { get; init; }
        public Exception? Failure { get; init; }
        public Action? BeforeLoad { get; init; }
        public AttentionReadProjection? LastProjection { get; private set; }
        public async Task<AttentionReadProjection?> ReadAttentionProjectionAsync(CancellationToken cancellationToken = default)
            => await ReadAttentionProjectionAsync(new AttentionProjectionReadOptions(), cancellationToken);
        public async Task<AttentionReadProjection?> ReadAttentionProjectionAsync(AttentionProjectionReadOptions options,
            CancellationToken cancellationToken)
        {
            if (Failure is not null) throw Failure;
            return LastProjection = Unsupported ? null : await inner.ReadAttentionProjectionAsync(options, cancellationToken);
        }
        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default)
        { MetadataLoads++; return inner.ListGoalMetadataAsync(cancellationToken); }
        public Task<IReadOnlyList<HumanInputRequestSnapshot>> ListOpenHumanInputRequestsAsync(CancellationToken cancellationToken = default) =>
            inner.ListOpenHumanInputRequestsAsync(cancellationToken);
        public Task<AgentOrchestratorKernel> LoadGoalsAsync(IReadOnlyCollection<GoalId> goalIds, CancellationToken cancellationToken = default)
        { Loads.Add(goalIds.Select(id => id.Value).ToArray()); BeforeLoad?.Invoke(); return inner.LoadGoalsAsync(goalIds, cancellationToken); }
        public Task<AgentOrchestratorKernel> LoadBoardSnapshotAsync(CancellationToken cancellationToken = default)
        { BoardLoads++; throw new InvalidOperationException("Whole board must not load"); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = CreateTempDirectory();
        public OrchestratorWorkspace Workspace { get; }
        public GoalSnapshot[] Goals { get; private set; } = [];
        public IOrchestratorStateQueries Queries => SqliteOrchestratorStateRepository.OpenReadOnly(Workspace.SqliteStatePath);
        public CollaborationItemStore Store { get; }
        private Fixture()
        {
            Workspace = OrchestratorWorkspace.ForDirectory(_root);
            StateDbMigrations.EnsureUpToDate(Workspace.SqliteStatePath);
            Store = CollaborationItemStore.ForDirectory(Workspace.OrchestratorDirectory);
        }
        public static Task<Fixture> CreateAsync(int count)
        {
            var kernel = new AgentOrchestratorKernel();
            for (var index = 0; index < count; index++) kernel.CreateGoal(new GoalId(Id(index + 1)), "Goal " + index);
            return CreateAsync(kernel);
        }

        public static Task<Fixture> CreateAsync(GoalSnapshot[] goals) =>
            CreateAsync(AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(goals, [])));

        private static async Task<Fixture> CreateAsync(AgentOrchestratorKernel kernel)
        {
            var fixture = new Fixture { Goals = kernel.ExportSnapshot().Goals.ToArray() };
            await new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath).SaveAsync(kernel);
            return fixture;
        }

        public IOrchestratorStateQueries OpenObservedQueries(List<GoalSnapshotReadObservation> reads) =>
            SqliteOrchestratorStateRepository.OpenReadOnly(Workspace.SqliteStatePath, reads.Add);

        public Task WriteAttentionMarkersAsync() =>
            new SqliteOrchestratorStateRepository(Workspace.SqliteStatePath).WriteAttentionArchiveEvidenceMarkersAsync();

        public async Task SeedMarkedHistoryAsync()
        {
            await WriteGoalAsync(Goals[0] with { Status = GoalStatus.Active });
            await WriteWaitAsync(new("fixed-wait", Goals[0].Id, Goals[0].Tasks[0].Id, "Choose?", Now));
            await WriteGoalAsync(WithArchiveEvidence(Goals[1], "gateEvidence"), archive: true, compress: true);
            await WriteGoalAsync(WithArchiveEvidence(Goals[2], "postLandingDuties"), archive: true, compress: true);
            await WriteGoalAsync(WithArchiveEvidence(Goals[3], "providerHolds"), archive: true, compress: true);
            foreach (var goal in Goals.Skip(4))
                await WriteGoalAsync(goal with { Status = GoalStatus.Completed, TerminatedAt = Now }, archive: true, compress: true);
            await WriteAttentionMarkersAsync();
        }
        public async Task WriteGoalAsync(GoalSnapshot snapshot, bool archive = false, bool compress = false)
        {
            await using var connection = new SqliteConnection($"Data Source={Workspace.SqliteStatePath};Pooling=False");
            await connection.OpenAsync();
            if (archive)
            {
                await using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO goal_snapshot_archive (goal_id, snapshot_json, archived_at) VALUES ($id, $json, $at)";
                insert.Parameters.AddWithValue("$id", snapshot.Id);
                var json = JsonSerializer.Serialize(snapshot, Options);
                insert.Parameters.AddWithValue("$json", compress ? (object)GoalSnapshotArchiveCodec.Encode(json) : json);
                insert.Parameters.AddWithValue("$at", Now.ToString("O"));
                Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            }
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE goals SET status = $status, snapshot_json = $json WHERE id = $id";
            update.Parameters.AddWithValue("$id", snapshot.Id);
            update.Parameters.AddWithValue("$status", snapshot.Status.ToString());
            update.Parameters.AddWithValue("$json", JsonSerializer.Serialize(archive ? snapshot with
                { IsMetadataOnly = true, Tasks = [], Timeline = [], GateTestObligations = null, CurrentHold = null, BriefVersions = null,
                    RefinedSpecVersions = null, CriterionEvidenceObligations = null } : snapshot, Options));
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }
        public async Task WriteWaitAsync(HumanInputRequestSnapshot wait)
        {
            await using var connection = new SqliteConnection($"Data Source={Workspace.SqliteStatePath};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO human_input_requests (id, goal_id, snapshot_json) VALUES ($id, $goal, $json)";
            command.Parameters.AddWithValue("$id", wait.Id);
            command.Parameters.AddWithValue("$goal", wait.GoalId);
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(wait, Options));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        public async Task<string> BaselineAsync(bool summary = false)
        {
            var metadata = await Queries.ListGoalMetadataAsync();
            var kernel = await Queries.LoadGoalsAsync(metadata.Select(goal => new GoalId(goal.Id)).ToArray());
            var items = await Store.GetAttentionQueueAsync();
            var waits = CliCommandHandlers.HumanWaitsForAttention(kernel, null, false);
            var actionableItems = CliCommandHandlers.CollaborationItemsForAttention(items, kernel, null, false);
            var providerHolds = ProviderUsageHoldView.Read(kernel.Goals, Now);
            var unlinkedGoals = UnlinkedGoalAttentionView.Rows(kernel.Goals.Select(goal => new AttentionGoalReadFacts(
                    goal.Id.Value, goal.Status, goal.AuthoritativeBrief.Version,
                    SourceBacklogItemId: goal.SourceBacklogItemId, Title: goal.Objective)).ToArray(), Workspace.PortfolioStorePath);
            if (!summary)
                return AttentionAgentReadView.Render(waits, actionableItems, new FixedTime(), providerHolds, kernel.Goals,
                    unlinkedGoals: unlinkedGoals);

            var ownerQuestions = kernel.Goals
                .Where(goal => goal.Status is not (GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Superseded))
                .Select(OwnerQuestionHoldBlocker.TryRead).OfType<OwnerQuestionHoldBlocker>().ToArray();
            var duties = kernel.Goals.SelectMany(goal => goal.OutstandingPostLandingDuties
                .Select(duty => new AttentionPostLandingDuty(goal.Id.Value, duty))).ToArray();
            return AttentionAgentReadView.RenderSummary(waits, actionableItems, new FixedTime(), providerHolds, ownerQuestions,
                duties, kernel.Goals, unlinkedGoals: unlinkedGoals);
        }
        public void Dispose() => Directory.Delete(_root, true);
    }
}
