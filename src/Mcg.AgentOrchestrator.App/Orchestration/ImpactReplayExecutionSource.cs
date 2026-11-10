using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A narrow adapter around existing replay I/O, allowing small deterministic cases in observation tests.
internal class ImpactReplayExecutionSource
{
    internal virtual string ReadSourceRevision(string executionDirectory)
    {
        var revision = GitCli.RunForRepository(executionDirectory, "rev-parse", "HEAD");
        return revision.ProcessStarted && revision.Succeeded && !revision.DrainTimedOut
            ? revision.Output.Trim() : "unknown: git HEAD unavailable";
    }

    internal virtual IReadOnlyDictionary<string, GateStatsReport.FlakeRow> LoadHistory(string directory,
        ICollection<ImpactReplayDiagnostic> skipped) => ImpactReplayHistoricalFlakeClassifier.Load(directory, skipped);

    internal virtual IReadOnlyList<AcceptanceLaneFlakeRow> LoadLedger(string executionDirectory) =>
        new AcceptanceLaneFlakeLedger(executionDirectory).Read();

    internal virtual IReadOnlyList<ImpactReplayStoredAttempt> LoadAttempts(string directory, DateTimeOffset? since,
        ICollection<ImpactReplayDiagnostic> skipped, IImpactReplaySink sink, CancellationToken cancellationToken) =>
        ImpactReplayAttemptSource.Read(directory, since, skipped, sink, cancellationToken);

