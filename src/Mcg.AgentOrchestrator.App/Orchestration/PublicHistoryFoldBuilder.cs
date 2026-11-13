using System.Collections.Immutable;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Offline canonical roll-up, contiguous partitioning, and training-plan validation.</summary>
internal static class PublicHistoryFoldBuilder
{
    internal sealed record Fold(int Index, ImmutableArray<PublicHistoryChangeUnit> Units,
        ImmutableArray<int> TrainingFoldIndexes)
    {
        public DateTimeOffset EarliestBuildTime => Units.Min(u => u.TerminalBuild.BuildTime).ToUniversalTime();
        public DateTimeOffset LatestBuildTime => Units.Max(u => u.TerminalBuild.BuildTime).ToUniversalTime();
    }

    internal sealed record Unresolved(string Repository, ImmutableArray<PublicHistoryBuild> Builds)
    {
        public ImmutableArray<string> Commits => Builds.Select(b => b.CommitSha).ToImmutableArray();
    }

    internal sealed record Diagnostic(string Code, string Message, string? BuildId = null,
        string? Repository = null, int? FoldIndex = null, int? EarlierFoldIndex = null,
        DateTimeOffset? BuildTime = null, DateTimeOffset? BoundaryTime = null,
        string? BoundaryBuildId = null);

    internal sealed record Plan(ImmutableArray<Fold> Folds, ImmutableArray<Unresolved> Unresolved,
        ImmutableArray<Diagnostic> Diagnostics);

    internal static Plan Build(IReadOnlyList<PublicHistoryBuild>? builds, int foldCount)
    {
        if (builds is null)
            return new([], [], [new("record-invalid", "Build list is null.")]);
        var ordered = builds.ToArray();
        var diagnostics = ordered.SelectMany(ValidateRecord).ToList();
        if (diagnostics.Count > 0) return new([], [], SortDiagnostics(diagnostics));
        Array.Sort(ordered, CompareBuilds);

        var pending = new Dictionary<string, List<PublicHistoryBuild>>(StringComparer.Ordinal);
        var units = new List<PublicHistoryChangeUnit>();
        foreach (var build in ordered)
        {
            if (!pending.TryGetValue(build.Repository, out var buffer))
                pending.Add(build.Repository, buffer = []);
            if (build.Outcome is PublicHistoryBuildOutcome.Skipped or PublicHistoryBuildOutcome.Cancelled)
                buffer.Add(build);
            else
            {
                units.Add(new(build, buffer));
                buffer.Clear();
            }
        }
        var unresolved = pending.Where(p => p.Value.Count > 0).OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new Unresolved(p.Key, p.Value.ToImmutableArray())).ToImmutableArray();
        if (foldCount < 1)
            return Refuse("fold-count-invalid", $"Requested {foldCount} folds for {units.Count} resolved units; count must be positive.");
        if (units.Count == 0) return new([], unresolved, []);
        if (foldCount > units.Count)
            return Refuse("fold-count-exceeds-units", $"Requested {foldCount} folds for {units.Count} resolved units.");

        // Units already follow their terminal build's canonical key. Equal UTC build instants
        // form indivisible consecutive groups; regressions across folds are rejected below.
        var ends = new List<int>();
        for (var i = 1; i <= units.Count; i++)
            if (i == units.Count || units[i - 1].TerminalBuild.BuildTime != units[i].TerminalBuild.BuildTime)
                ends.Add(i);
        if (foldCount > ends.Count)
            return Refuse("fold-count-exceeds-time-groups", $"Requested {foldCount} folds for {ends.Count} available time groups.");

        var folds = new List<Fold>();
        var previousGroup = 0;
        var start = 0;
        var idealCut = 0;
        for (var index = 0; index < foldCount; index++)
        {
            idealCut += units.Count / foldCount + (index < units.Count % foldCount ? 1 : 0);
            var groupCut = ends.FindIndex(end => end >= idealCut) + 1;
            // Reserve one whole group for each remaining fold, even when advancing a cut
            // over a large equal-time group would otherwise consume the entire tail.
            groupCut = Math.Clamp(groupCut, previousGroup + 1, ends.Count - (foldCount - index - 1));
            var end = ends[groupCut - 1];
            folds.Add(new(index, units.GetRange(start, end - start).ToImmutableArray(),
                Enumerable.Range(0, index).ToImmutableArray()));
            start = end;
            previousGroup = groupCut;
        }
        var errors = Validate(folds);
        return new(errors.IsEmpty ? folds.ToImmutableArray() : [], unresolved, errors);

