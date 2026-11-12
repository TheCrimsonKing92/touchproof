using System.Runtime.ExceptionServices;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ImpactReplay
{
    internal static ImpactReplayReport Read(string executionDirectory, string orchestratorDirectory,
        DateTimeOffset? since = null, int limit = 100, IReadOnlyList<string>? floor = null,
        string? tempCheckoutParent = null, int? maxChangedSourceFiles = null,
        IImpactReplaySink? observation = null, CancellationToken cancellationToken = default,
        ImpactReplayExecutionSource? source = null, ImpactReplayHistoryQuery? historyQuery = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChangedSourceFiles ?? 5, 1);
        var sink = observation ?? ImpactReplayNullSink.Instance;
        var configuration = new ImpactReplayRunConfiguration(executionDirectory, orchestratorDirectory,
            since, limit, floor, maxChangedSourceFiles ?? 5, tempCheckoutParent);
        var loadingDiagnostics = new ImpactReplayDiagnostics(sink);
        var skipped = new List<ImpactReplayDiagnostic>();
        var unreplayable = new List<ImpactReplayDiagnostic>();
        var cleanupErrors = new List<string>();
        var records = new List<ImpactReplayAttemptRecord>();
        var unmappedClasses = new List<ImpactReplayUnmappedTestClasses.Occurrence>();
        var gainedAttempts = 0;
        var guardClasses = new SortedSet<string>(StringComparer.Ordinal);
        var guardHelpersPresent = false;
        var flakeRows = new Lazy<IReadOnlyList<AcceptanceLaneFlakeRow>>(() =>
        {
            sink.Phase(ImpactReplayPhase.LedgerLoading);
            cancellationToken.ThrowIfCancellationRequested();
            try { return source!.LoadLedger(executionDirectory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var diagnostic = new ImpactReplayDiagnostic("lane-flakes.jsonl", $"Flake evidence could not be read: {ex.Message}");
                skipped.Add(diagnostic);
                sink.Diagnostic(diagnostic);
                return [];
            }
        });
        var historyReader = new ImpactReplayHistoricalEvidenceReader(() => flakeRows.Value);
        historyQuery ??= new(historyReader.Read);
        // The CLI supplies the base adapter to read the runtime revision; keep custom observation sources.
        if (source is null || source.GetType() == typeof(ImpactReplayExecutionSource))
            source = new ScopedHistoryExecutionSource(flakeRows, historyQuery);
        sink.Phase(ImpactReplayPhase.HistoryLoading);
        cancellationToken.ThrowIfCancellationRequested();
        var history = source.LoadHistory(orchestratorDirectory, loadingDiagnostics);
        var floorNames = new SortedSet<string>(floor ?? [], StringComparer.Ordinal);
        sink.Phase(ImpactReplayPhase.InventoryLoading);
        cancellationToken.ThrowIfCancellationRequested();
        var attempts = source.LoadAttempts(orchestratorDirectory, since, loadingDiagnostics, sink, cancellationToken);
        skipped.AddRange(loadingDiagnostics);
        sink.InventorySelected(attempts.Count);
        var ordinal = 0;
        foreach (var attempt in attempts)
        {
            // Skipped cases do not consume the historical replay limit.
            if (records.Count + unreplayable.Count >= limit) break;
            cancellationToken.ThrowIfCancellationRequested();
            sink.BeginCase(attempt, ++ordinal);
            var context = new ImpactReplayCaseContext();
            Exception? failure = null;
            try
            {
                ImpactReplayCaseResult result;
                Exception? inputFailure = null;
                try { result = source.ExecuteCase(attempt, configuration, context,
                    source is ScopedHistoryExecutionSource ? [] : flakeRows.Value, history, sink, cancellationToken); }
                catch (Exception ex) when (IsRecoverableInput(ex))
                {
                    inputFailure = ex;
                    result = new(ImpactReplayCaseKind.Unreplayable, Diagnostic: new(attempt.AttemptId, ex.Message));
                }
                // Durability precedes cleanup and the next case. Sink faults never become input diagnostics.
                try { sink.CompleteCase(result); }
                catch (Exception ex) when (inputFailure is not null)
                { throw new AggregateException(inputFailure, ex); }
                if (result.Record is { } record)
                {
                    records.Add(record);
                    floorNames.UnionWith(record.FloorLanes);
                }
                if (result.Kind == ImpactReplayCaseKind.Skipped) skipped.Add(result.Diagnostic!);
                if (result.Kind == ImpactReplayCaseKind.Unreplayable) unreplayable.Add(result.Diagnostic!);
                unmappedClasses.AddRange(result.UnmappedClasses ?? []);
                if (result.Gained) gainedAttempts++;
                floorNames.UnionWith(context.FloorLanes);
                if ((context.GuardClasses ?? result.GuardClasses) is { } guards)
                {
                    guardHelpersPresent = true;
                    guardClasses.UnionWith(guards);
                }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                // Observation failures must never prevent owned checkout cleanup, nor replace the primary error.
                try { sink.Phase(ImpactReplayPhase.Cleanup); }
                catch (Exception ex) { failure = Combine(failure, ex); }
                string? cleanupError;
                try { cleanupError = source.Cleanup(executionDirectory, context); }
                catch (Exception ex) { cleanupError = ex.Message; failure = Combine(failure, ex); }
                if (cleanupError is not null) cleanupErrors.Add(cleanupError);
                if (context.Checkout is { } checkout)
                {
                    try { sink.CheckoutCleaned(checkout, cleanupError is null ? "succeeded" : "failed", cleanupError); }
                    catch (Exception ex) { failure = Combine(failure, ex); }
                }
                if (failure is not null && cleanupError is not null)
                    failure = Combine(failure, new IOException(cleanupError));
            }
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(floor is null ? "default" : "override", floorNames.ToArray(), records, skipped, unreplayable, cleanupErrors, gainedAttempts,
            ImpactReplayUnmappedTestClasses.Aggregate(unmappedClasses),
            maxChangedSourceFiles is not null || records.Any(r => r.BalancedLaneOutcomes?.Count > 0)
                ? ImpactReplayMeasurement.Create(maxChangedSourceFiles ?? 5, records) : null,
            guardHelpersPresent ? new ImpactReplaySourceGuardReport(guardClasses.ToArray()) : null,
            new(historyQuery.QueriedIdentities, historyReader.MetadataReads, historyReader.ParsedPaths.Count,
                historyReader.ParsedPaths));
    }

    private static bool IsRecoverableInput(Exception ex) => ex is IOException or UnauthorizedAccessException or
        JsonException or System.Xml.XmlException or ArgumentException or InvalidOperationException or InvalidDataException;

    private static Exception Combine(Exception? primary, Exception secondary) =>
        primary is null ? secondary : new AggregateException(primary, secondary);

    // Keep scoped adjudication in this merge-owned seam while using the shared observation lifecycle.
    private sealed class ScopedHistoryExecutionSource(
        Lazy<IReadOnlyList<AcceptanceLaneFlakeRow>> ledger, ImpactReplayHistoryQuery query) : ImpactReplayExecutionSource
    {
        // History is acquired by exact identity inside the selected case, never during startup.
        internal override IReadOnlyDictionary<string, GateStatsReport.FlakeRow> LoadHistory(string directory,
            ICollection<ImpactReplayDiagnostic> skipped) => new Dictionary<string, GateStatsReport.FlakeRow>();

        internal override ImpactReplayCaseResult ExecuteCase(ImpactReplayStoredAttempt attempt,
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
            var routing = new ImpactReplayChangedSourceRouting(checkout);
            var testSources = new ImpactReplayTestSourceIndex(LaneTestSourceRootResolver.SourceFiles(checkout,
                LaneTestSourceRootResolver.Resolve(checkout)), routing.TestProjects, checkout);
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
            var changedSources = changedFiles.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var changedTests = changedSources.Where(routing.IsTestSource).ToArray();
            var productionSources = changedSources.Except(changedTests, StringComparer.OrdinalIgnoreCase).ToArray();
            var selection = productionSources.Length == 0 && changedTests.Length > 0
                ? ReverseDependencyTestSelection.Resolved([])
                : ReverseDependencyTestImpactReader.Read(checkout, productionSources, bypassCache: true);
            var augmented = ImpactReplayChangedTestClassSelection.Read(checkout, changedTests, selection.TestClassNames, routing);
            selection = ImpactReplaySelectionAugmentation.Apply(selection, changedFiles, testSources, classes, guards,
                out var declaredGuards, augmented.QualifiedClasses);
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
                evidence.LaneOutcomes.Concat(evidence.BalancedLaneOutcomes).ToArray(), checkNames, classes,
                evidence.Failures.Any(failure => failure.Lane is not null) ? ledger.Value : [], warnings);
            if (failures.Any(failure => failure.IsMiss && !failure.ConfirmedHardFailure))
            {
                sink.Phase(ImpactReplayPhase.HistoryLoading);
                cancellationToken.ThrowIfCancellationRequested();
            }
            failures = query.Classify(failures, evidence.FailingIdentities, warnings);
            sink.Phase(ImpactReplayPhase.ImpactAnalysis);
            cancellationToken.ThrowIfCancellationRequested();
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
                ClassLevelCpuUnavailableReason: classLevel.CpuUnavailableReason,
                DeclaredGuards: declaredGuards);
            record = ImpactReplayUnboundedSelectionMeasurement.Apply(record, checkout, productionSources,
                testSources, classes, guards, augmented.QualifiedClasses);
            return new(ImpactReplayCaseKind.Replayable, record, UnmappedClasses: attemptUnmappedClasses,
                Gained: augmented.Gained, GuardClasses: guards?.Classes.ToArray());
        }

        private static bool IsDefaultFloor(string name) =>
            name.StartsWith("Process spawning", StringComparison.Ordinal) ||
            name.StartsWith("Isolated process spawning", StringComparison.Ordinal) ||
            name is "Chaos gate" or "Remainder merge train and ratchet";
    }
}