    internal virtual ImpactReplayCaseResult ExecuteCase(ImpactReplayStoredAttempt attempt,
        ImpactReplayRunConfiguration configuration, ImpactReplayCaseContext context,
        IReadOnlyList<AcceptanceLaneFlakeRow> flakeRows, IReadOnlyDictionary<string, GateStatsReport.FlakeRow> history,
        IImpactReplaySink sink, CancellationToken cancellationToken)
    {
        var executionDirectory = configuration.ExecutionDirectory;
        var diff = GitCli.RunForRepository(executionDirectory, "diff", "--name-only", "-z", "--no-renames",
            attempt.MainHeadSha, attempt.BranchHeadSha, "--");
        if (!diff.ProcessStarted || !diff.Succeeded || diff.DrainTimedOut) throw new InvalidDataException($"Git diff failed: exit={diff.ExitCode}; {diff.Error}");
        var changedFiles = diff.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var tempCheckoutParent = configuration.CheckoutParent ?? OrchestratorTempRoot.GetPurposeDirectory("impact-replay-worktrees");
        Directory.CreateDirectory(tempCheckoutParent);
        var checkout = context.Checkout = Path.GetFullPath(Path.Combine(tempCheckoutParent, Guid.NewGuid().ToString("N")));
        sink.Phase(ImpactReplayPhase.Checkout);
        sink.OwnCheckout(checkout);
        cancellationToken.ThrowIfCancellationRequested();
        // Detached checkouts only supply source text. Hooks must not mutate the live repository.
        var added = GitCli.RunForRepository(executionDirectory, "-c", "core.hooksPath=" + Path.Combine(checkout, "no-hooks"),
            "worktree", "add", "--detach", "--quiet", checkout, attempt.BranchHeadSha);
        context.Registered = added.ProcessStarted && added.Succeeded && !added.DrainTimedOut;
        if (!context.Registered) throw new InvalidDataException($"Detached checkout unavailable: exit={added.ExitCode}; {added.Error}");
        sink.Phase(ImpactReplayPhase.SourceAnalysis);
        cancellationToken.ThrowIfCancellationRequested();
        var manifest = AcceptanceManifestLocator.Resolve(checkout);
        if (!File.Exists(manifest)) throw new InvalidDataException("Historical manifest is missing.");
        var settings = AcceptanceGateEngineSettings.Load(checkout);
        var checkNames = settings.DeclaredDotnetTestChecks.Where(check => check.Project is { } project &&
                Path.GetFileName(project.Replace('\\', '/')).Equals("Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", StringComparison.OrdinalIgnoreCase))
            .Select(check => check.Name).ToArray();
        var classes = AcceptanceTestClassSourceScanner.Scan(checkout);
        var testSources = new ImpactReplayTestSourceIndex(LaneTestSourceRootResolver.SourceFiles(checkout,
            LaneTestSourceRootResolver.Resolve(checkout)));
        var guards = SourceReadingGuardTestClasses.Read(testSources, classes);
        context.GuardClasses = guards?.Classes.ToArray();
        var lanes = AcceptanceLaneMembership.ResolveOwnedCollections(settings.InfrastructureTestLanes, classes);
        var poolNames = ImpactReplayBalancedPool.LaneNames(settings.BalancedLanePool);
        sink.Phase(ImpactReplayPhase.EvidenceReading);
        cancellationToken.ThrowIfCancellationRequested();
        var evidence = ImpactReplayTrxEvidence.Read(attempt, lanes, checkNames, classes, poolNames);
        if (evidence.LaneOutcomes.Count == 0 && evidence.BalancedLaneOutcomes.Count == 0)
        {
            return new(ImpactReplayCaseKind.Skipped, Diagnostic: new(attempt.AttemptId, "No Infrastructure.Tests lane evidence."));
        }
        sink.Phase(ImpactReplayPhase.ImpactAnalysis);
        cancellationToken.ThrowIfCancellationRequested();
        var attemptFloor = lanes.Where(lane => configuration.Floor is null ? IsDefaultFloor(lane.Name) : configuration.Floor.Contains(lane.Name, StringComparer.Ordinal))
            .Select(lane => lane.Name).Order(StringComparer.Ordinal).ToArray();
        context.FloorLanes = attemptFloor;
        var routing = new ImpactReplayChangedSourceRouting(checkout);
        var changedSources = changedFiles.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var changedTests = changedSources.Where(routing.IsTestSource).ToArray();
        var productionSources = changedSources.Except(changedTests, StringComparer.OrdinalIgnoreCase).ToArray();
        var selection = productionSources.Length == 0 && changedTests.Length > 0
            ? ReverseDependencyTestSelection.Resolved([])
            : ReverseDependencyTestImpactReader.Read(checkout, productionSources, bypassCache: true,
                maximumChangedSourceFiles: configuration.MaximumChangedSourceFiles);
        var augmented = ImpactReplayChangedTestClassSelection.Read(checkout, changedTests, selection.TestClassNames, routing);
        selection = ImpactReplaySelectionAugmentation.Apply(selection, changedFiles, testSources, classes, guards);
        var selectedLanes = new HashSet<string>(attemptFloor, StringComparer.Ordinal);
        var selectedFullNames = new HashSet<string>(StringComparer.Ordinal);
        var observedPool = ImpactReplayBalancedPool.Read(evidence.BalancedLaneOutcomes, classes, selectedFullNames, false);
        var unmapped = false;
        foreach (var selector in selection.TestClassNames.Concat(augmented.QualifiedClasses).Distinct(StringComparer.Ordinal))
        {
            var matches = classes.Where(item => selector.Contains('.') || selector.Contains('+')
                ? item.FullName == selector : item.FullName.Split('.', '+').Last() == selector).ToArray();
            if (matches.Length == 0) unmapped = true;
            foreach (var item in matches)
            {
                selectedFullNames.Add(item.FullName);
                var memberships = AcceptanceLaneMembership.LanesIncluding(lanes, item.FullName);
                var inPool = observedPool.Classes.Any(record => record.ClassName == item.FullName);
                if (memberships.Count == 0 && !inPool) unmapped = true;
                selectedLanes.UnionWith(memberships.Select(lane => lane.Name));
            }
        }
        var attemptUnmappedClasses = ImpactReplaySelectedClassDiagnostics.Read(attempt.AttemptId,
            checkout, changedTests, augmented.Classes, classes, lanes, observedPool.Classes);
        var resolved = selection.Outcome == ReverseDependencySelectionOutcome.Resolved && !unmapped && augmented.AbandonReason is null;
        var ran = evidence.LaneOutcomes.Select(item => item.Lane).Order(StringComparer.Ordinal).ToArray();
        if (!resolved) selectedLanes.UnionWith(ran);
        var skippedLanes = ran.Where(lane => !selectedLanes.Contains(lane)).ToArray();
        var warnings = evidence.Warnings.ToList();
        if (unmapped) warnings.Add("Selected classes could not all be mapped to lanes; no lanes were skipped.");
        if (augmented.AbandonReason is not null) warnings.Add(augmented.AbandonReason);
        var failures = ImpactReplayRecordedFlakeClassifier.Classify(attempt.AttemptId,
            evidence.Failures.Select(failure => failure with
            { WouldRun = failure.Lane is null ? null : poolNames.Contains(failure.Lane, StringComparer.Ordinal)
                ? !resolved || selectedFullNames.Contains(failure.ClassName) || !classes.Any(c => c.FullName == failure.ClassName)
                : selectedLanes.Contains(failure.Lane) }).ToArray(),
            evidence.LaneOutcomes.Concat(evidence.BalancedLaneOutcomes).ToArray(), checkNames, classes, flakeRows);
        failures = ImpactReplayHistoricalFlakeClassifier.Classify(failures,
            evidence.LaneOutcomes.Concat(evidence.BalancedLaneOutcomes).ToArray(), classes, history);
        var pool = ImpactReplayBalancedPool.Read(evidence.BalancedLaneOutcomes, classes, selectedFullNames, resolved);
        double observedNamed = 0, savedNamed = 0;
        var durationComplete = pool.Complete;
        foreach (var lane in evidence.LaneOutcomes)
        {
            if (lane.TrxPaths.Count == 0) durationComplete = false;
            foreach (var path in lane.TrxPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var duration = ImpactReplayTrxDurations.Read(path, classes);
                durationComplete &= duration.Complete;
                var seconds = duration.Results.Sum(r => r.Seconds ?? 0);
                observedNamed += seconds;
                if (skippedLanes.Contains(lane.Lane, StringComparer.Ordinal)) savedNamed += seconds;
            }
        }
        var classLevel = ImpactReplayClassLevelSavings.Measure(evidence.LaneOutcomes, evidence.BalancedLaneOutcomes,
            attemptFloor, ImpactReplayClassLevelSavings.RetainedClasses(selectedFullNames, guards?.Classes ?? [],
                lanes, attemptFloor, classes), classes, resolved,
            unmapped || augmented.AbandonReason is not null || selection.Outcome == ReverseDependencySelectionOutcome.Abandoned,
            durationComplete);
        var record = new ImpactReplayAttemptRecord(attempt.AttemptId, attempt.GoalId, attempt.StartedAt, attempt.MainHeadSha,
            attempt.BranchHeadSha, unmapped || augmented.AbandonReason is not null ? "Abandoned" : selection.Outcome.ToString(),
            augmented.AbandonReason is not null ? "ChangedTestSupportUnresolved" :
                unmapped ? "UnmappedTestClass" : selection.DegradationKind?.ToString() ?? (resolved ? null : selection.Outcome.ToString()),
            augmented.AbandonReason ?? selection.Reason, changedFiles, augmented.Classes, attemptFloor, ran,
            selectedLanes.Order(StringComparer.Ordinal).ToArray(), skippedLanes,
            lanes.Where(lane => skippedLanes.Contains(lane.Name, StringComparer.Ordinal)).Sum(lane => lane.EstimatedSerialSeconds),
            evidence.LaneOutcomes, failures,
            warnings.Count == 0, warnings, productionSources.Length, pool.Classes, evidence.BalancedLaneOutcomes,
            pool.SecondsSaved, savedNamed, observedNamed + pool.ObservedSeconds, durationComplete,
            evidence.UnresolvedFailures, augmented.OtherFiles,
            ClassLevelSecondsSaved: classLevel.SecondsSaved,
            ClassLevelNonFloorTestSeconds: classLevel.NonFloorTestSeconds,
            ClassLevelUnavailableReason: classLevel.UnavailableReason,
            ClassLevelCpuSecondsSaved: classLevel.CpuSecondsSaved,
            ClassLevelCpuUnavailableReason: classLevel.CpuUnavailableReason);
        return new(ImpactReplayCaseKind.Replayable, record, UnmappedClasses: attemptUnmappedClasses,
            Gained: augmented.Gained, GuardClasses: guards?.Classes.ToArray());
    }