        Plan Refuse(string code, string message) => new([], unresolved, [new(code, message)]);
    }

    /// <summary>Checks caller-supplied training assignments against the terminal observations.</summary>
    internal static ImmutableArray<Diagnostic> Validate(IReadOnlyList<Fold>? folds)
    {
        if (folds is null) return [new("fold-invalid", "Fold list is null.")];
        var errors = new List<Diagnostic>();
        for (var i = 0; i < folds.Count; i++)
        {
            var fold = folds[i];
            if (fold is null || fold.Index != i || fold.Units.IsDefaultOrEmpty ||
                fold.TrainingFoldIndexes.IsDefault || fold.Units.Any(u => u is null || u.TerminalBuild is null))
                errors.Add(new("fold-invalid", "Folds must have consecutive indexes, units and training lists.", FoldIndex: i));
        }
        if (errors.Count > 0) return SortDiagnostics(errors);

        foreach (var fold in folds)
        {
            if (!fold.TrainingFoldIndexes.SequenceEqual(Enumerable.Range(0, fold.Index)))
                errors.Add(new("training-folds-invalid", "Training must list every preceding fold exactly once, in order.",
                    FoldIndex: fold.Index));
            foreach (var trainingIndex in fold.TrainingFoldIndexes)
            {
                if (trainingIndex < 0 || trainingIndex >= folds.Count)
                {
                    errors.Add(new("training-fold-missing", "Training references an absent fold.",
                        FoldIndex: fold.Index, EarlierFoldIndex: trainingIndex));
                    continue;
                }
                var training = folds[trainingIndex];
                var latest = training.Units.MaxBy(u => u.TerminalBuild.BuildTime)!.TerminalBuild;
                if (trainingIndex >= fold.Index)
                    errors.Add(new("training-not-earlier-fold", "Training references the evaluated fold or a later fold.",
                        latest.BuildId, latest.Repository, fold.Index, trainingIndex,
                        latest.BuildTime.ToUniversalTime(), fold.EarliestBuildTime));
                foreach (var unit in fold.Units)
                {
                    var evaluated = unit.TerminalBuild;
                    if (latest.BuildTime >= evaluated.BuildTime)
                        errors.Add(new("training-time-not-earlier", "Training build time must be strictly earlier than the evaluated build.",
                            latest.BuildId, latest.Repository, fold.Index, trainingIndex,
                            latest.BuildTime.ToUniversalTime(), evaluated.BuildTime.ToUniversalTime(), evaluated.BuildId));
                }
            }
        }
        return SortDiagnostics(errors);
    }

    private static IEnumerable<Diagnostic> ValidateRecord(PublicHistoryBuild? build)
    {
        if (build is null)
        {
            yield return new("record-invalid", "Build record is null.");
            yield break;
        }
        var invalid = new List<string>();
        if (string.IsNullOrWhiteSpace(build.Repository)) invalid.Add(nameof(build.Repository));
        if (string.IsNullOrWhiteSpace(build.StackLabel)) invalid.Add(nameof(build.StackLabel));
        if (string.IsNullOrWhiteSpace(build.CommitSha)) invalid.Add(nameof(build.CommitSha));
        if (string.IsNullOrWhiteSpace(build.BuildId)) invalid.Add(nameof(build.BuildId));
        if (string.IsNullOrWhiteSpace(build.SourceReference)) invalid.Add(nameof(build.SourceReference));
        if (build.FailingTests is null || build.FailingTests.Any(t => t is null)) invalid.Add(nameof(build.FailingTests));
        if (!Enum.IsDefined(build.Outcome)) invalid.Add(nameof(build.Outcome));
        if (invalid.Count > 0)
            yield return new("record-invalid", $"Invalid build fields: {string.Join(", ", invalid)}.", build.BuildId, build.Repository);
    }

    private static int CompareBuilds(PublicHistoryBuild left, PublicHistoryBuild right)
    {
        int[] keys = [left.CommitTime.CompareTo(right.CommitTime), left.BuildTime.CompareTo(right.BuildTime),
            StringComparer.Ordinal.Compare(left.BuildId, right.BuildId),
            StringComparer.Ordinal.Compare(left.Repository, right.Repository),
            StringComparer.Ordinal.Compare(left.CommitSha, right.CommitSha),
            StringComparer.Ordinal.Compare(left.StackLabel, right.StackLabel), left.Outcome.CompareTo(right.Outcome),
            StringComparer.Ordinal.Compare(left.SourceReference, right.SourceReference)];
        foreach (var key in keys) if (key != 0) return key;
        for (var i = 0; i < Math.Min(left.FailingTests.Count, right.FailingTests.Count); i++)
        {
            var comparison = StringComparer.Ordinal.Compare(left.FailingTests[i], right.FailingTests[i]);
            if (comparison != 0) return comparison;
        }
        var count = left.FailingTests.Count.CompareTo(right.FailingTests.Count);
        if (count != 0) return count;
        var commitOffset = left.CommitTime.Offset.CompareTo(right.CommitTime.Offset);
        return commitOffset != 0 ? commitOffset : left.BuildTime.Offset.CompareTo(right.BuildTime.Offset);
    }

    private static ImmutableArray<Diagnostic> SortDiagnostics(IEnumerable<Diagnostic> diagnostics) => diagnostics
        .OrderBy(d => d.Code, StringComparer.Ordinal).ThenBy(d => d.Repository, StringComparer.Ordinal)
        .ThenBy(d => d.BuildId, StringComparer.Ordinal).ThenBy(d => d.FoldIndex).ThenBy(d => d.EarlierFoldIndex)
        .ThenBy(d => d.BuildTime).ThenBy(d => d.BoundaryTime).ThenBy(d => d.BoundaryBuildId, StringComparer.Ordinal)
        .ThenBy(d => d.Message, StringComparer.Ordinal).ToImmutableArray();
}
