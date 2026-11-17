using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

public sealed class DispatchSnapshotShapingTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GoalSnapshot_MatchingBrief_SummarizesOnlyHistoricalReceipts()
    {
        var goal = CreateGoal(3, "The exact version text.");
        var task = Assert.Single(goal.Tasks);
        var original = task.ToSnapshot();
        var originalJson = JsonSerializer.Serialize(original);

        var stored = Assert.Single(goal.ToSnapshot().Tasks);

        Assert.Equal(3, stored.DispatchHistory!.Count);
        for (var index = 0; index < stored.DispatchHistory.Count; index++)
        {
            var before = original.DispatchHistory![index];
            var after = stored.DispatchHistory[index];
            Assert.Null(after.BriefSnapshot);
            Assert.True(after.BriefSnapshotFromVersion);
            if (index < stored.DispatchHistory.Count - 1)
            {
                var summary = Assert.IsType<WorkerContextPackageReceipt>(after.ContextPackageReceipt);
                Assert.Empty(summary.Sections);
                Assert.Equal(before.ContextPackageReceipt!.Sections.Count, summary.SectionCount);
                Assert.Equal(JsonSerializer.Serialize(before.ContextPackageReceipt), JsonSerializer.Serialize(
                    summary with { Sections = before.ContextPackageReceipt.Sections, SectionCount = null }));
            }
            else
            {
                Assert.Same(before.ContextPackageReceipt, after.ContextPackageReceipt);
            }

            Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after with
            {
                BriefSnapshot = before.BriefSnapshot,
                BriefSnapshotFromVersion = null,
                ContextPackageReceipt = before.ContextPackageReceipt
            }));
        }

        Assert.Same(task.LastDispatch!.ContextPackageReceipt, stored.LastDispatch!.ContextPackageReceipt);
        Assert.Null(stored.LastDispatch.BriefSnapshot);
        Assert.True(stored.LastDispatch.BriefSnapshotFromVersion);
        Assert.Equal(JsonSerializer.Serialize(stored.DispatchHistory[^1]), JsonSerializer.Serialize(stored.LastDispatch));
        Assert.Equal(originalJson, JsonSerializer.Serialize(task.ToSnapshot()));
    }

    [Theory]
    [InlineData("version", 1, true)]
    [InlineData("Version", 1, false)]
    [InlineData("version ", 1, false)]
    [InlineData("version\n", 1, false)]
    [InlineData("version", 2, false)]
    [InlineData(null, 1, false)]
    public void ForStorage_OrdinalMatch_CompactsOnlyKnownNonNullText(
        string? brief, int version, bool compacted)
    {
        var original = Dispatch(brief, version);
        var shaped = DispatchSnapshotShaping.ForStorage(original, true,
            new Dictionary<int, string> { [1] = "version" });

        Assert.Equal(compacted ? null : brief, shaped.BriefSnapshot);
        Assert.Equal(compacted ? true : (bool?)null, shaped.BriefSnapshotFromVersion);
        Assert.Equal(original, shaped with { BriefSnapshot = brief, BriefSnapshotFromVersion = null });
    }

    [Fact]
    public void GoalSnapshot_HistoryOrder_KeepsFinalReceiptFull()
    {
        var goal = CreateGoal(3, "version");
        var task = goal.Tasks[0].ToSnapshot();
        var entries = task.DispatchHistory!;
        var input = goal.ToSnapshot() with
        {
            Tasks = [task with
            {
                DispatchHistory = [entries[2], entries[0], entries[1]],
                LastDispatch = entries[1]
            }]
        };

        var stored = Assert.Single(Goal.FromSnapshot(input).ToSnapshot().Tasks);

        Assert.Equal(new[] { entries[2].DispatchedAt, entries[0].DispatchedAt, entries[1].DispatchedAt },
            stored.DispatchHistory!.Select(dispatch => dispatch.DispatchedAt));
        Assert.Empty(stored.DispatchHistory[0].ContextPackageReceipt!.Sections);
        Assert.Empty(stored.DispatchHistory[1].ContextPackageReceipt!.Sections);
        Assert.Equal(4, stored.DispatchHistory[2].ContextPackageReceipt!.Sections.Count);
        Assert.Null(stored.DispatchHistory[2].ContextPackageReceipt!.SectionCount);
        Assert.Equal(JsonSerializer.Serialize(stored.DispatchHistory[2]), JsonSerializer.Serialize(stored.LastDispatch));
    }

    [Fact]
    public void ForStorage_EmptyVersionText_CompactsEmptyButPreservesNull()
    {
        var versions = new Dictionary<int, string> { [1] = "" };
        var compacted = DispatchSnapshotShaping.ForStorage(Dispatch(""), true, versions);
        var absent = DispatchSnapshotShaping.ForStorage(Dispatch(null), true, versions);

        Assert.Null(compacted.BriefSnapshot);
        Assert.True(compacted.BriefSnapshotFromVersion);
        Assert.Null(absent.BriefSnapshot);
        Assert.Null(absent.BriefSnapshotFromVersion);
    }

    [Theory]
    [InlineData(0, null, 0)]
    [InlineData(0, 7, 7)]
    [InlineData(2, null, 2)]
    [InlineData(2, 7, 2)]
    public void ForStorage_HistoricalReceipt_PreservesOriginalCountAcrossSaves(
        int sections, int? recordedCount, int expectedCount)
    {
        var original = Dispatch("version") with
        {
            ContextPackageReceipt = Receipt(sections) with { SectionCount = recordedCount }
        };
        var versions = new Dictionary<int, string> { [1] = "version" };
        var first = DispatchSnapshotShaping.ForStorage(original, false, versions);
        var second = DispatchSnapshotShaping.ForStorage(first, false, versions);

        Assert.Empty(first.ContextPackageReceipt!.Sections);
        Assert.Equal(expectedCount, first.ContextPackageReceipt.SectionCount);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
    }

    [Fact]
    public void ForStorage_NullReceipt_RemainsNull()
    {
        var stored = DispatchSnapshotShaping.ForStorage(Dispatch(null), false, new Dictionary<int, string>());
        Assert.Null(stored.ContextPackageReceipt);
    }

    [Theory]
    [InlineData(null, true, 1, "version")]
    [InlineData(null, null, 1, null)]
    [InlineData(null, false, 1, null)]
    [InlineData(null, true, 2, null)]
    [InlineData("inline wins", true, 1, "inline wins")]
    public void FromStorage_Marker_ResolvesOnlyCompactedNulls(
        string? inline, bool? marker, int version, string? expected)
    {
        var original = Dispatch(inline, version) with { BriefSnapshotFromVersion = marker };
        var restored = DispatchSnapshotShaping.FromStorage(original,
            new Dictionary<int, string> { [1] = "version" });

        Assert.Equal(expected, restored.BriefSnapshot);
        Assert.Equal(original, restored with
        {
            BriefSnapshot = original.BriefSnapshot,
            BriefSnapshotFromVersion = original.BriefSnapshotFromVersion
        });
    }

    [Fact]
    public void BriefLookup_AmbiguousOrMissingVersions_PreservesInlineText()
    {
        var versions = DispatchSnapshotShaping.BriefLookup([
            new GoalBriefVersion(1, "first", Start), new GoalBriefVersion(1, "second", Start)]);
        var original = Dispatch("first");

        Assert.Empty(versions);
        Assert.Same(original, DispatchSnapshotShaping.ForStorage(original, true, versions));
        Assert.Empty(DispatchSnapshotShaping.BriefLookup(null));
    }

    [Fact]
    public void LegacyJson_AbsentOptionalFields_DefaultToNullAndStayOmitted()
    {
        const string json = """
            {"WorkerName":"worker","Command":"command","WorkingDirectory":"root","DispatchedAt":"2026-11-17T00:00:00Z"}
            """;
        var dispatch = JsonSerializer.Deserialize<TaskDispatchSnapshot>(json)!;
        var receipt = JsonSerializer.Deserialize<WorkerContextPackageReceipt>(JsonSerializer.Serialize(Receipt(1)))!;

        Assert.Null(dispatch.BriefSnapshotFromVersion);
        Assert.DoesNotContain("BriefSnapshotFromVersion", JsonSerializer.Serialize(dispatch));
        Assert.Null(receipt.SectionCount);
        Assert.DoesNotContain("SectionCount", JsonSerializer.Serialize(receipt));
    }

    internal static Goal CreateGoal(int dispatchCount, string brief)
    {
        var task = new TaskSpec(new TaskId("shaping-task"), "Work", AgentRole.Developer);
        for (var index = 0; index < dispatchCount; index++)
        {
            task.RecordDispatch(new TaskDispatchRecord("worker", "command", "root", Start.AddMinutes(index),
                ProviderName: "provider", ModelName: "model", ReasoningEffort: "high",
                PromptCharacterCount: 1234, BaseCommit: "base", ResultCommit: "result",
                BriefVersion: 1, BriefSnapshot: brief, ContextPackageReceipt: Receipt(4),
                PlannerSampleCount: 2));
        }

        task.RecordVerification(new TaskVerificationRecord("command", "root", 0,
            "verification output", "verification diagnostics", Start.AddHours(1),
            WorkerResultPresent: true, FullStandardOutput: "verification output",
            FullStandardError: "verification diagnostics"));

        return new Goal(new GoalId("shaping-goal"), brief, [task]);
    }

    private static TaskDispatchSnapshot Dispatch(string? brief, int version = 1) =>
        new("worker", "command", "root", Start, BriefVersion: version, BriefSnapshot: brief);

    private static WorkerContextPackageReceipt Receipt(int sectionCount) => new(
        "ctxpkg-v1-sha256:" + new string('a', 64),
        Enumerable.Range(0, sectionCount).Select(index => new WorkerContextSectionReceipt(
            $"context/section-{index}.md", 800, 900, new string('b', 64),
            ContextDeliveryMode.OnDemandFile, 1, [AgentRole.Developer],
            MandatoryRelativePath: $"context/section-{index}.md")).ToArray(),
        ProviderUsageValue.Reported(1000), ProviderUsageValue.Reported(200), ProviderUsageValue.Reported(30),
        RenderedPromptBytes: 5000, UniqueReviewFindingRoundCount: 3, DuplicateReviewFindingRoundCount: 2,
        UniqueFindingEvidenceReceiptCount: 1, DuplicateFindingEvidenceReceiptCount: 4,
        ReviewFindingFallbackReason: "fixture", RenderedPromptCharacters: 4500,
        DeliveredArtifactBytes: 900, OnDemandArtifactBytes: 3600, ToolTranscriptCharacters: 600,
        ModelInputTokenEstimate: 1200, BaselinePromptCharacters: 6000, BaselineDeliveredArtifactBytes: 2700,
        BaselineToolTranscriptCharacters: 1800, BaselineModelInputTokenEstimate: 2400);
}