    internal virtual string? Cleanup(string executionDirectory, ImpactReplayCaseContext context)
    {
        if (context.Checkout is not { } checkout) return null;
        // Worktree add may register its path before reporting a pipe-drain or checkout fault.
        var listed = GitCli.RunForRepository(executionDirectory, "worktree", "list", "--porcelain");
        var stillRegistered = listed.Output.Split('\n').Any(line => line.TrimEnd('\r') == "worktree " + checkout.Replace('\\', '/'));
        if (!context.Registered && !stillRegistered && !Directory.Exists(checkout))
            return !listed.ProcessStarted || !listed.Succeeded || listed.DrainTimedOut
                ? $"Impact replay checkout cleanup could not verify registration: path={checkout}" : null;
        var removed = GitCli.RunForRepository(executionDirectory, "worktree", "remove", "--force", checkout);
        return !removed.ProcessStarted || !removed.Succeeded || removed.DrainTimedOut
            ? $"Impact replay checkout cleanup failed: exit={removed.ExitCode}; path={checkout}" : null;
    }

    private static bool IsDefaultFloor(string name) =>
        name.StartsWith("Process spawning", StringComparison.Ordinal) ||
        name.StartsWith("Isolated process spawning", StringComparison.Ordinal) ||
        name is "Chaos gate" or "Remainder merge train and ratchet";
}
