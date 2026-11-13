using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Xunit;
using static Mcg.AgentOrchestrator.App.Orchestration.PublicHistoryFoldBuilder;

// Parallel-safe: all fixtures and results are deterministic, test-owned in-memory values.
public sealed class PublicHistoryFoldBuilderTests
{
    [Fact]
    public void Build_ShuffledInputs_ProduceIdenticalSerializedPlan()
    {
        var tied = Make(4, "tied") with { SourceReference = "source-b" };
        PublicHistoryBuild[] input = [Make(0, "rolled", outcome: PublicHistoryBuildOutcome.Skipped),
            Make(1, "other", "repo-b"),
            Make(2, "cancelled", outcome: PublicHistoryBuildOutcome.Cancelled) with { BuildTime = At(99) },
            Make(3, "terminal"), tied, tied with { SourceReference = "source-a" },
            tied with { CommitTime = At(4).ToOffset(TimeSpan.FromHours(2)) },
            Make(5, "last"), Make(6, "unresolved", outcome: PublicHistoryBuildOutcome.Skipped)];
        var plan = Build(input, 3);
        Assert.Empty(plan.Diagnostics);
        Assert.Equal(3, plan.Folds.Length);
        var text = JsonSerializer.Serialize(plan);
        Assert.Contains("sha-rolled", text);
        Assert.Contains("\"Index\":2", text);
        Assert.Contains("sha-unresolved", text);
        var expected = Encoding.UTF8.GetBytes(text);
        Assert.Equal(expected, JsonSerializer.SerializeToUtf8Bytes(Build(input.Reverse().ToArray(), 3)));
        var random = new Random(739);
        for (var i = 0; i < 10; i++)
        {
            var shuffled = input.ToArray();
            random.Shuffle(shuffled);
            Assert.Equal(expected, JsonSerializer.SerializeToUtf8Bytes(Build(shuffled, 3)));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void Build_PrimaryKeysTie_ContentAndOffsetsBreakTies(int field)
    {
        var first = Make(0, "same");
        var second = field switch
        {
            0 => first with { Repository = "repo-b" },
            1 => first with { CommitSha = "different" },
            2 => first with { StackLabel = "other-stack" },
            3 => first with { Outcome = PublicHistoryBuildOutcome.Failed },
            4 => first with { SourceReference = "other-source" },
            5 => new(first.Repository, first.StackLabel, first.CommitSha, first.CommitTime,
                first.BuildId, first.BuildTime, first.Outcome, ["test-a", "test-b"], first.SourceReference),
            6 => first with { CommitTime = first.CommitTime.ToOffset(TimeSpan.FromHours(2)) },
            _ => first with { BuildTime = first.BuildTime.ToOffset(TimeSpan.FromHours(-2)) }
        };
        var forward = Build([first, second], 1);
        var reverse = Build([second, first], 1);
        Assert.Empty(forward.Diagnostics);
        Assert.Equal(2, Assert.Single(forward.Folds).Units.Length);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(forward), JsonSerializer.SerializeToUtf8Bytes(reverse));
    }

    [Fact]
    public void Build_ValidHistory_TrainingBuildsStrictlyEarlier()
    {
        var plan = Build(Enumerable.Range(0, 7).Select(i => Make(i, $"b{i}")).ToArray(), 3);
        Assert.Empty(plan.Diagnostics);
        Assert.Equal(3, plan.Folds.Length);
        Assert.Empty(plan.Folds[0].TrainingFoldIndexes);
        foreach (var fold in plan.Folds)
        {
            Assert.Equal(Enumerable.Range(0, fold.Index), fold.TrainingFoldIndexes);
            Assert.Equal(fold.Units.Min(u => u.TerminalBuild.BuildTime), fold.EarliestBuildTime);
            Assert.Equal(fold.Units.Max(u => u.TerminalBuild.BuildTime), fold.LatestBuildTime);
            foreach (var trainingIndex in fold.TrainingFoldIndexes)
                foreach (var training in plan.Folds[trainingIndex].Units)
                    foreach (var evaluation in fold.Units)
                        Assert.True(training.TerminalBuild.BuildTime < evaluation.TerminalBuild.BuildTime);
        }
    }

    [Fact]
    public void Validate_TrainingBuildLaterThanFold_RejectsWithDiagnostic()
    {
        var plan = Build([Make(0, "early"), Make(1, "middle"), Make(2, "late")], 3);
        Assert.Empty(plan.Diagnostics);
        Assert.Equal(3, plan.Folds.Length);
        Assert.Empty(Validate(plan.Folds));
        var tampered = plan.Folds.SetItem(1, plan.Folds[1] with { TrainingFoldIndexes = [0, 2] });

        var errors = Validate(tampered);

        var diagnostic = Assert.Single(errors, e => e.Code == "training-time-not-earlier");
        Assert.Equal("late", diagnostic.BuildId);
        Assert.Equal("repo-a", diagnostic.Repository);
        Assert.Equal(1, diagnostic.FoldIndex);
        Assert.Equal(2, diagnostic.EarlierFoldIndex);
        Assert.Equal(At(2), diagnostic.BuildTime);
        Assert.Equal(At(1), diagnostic.BoundaryTime);
        Assert.Equal("middle", diagnostic.BoundaryBuildId);
        Assert.Contains("strictly earlier", diagnostic.Message);
    }

    [Fact]
    public void Build_BuildTimeBeforeEarlierFold_RejectsPlan()
    {
        var plan = Build([Make(0, "training") with { BuildTime = At(5) }, Make(1, "evaluation")], 2);
        Assert.Empty(plan.Folds);
        var diagnostic = Assert.Single(plan.Diagnostics);
        Assert.Equal("training-time-not-earlier", diagnostic.Code);
        Assert.Equal("training", diagnostic.BuildId);
        Assert.Equal("evaluation", diagnostic.BoundaryBuildId);
        Assert.Equal(0, diagnostic.EarlierFoldIndex);
        Assert.Equal(1, diagnostic.FoldIndex);
        Assert.Equal(At(5), diagnostic.BuildTime);
        Assert.Equal(At(1), diagnostic.BoundaryTime);
    }

    [Theory]
    [InlineData((int)PublicHistoryBuildOutcome.Passed)]
    [InlineData((int)PublicHistoryBuildOutcome.Failed)]
    [InlineData((int)PublicHistoryBuildOutcome.Unknown)]
    public void Build_SkippedAndCancelled_RollIntoNextEligibleBuild(int terminalOutcome)
    {
        // The parameter is integral because the product enum is internal to the App assembly.
        var skipped = Make(0, "skipped", outcome: PublicHistoryBuildOutcome.Skipped);
        var cancelled = Make(1, "cancelled", outcome: PublicHistoryBuildOutcome.Cancelled);
        var terminal = Make(2, "terminal", outcome: (PublicHistoryBuildOutcome)terminalOutcome);
        var plan = Build([terminal, cancelled, skipped], 1);
        Assert.Empty(plan.Diagnostics);
        Assert.Empty(plan.Unresolved);
        var unit = Assert.Single(Assert.Single(plan.Folds).Units);
        Assert.Equal(terminal.BuildId, unit.TerminalBuild.BuildId);
        Assert.Equal(new[] { "skipped", "cancelled" }, unit.RolledBuilds.Select(b => b.BuildId));
        Assert.Equal(new[] { "sha-skipped", "sha-cancelled", "sha-terminal" }, unit.Commits);
    }

    [Fact]
    public void Build_InterleavedRepositories_NeverMergeAcrossRepositories()
    {
        var plan = Build([Make(0, "a-skip", outcome: PublicHistoryBuildOutcome.Skipped),
            Make(1, "b", "repo-b"), Make(2, "case", "REPO-A"),
            Make(3, "a") with { StackLabel = "changed-stack" }], 1);
        Assert.Empty(plan.Diagnostics);
        var units = Assert.Single(plan.Folds).Units;
        Assert.Equal(3, units.Length);
        Assert.Equal(new[] { "sha-b" }, units[0].Commits);
        Assert.Equal(new[] { "sha-case" }, units[1].Commits);
        Assert.Equal(new[] { "sha-a-skip", "sha-a" }, units[2].Commits);
        Assert.Equal("repo-a", units[2].Repository);
        Assert.Equal("REPO-A", units[1].Repository);
    }

    [Fact]
    public void Build_TrailingSkippedRun_ReportedUnresolved()
    {
        PublicHistoryBuild[] input = [Make(0, "passed"),
            Make(1, "skipped", outcome: PublicHistoryBuildOutcome.Skipped),
            Make(2, "cancelled", outcome: PublicHistoryBuildOutcome.Cancelled),
            Make(3, "other", "repo-b", PublicHistoryBuildOutcome.Skipped)];
        var plan = Build(input, 1);
        Assert.Empty(plan.Diagnostics);
        Assert.Equal(2, plan.Unresolved.Length);
        Assert.Equal(new[] { "repo-a", "repo-b" }, plan.Unresolved.Select(u => u.Repository));
        Assert.Equal(new[] { "skipped", "cancelled" }, plan.Unresolved[0].Builds.Select(b => b.BuildId));
        Assert.Equal(new[] { "sha-skipped", "sha-cancelled" }, plan.Unresolved[0].Commits);
        Assert.Equal(new[] { "sha-other" }, plan.Unresolved[1].Commits);
        Assert.Equal("passed", Assert.Single(Assert.Single(plan.Folds).Units).TerminalBuild.BuildId);
        Assert.Equal(input.Length, plan.Folds.SelectMany(f => f.Units).Sum(u => u.RolledBuilds.Length + 1)
            + plan.Unresolved.Sum(u => u.Builds.Length));
    }

    [Fact]
    public void Build_RolledBuildTimes_UsesOnlyTerminalTimeForTraining()
    {
        var rolled = Make(1, "rolled", outcome: PublicHistoryBuildOutcome.Cancelled) with { BuildTime = At(99) };
        var plan = Build([Make(0, "early"), rolled, Make(2, "terminal"), Make(3, "late")], 3);
        Assert.Empty(plan.Diagnostics);
        Assert.Equal(3, plan.Folds.Length);
        var fold = plan.Folds[1];
        Assert.Equal(At(2), fold.EarliestBuildTime);
        Assert.Equal(At(2), fold.LatestBuildTime);
        Assert.Equal(At(99), Assert.Single(Assert.Single(fold.Units).RolledBuilds).BuildTime);
    }

    [Fact]
    public void Build_UnevenUnitCount_EarliestFoldsTakeExtra()
    {
        var plan = Build(Enumerable.Range(0, 8).Select(i => Make(i, $"b{i}")).ToArray(), 3);
        Assert.Empty(plan.Diagnostics);
        Assert.Equal(new[] { 3, 3, 2 }, plan.Folds.Select(f => f.Units.Length));
        Assert.Equal(Enumerable.Range(0, 8).Select(i => $"b{i}"),
            plan.Folds.SelectMany(f => f.Units).Select(u => u.TerminalBuild.BuildId));
    }

    [Fact]
    public void Build_EqualBuildTimesAtCut_MoveBoundaryForward()
    {
        var plan = Build([Make(0, "a"), Make(1, "b"),
            Make(2, "c") with { BuildTime = At(1).ToOffset(TimeSpan.FromHours(2)) }, Make(3, "d")], 2);
        Assert.Empty(plan.Diagnostics);
        Assert.Equal(new[] { 3, 1 }, plan.Folds.Select(f => f.Units.Length));
        Assert.Equal(new[] { "a", "b", "c" }, plan.Folds[0].Units.Select(u => u.TerminalBuild.BuildId));
        Assert.Equal("d", Assert.Single(plan.Folds[1].Units).TerminalBuild.BuildId);
    }

    [Fact]
    public void Build_TiesWouldStarveLaterFold_KeepsRequestedFoldCount()
    {
        var plan = Build(Enumerable.Range(0, 6).Select(i => Make(i, $"b{i}")
            with { BuildTime = At(Math.Min(i, 2)) }).ToArray(), 3);
        Assert.Empty(plan.Diagnostics);
        Assert.Equal(new[] { 1, 1, 4 }, plan.Folds.Select(f => f.Units.Length));
        Assert.Equal(new[] { At(0), At(1), At(2) }, plan.Folds.Select(f => f.EarliestBuildTime));
    }

    [Theory]
    [InlineData(0, "fold-count-invalid", "3 resolved units")]
    [InlineData(-1, "fold-count-invalid", "3 resolved units")]
    [InlineData(4, "fold-count-exceeds-units", "3 resolved units")]
    [InlineData(3, "fold-count-exceeds-time-groups", "2 available time groups")]
    public void Build_FoldCountOutOfRange_RejectsNamingBothNumbers(int count, string code, string available)
    {
        var plan = Build([Make(0, "a"), Make(1, "b"), Make(2, "c") with { BuildTime = At(1) }], count);
        Assert.Empty(plan.Folds);
        var diagnostic = Assert.Single(plan.Diagnostics);
        Assert.Equal(code, diagnostic.Code);
        Assert.Contains($"Requested {count} folds", diagnostic.Message);
        Assert.Contains(available, diagnostic.Message);
    }

    [Fact]
    public void Build_EmptyOrOnlyUnresolved_ReturnsNoFolds()
    {
        var empty = Build([], 1);
        Assert.Empty(empty.Folds);
        Assert.Empty(empty.Unresolved);
        Assert.Empty(empty.Diagnostics);
        var pending = Build([Make(0, "pending", outcome: PublicHistoryBuildOutcome.Skipped)], 4);
        Assert.Empty(pending.Folds);
        Assert.Empty(pending.Diagnostics);
        Assert.Equal("sha-pending", Assert.Single(Assert.Single(pending.Unresolved).Commits));
        Assert.Equal("fold-count-invalid", Assert.Single(Build([], 0).Diagnostics).Code);
    }

    [Fact]
    public void Build_CanonicalKeys_OrderCommitThenBuildThenOrdinalId()
    {
        var early = Make(0, "early") with { BuildTime = At(4) };
        var ten = Make(1, "10");
        var two = Make(1, "2");
        var later = Make(1, "0") with { BuildTime = At(2) };
        var plan = Build([later, two, ten, early], 1);
        Assert.Empty(plan.Diagnostics);
        Assert.Equal(new[] { "early", "10", "2", "0" },
            Assert.Single(plan.Folds).Units.Select(u => u.TerminalBuild.BuildId));
    }

    [Fact]
    public void Build_RepeatedRecordsAndShas_PreservesEveryOccurrence()
    {
        var pending = Make(0, "same", outcome: PublicHistoryBuildOutcome.Skipped);
        var terminal = Make(1, "terminal") with { CommitSha = pending.CommitSha };
        var plan = Build([pending, terminal, pending, terminal], 1);
        Assert.Empty(plan.Diagnostics);
        var units = Assert.Single(plan.Folds).Units;
        Assert.Equal(2, units.Length);
        Assert.Equal(new[] { "sha-same", "sha-same", "sha-same" }, units[0].Commits);
        Assert.Equal(new[] { "sha-same" }, units[1].Commits);
    }

    [Fact]
    public void Build_CallerMutatesLists_RecordAndResultRemainUnchanged()
    {
        var failingTests = new List<string> { "suite.case" };
        var record = new PublicHistoryBuild("repo-a", "vitest", "sha", At(0), "b", At(0),
            PublicHistoryBuildOutcome.Failed, failingTests, "fixture");
        var input = new List<PublicHistoryBuild> { record };
        var plan = Build(input, 1);
        Assert.Empty(plan.Diagnostics);
        var before = JsonSerializer.SerializeToUtf8Bytes(plan);
        failingTests[0] = "changed";
        input.Clear();
        Assert.Equal(new[] { "suite.case" }, record.FailingTests);
        Assert.Equal(before, JsonSerializer.SerializeToUtf8Bytes(plan));
        Assert.Equal("sha", Assert.Single(Assert.Single(Assert.Single(plan.Folds).Units).Commits));
    }

    [Theory]
    [InlineData("record")]
    [InlineData("Repository")]
    [InlineData("StackLabel")]
    [InlineData("CommitSha")]
    [InlineData("BuildId")]
    [InlineData("SourceReference")]
    [InlineData("FailingTests")]
    [InlineData("test-entry")]
    [InlineData("Outcome")]
    public void Build_MalformedRecord_ReportsDiagnosticWithoutFolds(string field)
    {
        var valid = Make(0, "valid");
        var invalid = field switch
        {
            "record" => null!,
            "Repository" => valid with { Repository = " " },
            "StackLabel" => valid with { StackLabel = null! },
            "CommitSha" => valid with { CommitSha = "" },
            "BuildId" => valid with { BuildId = null! },
            "SourceReference" => valid with { SourceReference = " " },
            "Outcome" => valid with { Outcome = (PublicHistoryBuildOutcome)99 },
            _ => new(valid.Repository, valid.StackLabel, valid.CommitSha, valid.CommitTime,
                valid.BuildId, valid.BuildTime, valid.Outcome, field == "FailingTests" ? null! : [null!], valid.SourceReference)
        };
        var plan = Build([valid, invalid], 1);
        Assert.Empty(plan.Folds);
        Assert.Empty(plan.Unresolved);
        var diagnostic = Assert.Single(plan.Diagnostics);
        Assert.Equal("record-invalid", diagnostic.Code);
        if (field is not "record" and not "test-entry") Assert.Contains(field, diagnostic.Message);
        Assert.Equal("record-invalid", Assert.Single(Build(null, 1).Diagnostics).Code);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(99)]
    public void Validate_TrainingAssignmentTampered_RejectsInvalidMembership(int trainingIndex)
    {
        var plan = Build([Make(0, "a"), Make(1, "b"), Make(2, "c")], 3);
        Assert.Empty(plan.Diagnostics);
        var candidate = plan.Folds.SetItem(0, plan.Folds[0] with { TrainingFoldIndexes = [trainingIndex] });
        Assert.Contains(Validate(candidate), e => e.Code == "training-folds-invalid");
    }

    [Fact]
    public void Validate_MissingEarlierFold_RejectsIncompleteTraining()
    {
        var plan = Build([Make(0, "a"), Make(1, "b"), Make(2, "c")], 3);
        Assert.Empty(plan.Diagnostics);
        var candidate = plan.Folds.SetItem(2, plan.Folds[2] with { TrainingFoldIndexes = [0] });
        var diagnostic = Assert.Single(Validate(candidate));
        Assert.Equal("training-folds-invalid", diagnostic.Code);
        Assert.Equal(2, diagnostic.FoldIndex);
    }

    [Fact]
    public void Validate_EqualBuildInstantsAcrossFolds_RejectsLeak()
    {
        var plan = Build([Make(0, "a"), Make(1, "b")], 2);
        Assert.Empty(plan.Diagnostics);
        Assert.Empty(Validate(plan.Folds));
        var tied = Make(1, "b") with { BuildTime = At(0).ToOffset(TimeSpan.FromHours(2)) };
        var candidate = plan.Folds.SetItem(1, plan.Folds[1] with
        {
            Units = [new PublicHistoryChangeUnit(tied, [])]
        });
        var diagnostic = Assert.Single(Validate(candidate));
        Assert.Equal("training-time-not-earlier", diagnostic.Code);
        Assert.Equal(At(0), diagnostic.BuildTime);
        Assert.Equal(diagnostic.BuildTime, diagnostic.BoundaryTime);
        Assert.Equal("a", diagnostic.BuildId);
        Assert.Equal("b", diagnostic.BoundaryBuildId);
    }

    private static DateTimeOffset At(int hours) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddHours(hours);

    private static PublicHistoryBuild Make(int hour, string id, string repository = "repo-a",
        PublicHistoryBuildOutcome outcome = PublicHistoryBuildOutcome.Passed) =>
        new(repository, "vitest", $"sha-{id}", At(hour), id, At(hour), outcome, [], $"fixture:{id}");
}
