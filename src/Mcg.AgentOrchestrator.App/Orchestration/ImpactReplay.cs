using System.Runtime.ExceptionServices;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ImpactReplay
{
    internal static ImpactReplayReport Read(string executionDirectory, string orchestratorDirectory,
        DateTimeOffset? since = null, int limit = 100, IReadOnlyList<string>? floor = null,
        string? tempCheckoutParent = null, int? maxChangedSourceFiles = null,
        IImpactReplaySink? observation = null, CancellationToken cancellationToken = default,
        ImpactReplayExecutionSource? source = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChangedSourceFiles ?? 5, 1);
        var sink = observation ?? ImpactReplayNullSink.Instance;
        source ??= new ImpactReplayExecutionSource();
        var configuration = new ImpactReplayRunConfiguration(executionDirectory, orchestratorDirectory,
            since, limit, floor, maxChangedSourceFiles ?? 5, tempCheckoutParent);
        var loadingDiagnostics = new ImpactReplayDiagnostics(sink);
        var skipped = new List<ImpactReplayDiagnostic>();
        var unreplayable = new List<ImpactReplayDiagnostic>();
        var cleanupErrors = new List<string>();
        var records = new List<ImpactReplayAttemptRecord>();
        var unmappedClasses = new List<ImpactReplayUnmappedTestClasses.Occurrence>();
        var gainedAttempts = 0;
        sink.Phase(ImpactReplayPhase.HistoryLoading);
        cancellationToken.ThrowIfCancellationRequested();
        var history = source.LoadHistory(orchestratorDirectory, loadingDiagnostics);
        var guardClasses = new SortedSet<string>(StringComparer.Ordinal);
        var guardHelpersPresent = false;
        IReadOnlyList<AcceptanceLaneFlakeRow> flakeRows;
        sink.Phase(ImpactReplayPhase.LedgerLoading);
        cancellationToken.ThrowIfCancellationRequested();
        try { flakeRows = source.LoadLedger(executionDirectory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            flakeRows = [];
            loadingDiagnostics.Add(new("lane-flakes.jsonl", $"Flake evidence could not be read: {ex.Message}"));
        }
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
                try { result = source.ExecuteCase(attempt, configuration, context, flakeRows, history, sink, cancellationToken); }
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
            guardHelpersPresent ? new ImpactReplaySourceGuardReport(guardClasses.ToArray()) : null);
    }

    private static bool IsRecoverableInput(Exception ex) => ex is IOException or UnauthorizedAccessException or
        JsonException or System.Xml.XmlException or ArgumentException or InvalidOperationException or InvalidDataException;

    private static Exception Combine(Exception? primary, Exception secondary) =>
        primary is null ? secondary : new AggregateException(primary, secondary);
}
